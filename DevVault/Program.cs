using DevVault.Data;
using DevVault.DTOs;
using DevVault.Models;
using DevVault.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. SERVICE REGISTRATION (The Container)
// ==========================================

// OpenAPI / Swagger
builder.Services.AddOpenApi();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// External Services
builder.Services.AddHttpClient<IGeminiService, GeminiService>();

// Health Checks (For Render to monitor uptime)
builder.Services.AddHealthChecks();

// Global Exception Handling (Standardizes errors as JSON instead of HTML crashes)
builder.Services.AddProblemDetails();

// CORS Policy
var frontendUrl = builder.Configuration["Cors:FrontendUrl"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        if (!string.IsNullOrEmpty(frontendUrl))
        {
            policy.WithOrigins(frontendUrl)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// Authentication & OAuth
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/api/auth/login";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
})
.AddGitHub("GitHub", options =>
{
    options.ClientId = builder.Configuration["GitHub:ClientId"]!;
    options.ClientSecret = builder.Configuration["GitHub:ClientSecret"]!;
    options.CallbackPath = "/signin-github";
    options.Scope.Add("user:email");
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

// ==========================================
// 2. MIDDLEWARE PIPELINE (Order is Critical!)
// ==========================================

// A. Cloud Proxy Headers (Must be first for Render HTTPS detection)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
});

// B. Global Exception Handler (Catches all crashes and formats them nicely)
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// C. Security & Routing
app.UseCors("AngularDev"); // CORS must happen before Auth and Routing
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ==========================================
// 3. ENDPOINTS
// ==========================================

app.MapGet("/", () => "DevVault API Engine is Running!");

// Health Check Endpoint
app.MapHealthChecks("/api/health");

// --- AI Orchestration ---
app.MapPost("/api/generate", async (GenerateReleaseRequest request, IGeminiService ai) =>
{
    // Basic validation to prevent null reference exceptions
    if (string.IsNullOrWhiteSpace(request.RawGitDiff))
    {
        return Results.BadRequest("Git diff cannot be empty.");
    }

    var result = await ai.AnalyzeGitDiffAsync(request.RawGitDiff);

    return result is not null
        ? Results.Ok(result)
        : Results.Problem("AI generation failed to return a valid response.", statusCode: 500);
})
.WithName("GenerateReleaseNotes");


// --- Career Vault ---
app.MapPost("/api/vault", async (SaveReleaseRequest request, AppDbContext db, ClaimsPrincipal userToken) =>
{
    var realUserIdStr = userToken.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (!Guid.TryParse(realUserIdStr, out var realUserId))
        return Results.Unauthorized();

    var release = new SavedRelease
    {
        Id = Guid.NewGuid(),
        UserId = realUserId,
        Title = request.Title ?? "Untitled Release",
        RawInput = request.RawInput ?? "",
        MarkdownNotes = request.MarkdownNotes ?? "",
        SocialPost = request.SocialPost ?? "",
        SemanticVersion = request.SemanticVersion ?? "Patch",
        CreatedAt = DateTime.UtcNow
    };

    db.SavedReleases.Add(release);
    await db.SaveChangesAsync();
    return Results.Created($"/api/vault/{release.Id}", release);
})
.RequireAuthorization();

app.MapGet("/api/vault/history", async (AppDbContext db, ClaimsPrincipal userToken) =>
{
    var realUserIdStr = userToken.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (!Guid.TryParse(realUserIdStr, out var realUserId))
        return Results.Unauthorized();

    var history = await db.SavedReleases
        .Where(r => r.UserId == realUserId)
        .OrderByDescending(r => r.CreatedAt)
        .ToListAsync();

    return Results.Ok(history);
})
.RequireAuthorization();

app.MapGet("/api/vault/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var release = await db.SavedReleases.FindAsync(id);

    return release is not null
        ? Results.Ok(release)
        : Results.NotFound();
})
.WithName("GetReleaseDetail");


// --- Authentication ---
app.MapGet("/api/auth/login", () =>
{
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/api/auth/callback" },
        new[] { "GitHub" }
    );
});

app.MapGet("/api/auth/callback", async (HttpContext context, AppDbContext db) =>
{
    var frontendUrl = builder.Configuration["Cors:FrontendUrl"]?.TrimEnd('/');

    var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    if (!result.Succeeded)
        return Results.Redirect($"{frontendUrl}/?error=login_failed");

    var githubId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var username = result.Principal.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
    var email = result.Principal.FindFirst(ClaimTypes.Email)?.Value ?? "";

    if (string.IsNullOrEmpty(githubId))
        return Results.Redirect($"{frontendUrl}/?error=missing_github_id");

    var user = db.Users.FirstOrDefault(u => u.Username == username);

    if (user == null)
    {
        user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            GitHubId = githubId,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!));
    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim("username", user.Username)
    };

    var token = new JwtSecurityToken(
        issuer: builder.Configuration["Jwt:Issuer"],
        audience: builder.Configuration["Jwt:Audience"],
        claims: claims,
        expires: DateTime.Now.AddDays(7),
        signingCredentials: credentials);

    var jwtString = new JwtSecurityTokenHandler().WriteToken(token);

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    return Results.Redirect($"{frontendUrl}/dashboard?token={jwtString}");
});

app.Run();
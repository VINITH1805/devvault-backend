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

// Add services to the container.

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpClient<IGeminiService, GeminiService>();

// Add CORS Policy
var frontendUrl = builder.Configuration["Cors:FrontendUrl"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        // Safety check: Ensure the URL isn't null before adding it
        if (!string.IsNullOrEmpty(frontendUrl))
        {
            policy.WithOrigins(frontendUrl)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// --- AUTHENTICATION PIPELINE ---
builder.Services.AddAuthentication(options =>
{
    // We use a temporary cookie to hold the state while GitHub redirects back
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.LoginPath = "/api/auth/login";

    // ADD THIS EVENT HANDLER:
    // This stops the backend from redirecting API calls to GitHub!
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = 401; // Just return Unauthorized
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

    // We want their email so we can save it in our database!
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

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AngularDev");

app.UseHttpsRedirection();

app.MapGet("/", () => "DevVault API Engine is Running! ");


// ==========================================
// THE AI ORCHESTRATION ENDPOINT
// ==========================================
app.MapPost("/api/generate", async (GenerateReleaseRequest request, IGeminiService ai) =>
{
    var result = await ai.AnalyzeGitDiffAsync(request.RawGitDiff);

    return result is not null
        ? Results.Ok(result)
        : Results.BadRequest("AI generation failed or returned empty.");
})
.WithName("GenerateReleaseNotes");

// ==========================================
// THE CAREER VAULT ENDPOINTS
// ==========================================

// 1. SAVE to Vault
app.MapPost("/api/vault", async (SaveReleaseRequest request, AppDbContext db, ClaimsPrincipal userToken) =>
{
    // Extract the real Database ID from the JWT
    var realUserIdStr = userToken.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (!Guid.TryParse(realUserIdStr, out var realUserId))
        return Results.Unauthorized();

    // Now save to the DB using their REAL ID instead of request.UserId
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
.RequireAuthorization(); // <--- This endpoint now demands a VIP Pass!


// 2. FETCH Vault History (For a specific developer)
app.MapGet("/api/vault/history", async (AppDbContext db, ClaimsPrincipal userToken) =>
{
    // 1. Extract the user's real ID from their JWT Token
    var realUserIdStr = userToken.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    if (!Guid.TryParse(realUserIdStr, out var realUserId))
        return Results.Unauthorized();

    // 2. Fetch only their records, sorting by newest first!
    var history = await db.SavedReleases
        .Where(r => r.UserId == realUserId)
        .OrderByDescending(r => r.CreatedAt)
        .ToListAsync();

    return Results.Ok(history);
})
.RequireAuthorization(); // Requires the JWT VIP Pass!


// 3. FETCH Specific Release Detail (When they click a row in the UI)
app.MapGet("/api/vault/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var release = await db.SavedReleases.FindAsync(id);

    return release is not null
        ? Results.Ok(release)
        : Results.NotFound();
})
.WithName("GetReleaseDetail");

// 1. Trigger the GitHub Login Screen
app.MapGet("/api/auth/login", () =>
{
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/api/auth/callback" },
        new[] { "GitHub" }
    );
});

// 2. The Callback (Where GitHub sends the user back after they approve your app)
// 2. The Callback (Where GitHub sends the user back after they approve your app)
app.MapGet("/api/auth/callback", async (HttpContext context, AppDbContext db) =>
{
    // Grab the dynamic frontend URL from the environment (Netlify in production, Localhost in dev)
    var frontendUrl = builder.Configuration["Cors:FrontendUrl"]?.TrimEnd('/');

    var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    if (!result.Succeeded)
        return Results.Redirect($"{frontendUrl}/?error=login_failed");

    // 1. Extract GitHub Data
    var githubId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var username = result.Principal.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
    var email = result.Principal.FindFirst(ClaimTypes.Email)?.Value ?? "";

    if (string.IsNullOrEmpty(githubId))
        return Results.Redirect($"{frontendUrl}/?error=missing_github_id");

    // 2. Database Sync: Does this user exist?
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

    // 3. Mint the JWT VIP Pass
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

    // Clean up the temporary cookie
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    // 4. Send them back to Angular using the dynamic URL!
    return Results.Redirect($"{frontendUrl}/dashboard?token={jwtString}");
});

app.UseAuthentication(); 

app.UseAuthorization();

app.Run();

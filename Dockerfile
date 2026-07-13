# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["DevVault.csproj", "./"]
RUN dotnet restore "./DevVault.csproj"

# Copy the rest of the code and build
COPY . .
RUN dotnet publish "DevVault.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Run the application
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

# Render dynamically assigns a port, this ensures .NET listens to it
ENV ASPNETCORE_URLS=http://+:8080 

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DevVault.dll"]
# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MedulaPrecheck.Core/MedulaPrecheck.Core.csproj src/MedulaPrecheck.Core/
COPY src/MedulaPrecheck.Oracle/MedulaPrecheck.Oracle.csproj src/MedulaPrecheck.Oracle/
COPY src/MedulaPrecheck.Api/MedulaPrecheck.Api.csproj src/MedulaPrecheck.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/MedulaPrecheck.Api/MedulaPrecheck.Api.csproj

COPY .editorconfig ./
COPY db/ db/
COPY src/MedulaPrecheck.Core/ src/MedulaPrecheck.Core/
COPY src/MedulaPrecheck.Oracle/ src/MedulaPrecheck.Oracle/
COPY src/MedulaPrecheck.Api/ src/MedulaPrecheck.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/MedulaPrecheck.Api/MedulaPrecheck.Api.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="medula-precheck-api" \
      org.opencontainers.image.source="https://github.com/hekimcan206/medula-precheck" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MedulaPrecheck.Api.dll"]

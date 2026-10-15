# OpenHandsNET — build multi-stage (app em :8080, compose expoe :3032)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (camada cacheada): props + todos os csproj
COPY Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/OpenHandsNET.Domain/OpenHandsNET.Domain.csproj src/OpenHandsNET.Domain/
COPY src/OpenHandsNET.Application/OpenHandsNET.Application.csproj src/OpenHandsNET.Application/
COPY src/OpenHandsNET.Infrastructure/OpenHandsNET.Infrastructure.csproj src/OpenHandsNET.Infrastructure/
COPY src/OpenHandsNET.Sandboxing/OpenHandsNET.Sandboxing.csproj src/OpenHandsNET.Sandboxing/
COPY src/OpenHandsNET.RuntimeApi/OpenHandsNET.RuntimeApi.csproj src/OpenHandsNET.RuntimeApi/
COPY src/OpenHandsNET.Client/OpenHandsNET.Client.csproj src/OpenHandsNET.Client/
COPY src/OpenHandsNET.Api/OpenHandsNET.Api.csproj src/OpenHandsNET.Api/
COPY cli/OpenHandsNET.Cli/OpenHandsNET.Cli.csproj cli/OpenHandsNET.Cli/
RUN dotnet restore src/OpenHandsNET.Api/OpenHandsNET.Api.csproj

COPY src/ src/
COPY cli/ cli/
RUN dotnet publish src/OpenHandsNET.Api/OpenHandsNET.Api.csproj \
      -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Volume persistente: SQLite (data/) + workspace dos agentes.
# SPEC-18: Chromium headless do browser tool — o driver Node do Playwright
# vai bundled no publish (.playwright/), --with-deps instala as libs do SO.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    OPENHANDSNET_Database__ConnectionString="Data Source=/app/data/openhandsnet.db"
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    /app/.playwright/node/*/node /app/.playwright/package/cli.js \
      install --with-deps chromium && \
    apt-get clean && rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app/data /workspace && \
    groupadd -r openhandsnet && useradd -r -g openhandsnet openhandsnet && \
    chown -R openhandsnet:openhandsnet /app /workspace /ms-playwright
USER openhandsnet

EXPOSE 8080
VOLUME ["/app/data", "/workspace"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/api/v1/ready || exit 1

ENTRYPOINT ["dotnet", "OpenHandsNET.Api.dll"]

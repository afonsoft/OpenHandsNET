# Sandcastle — build multi-stage (app em :8080, compose expoe :3032)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (camada cacheada): props + todos os csproj
COPY Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/Sandcastle.Domain/Sandcastle.Domain.csproj src/Sandcastle.Domain/
COPY src/Sandcastle.Application/Sandcastle.Application.csproj src/Sandcastle.Application/
COPY src/Sandcastle.Infrastructure/Sandcastle.Infrastructure.csproj src/Sandcastle.Infrastructure/
COPY src/Sandcastle.Sandboxing/Sandcastle.Sandboxing.csproj src/Sandcastle.Sandboxing/
COPY src/Sandcastle.RuntimeApi/Sandcastle.RuntimeApi.csproj src/Sandcastle.RuntimeApi/
COPY src/Sandcastle.Client/Sandcastle.Client.csproj src/Sandcastle.Client/
COPY src/Sandcastle.Api/Sandcastle.Api.csproj src/Sandcastle.Api/
COPY cli/Sandcastle.Cli/Sandcastle.Cli.csproj cli/Sandcastle.Cli/
RUN dotnet restore src/Sandcastle.Api/Sandcastle.Api.csproj

COPY src/ src/
COPY cli/ cli/
# SPEC-152: swe-lite subset — EmbeddedResource da Infrastructure
# (referência ../../bench/swe-lite.jsonl no csproj).
COPY bench/ bench/
RUN dotnet publish src/Sandcastle.Api/Sandcastle.Api.csproj \
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
    SANDCASTLE_Database__ConnectionString="Data Source=/app/data/sandcastle.db"
# SPEC-131: servidores MCP stdio rodam via `docker exec -i` no container
# do sandbox — a imagem do app precisa do cliente docker (só o binário;
# o engine é acessado pelo socket montado, via Docker.DotNet ou CLI).
ARG DOCKER_CLI_VERSION=27.5.1
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    /app/.playwright/node/*/node /app/.playwright/package/cli.js \
      install --with-deps chromium && \
    curl -fsSL "https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_CLI_VERSION}.tgz" \
      | tar xz --strip-components=1 -C /usr/local/bin docker/docker && \
    apt-get clean && rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app/data /workspace && \
    groupadd -r sandcastle && useradd -r -g sandcastle sandcastle && \
    chown -R sandcastle:sandcastle /app /workspace /ms-playwright
USER sandcastle

EXPOSE 8080
VOLUME ["/app/data", "/workspace"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/api/v1/ready || exit 1

ENTRYPOINT ["dotnet", "Sandcastle.Api.dll"]

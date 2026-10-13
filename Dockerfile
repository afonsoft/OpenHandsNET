# OpenHandsNET — build multi-stage (app em :8080, compose expoe :3032)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (camada cacheada): props + todos os csproj
COPY Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY src/OpenHandsNET.Domain/OpenHandsNET.Domain.csproj src/OpenHandsNET.Domain/
COPY src/OpenHandsNET.Application/OpenHandsNET.Application.csproj src/OpenHandsNET.Application/
COPY src/OpenHandsNET.Infrastructure/OpenHandsNET.Infrastructure.csproj src/OpenHandsNET.Infrastructure/
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

# Volume persistente: SQLite (data/) + workspace dos agentes
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app/data /workspace && \
    groupadd -r openhandsnet && useradd -r -g openhandsnet openhandsnet && \
    chown -R openhandsnet:openhandsnet /app /workspace
USER openhandsnet

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    OPENHANDSNET_Database__ConnectionString="Data Source=/app/data/openhandsnet.db"

EXPOSE 8080
VOLUME ["/app/data", "/workspace"]

COPY --from=build --chown=openhandsnet:openhandsnet /app/publish .
ENTRYPOINT ["dotnet", "OpenHandsNET.Api.dll"]

# Install — Host (no app container)

Runs the app directly on the machine — for development or systemd
production. **Sandboxes still use Docker**: the daemon must be installed
and reachable, unless `Sandbox__Provider` is `remote` or `kubernetes`.
Português: [../../installation/host.md](../../installation/host.md).

## Requirements

- .NET SDK 10 (build) or ASP.NET Core Runtime 10 (run only)
- Docker Engine 24+ reachable at `/var/run/docker.sock` (provider `local`)
- Node.js 22 — drift tooling only (optional)
- 4 GB free RAM

## Development

```bash
git clone https://github.com/afonsoft/OpenHandsNET.git
cd OpenHandsNET
dotnet restore OpenHandsNET.slnx
dotnet run --project src/OpenHandsNET.Api    # http://localhost:8080
```

Boot applies EF migrations (`DatabaseMigrator`) and seeds OSS data.
The Blazor WASM client is served by the Api itself — no separate frontend.

Configure via `OPENHANDSNET_*` (prefix + `__` for the section):

```bash
export OPENHANDSNET_AgentLlm__BaseUrl=https://omniroute.example.com/v1
export OPENHANDSNET_AgentLlm__ApiKey=sk-...
export OPENHANDSNET_AgentLlm__Model=my-model
dotnet run --project src/OpenHandsNET.Api
```

With no LLM configured, conversations work with
`AgentLlm:AllowMock=true` (mock mode — dev/tests only).

## Production (systemd) {#producao-systemd}

1. Publish:

```bash
dotnet publish src/OpenHandsNET.Api -c Release -o /opt/openhandsnet
```

2. Create user and directories:

```bash
sudo useradd -r -s /usr/sbin/nologin openhandsnet
sudo mkdir -p /var/lib/openhandsnet/data /var/lib/openhandsnet/workspaces
sudo chown -R openhandsnet:openhandsnet /opt/openhandsnet /var/lib/openhandsnet
# docker.sock access to launch sandboxes:
sudo usermod -aG docker openhandsnet
```

3. `/etc/systemd/system/openhandsnet.service`:

```ini
[Unit]
Description=OpenHandsNET
After=network-online.target docker.service
Requires=docker.service

[Service]
User=openhandsnet
Group=docker
WorkingDirectory=/opt/openhandsnet
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:8080
Environment=OPENHANDSNET_Database__ConnectionString=Data Source=/var/lib/openhandsnet/data/openhandsnet.db
Environment=OPENHANDSNET_Sandbox__WorkspaceRoot=/var/lib/openhandsnet/workspaces
EnvironmentFile=-/etc/openhandsnet/env
ExecStart=/opt/openhandsnet/OpenHandsNET.Api
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

4. `/etc/openhandsnet/env` (chmod 600) — LLM and other secrets:

```bash
OPENHANDSNET_AgentLlm__BaseUrl=https://omniroute.example.com/v1
OPENHANDSNET_AgentLlm__ApiKey=sk-...
OPENHANDSNET_AgentLlm__Model=my-model
```

5. Enable:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now openhandsnet
curl -fsS http://localhost:8080/api/v1/ready
```

## Reverse proxy (e.g. nginx)

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    # SignalR + SSE need upgrade/keepalive
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_read_timeout 3600s;
}
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
```

For PostgreSQL instead of SQLite, see
[../../deployment/external-postgres.md](../../deployment/external-postgres.md).

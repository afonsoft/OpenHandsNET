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
git clone https://github.com/afonsoft/sandcastle.git
cd Sandcastle
dotnet restore Sandcastle.slnx
dotnet run --project src/Sandcastle.Api    # http://localhost:8080
```

Boot applies EF migrations (`DatabaseMigrator`) and seeds OSS data.
The Blazor WASM client is served by the Api itself — no separate frontend.

Configure via `SANDCASTLE_*` (prefix + `__` for the section):

```bash
export SANDCASTLE_AgentLlm__BaseUrl=https://omniroute.example.com/v1
export SANDCASTLE_AgentLlm__ApiKey=sk-...
export SANDCASTLE_AgentLlm__Model=my-model
dotnet run --project src/Sandcastle.Api
```

With no LLM configured, conversations work with
`AgentLlm:AllowMock=true` (mock mode — dev/tests only).

## Installer (`install.sh`) {#installer}

`install.sh` at the repo root automates the production path documented
below: it validates/installs **.NET SDK 10** and **Docker Engine**, publishes
the app, creates the user/directories, installs Playwright Chromium (browser
tool), ensures the sandbox runtime image
(`ghcr.io/sandcastle/runtime:latest` — pull, with local `runtime/` build as
fallback), writes the systemd unit and runs a health check.

```bash
git clone https://github.com/afonsoft/sandcastle.git
cd sandcastle
sudo ./install.sh            # installs and starts the service (app on :8080)
```

Modes:

| Command | What it does |
|---|---|
| `sudo ./install.sh` | Full install/reinstall |
| `sudo ./install.sh update` | `git fetch`+`pull` of `main`; on changes, republishes app, Chromium, runtime image and restarts the service. No changes → no-op |
| `sudo ./install.sh update --force` | Redeploy even without new commits |
| `sudo ./install.sh uninstall` | Removes service + binaries (keeps data and secrets) |
| `sudo ./install.sh uninstall --purge` | Also removes `/var/lib/sandcastle`, `/etc/sandcastle` and the user |

Flags: `--prefix DIR` (`/opt/sandcastle`), `--data-dir DIR`
(`/var/lib/sandcastle`), `--port N` (8080), `--branch NAME` (`main`),
`--no-start` (writes everything but does not start the service).

After install: configure the LLM in `/etc/sandcastle/env` and restart:

```bash
sudo systemctl restart sandcastle
journalctl -u sandcastle -f          # logs
curl -fsS http://localhost:8080/api/v1/ready
```

Managing sandbox containers is the app's job — the Docker daemon just
needs to be running; the `sandcastle` user is added to the `docker` group
during installation.

## Production — manual (systemd) {#producao-systemd}

1. Publish:

```bash
dotnet publish src/Sandcastle.Api -c Release -o /opt/sandcastle
```

2. Create user and directories:

```bash
sudo useradd -r -s /usr/sbin/nologin sandcastle
sudo mkdir -p /var/lib/sandcastle/data /var/lib/sandcastle/workspaces
sudo chown -R sandcastle:sandcastle /opt/sandcastle /var/lib/sandcastle
# docker.sock access to launch sandboxes:
sudo usermod -aG docker sandcastle
```

3. `/etc/systemd/system/sandcastle.service`:

```ini
[Unit]
Description=Sandcastle
After=network-online.target docker.service
Requires=docker.service

[Service]
User=sandcastle
Group=docker
WorkingDirectory=/opt/sandcastle
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:8080
Environment=SANDCASTLE_Database__ConnectionString=Data Source=/var/lib/sandcastle/data/sandcastle.db
Environment=SANDCASTLE_Sandbox__WorkspaceRoot=/var/lib/sandcastle/workspaces
EnvironmentFile=-/etc/sandcastle/env
ExecStart=/opt/sandcastle/Sandcastle.Api
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

4. `/etc/sandcastle/env` (chmod 600) — LLM and other secrets:

```bash
SANDCASTLE_AgentLlm__BaseUrl=https://omniroute.example.com/v1
SANDCASTLE_AgentLlm__ApiKey=sk-...
SANDCASTLE_AgentLlm__Model=my-model
```

5. Enable:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now sandcastle
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

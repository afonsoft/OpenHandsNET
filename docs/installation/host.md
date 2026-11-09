# Instalação — Host (sem container para o app)

Roda o app direto na máquina — para desenvolvimento ou produção systemd.
Os **sandboxes ainda usam Docker**: o daemon precisa estar instalado e
acessível, a menos que `Sandbox__Provider` seja `remote` ou `kubernetes`.
English: [../en/installation/host.md](../en/installation/host.md).

## Requisitos

- .NET SDK 10 (build) ou ASP.NET Core Runtime 10 (só execução)
- Docker Engine 24+ acessível via `/var/run/docker.sock` (provider `local`)
- Node.js 22 — só para tooling de drift (opcional)
- 4 GB RAM livres

## Desenvolvimento

```bash
git clone https://github.com/afonsoft/sandcastle.git
cd Sandcastle
dotnet restore Sandcastle.slnx
dotnet run --project src/Sandcastle.Api    # http://localhost:8080
```

O boot aplica as migrações EF (`DatabaseMigrator`) e semeia dados OSS.
O Blazor WASM é servido pelo próprio Api — não há front separado.

Config via `SANDCASTLE_*` (prefixo + `__` para seção):

```bash
export SANDCASTLE_AgentLlm__BaseUrl=https://omniroute.exemplo.com/v1
export SANDCASTLE_AgentLlm__ApiKey=sk-...
export SANDCASTLE_AgentLlm__Model=meu-modelo
dotnet run --project src/Sandcastle.Api
```

Sem LLM configurado, conversas funcionam com `AgentLlm:AllowMock=true`
(modo mock — dev/testes apenas).

## Produção (systemd) {#producao-systemd}

1. Publique:

```bash
dotnet publish src/Sandcastle.Api -c Release -o /opt/sandcastle
```

2. Crie o usuário e diretórios:

```bash
sudo useradd -r -s /usr/sbin/nologin sandcastle
sudo mkdir -p /var/lib/sandcastle/data /var/lib/sandcastle/workspaces
sudo chown -R sandcastle:sandcastle /opt/sandcastle /var/lib/sandcastle
# acesso ao docker.sock para subir sandboxes:
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

4. `/etc/sandcastle/env` (chmod 600) — LLM e demais segredos:

```bash
SANDCASTLE_AgentLlm__BaseUrl=https://omniroute.exemplo.com/v1
SANDCASTLE_AgentLlm__ApiKey=sk-...
SANDCASTLE_AgentLlm__Model=meu-modelo
```

5. Ative:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now sandcastle
curl -fsS http://localhost:8080/api/v1/ready
```

## Reverse proxy (ex.: nginx)

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    # SignalR + SSE exigem upgrade/keepalive
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_read_timeout 3600s;
}
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
```

Para PostgreSQL em vez de SQLite, veja
[../deployment/external-postgres.md](../deployment/external-postgres.md).

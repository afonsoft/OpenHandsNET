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
git clone https://github.com/afonsoft/OpenHandsNET.git
cd OpenHandsNET
dotnet restore OpenHandsNET.slnx
dotnet run --project src/OpenHandsNET.Api    # http://localhost:8080
```

O boot aplica as migrações EF (`DatabaseMigrator`) e semeia dados OSS.
O Blazor WASM é servido pelo próprio Api — não há front separado.

Config via `OPENHANDSNET_*` (prefixo + `__` para seção):

```bash
export OPENHANDSNET_AgentLlm__BaseUrl=https://omniroute.exemplo.com/v1
export OPENHANDSNET_AgentLlm__ApiKey=sk-...
export OPENHANDSNET_AgentLlm__Model=meu-modelo
dotnet run --project src/OpenHandsNET.Api
```

Sem LLM configurado, conversas funcionam com `AgentLlm:AllowMock=true`
(modo mock — dev/testes apenas).

## Produção (systemd) {#producao-systemd}

1. Publique:

```bash
dotnet publish src/OpenHandsNET.Api -c Release -o /opt/openhandsnet
```

2. Crie o usuário e diretórios:

```bash
sudo useradd -r -s /usr/sbin/nologin openhandsnet
sudo mkdir -p /var/lib/openhandsnet/data /var/lib/openhandsnet/workspaces
sudo chown -R openhandsnet:openhandsnet /opt/openhandsnet /var/lib/openhandsnet
# acesso ao docker.sock para subir sandboxes:
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

4. `/etc/openhandsnet/env` (chmod 600) — LLM e demais segredos:

```bash
OPENHANDSNET_AgentLlm__BaseUrl=https://omniroute.exemplo.com/v1
OPENHANDSNET_AgentLlm__ApiKey=sk-...
OPENHANDSNET_AgentLlm__Model=meu-modelo
```

5. Ative:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now openhandsnet
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

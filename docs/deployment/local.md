# Deploy local (OSS)

O tier default roda tudo num único container com SQLite embutido.

## Requisitos

- Docker 24+ (o app precisa do daemon para subir sandboxes — veja `security.md`)
- 4 GB RAM livres

## Subir

```bash
cp .env.exemplo .env          # ajuste se necessário
docker compose up -d --build  # app em http://localhost:3032
```

Verificação:

```bash
curl -fsS http://localhost:3032/api/v1/ready   # 200 quando migrado
curl -fsS http://localhost:3032/api/v1/health  # liveness simples
curl -fsS http://localhost:3032/metrics        # contadores Prometheus
```

## Sandboxes

Cada conversa cria um container Docker isolado montando
`data/workspaces/<conversation-id>` em `/workspace` do sandbox.
Sem mount de `docker.sock`, o app reporta `docker: unreachable` no
`/api/v1/readiness` e conversas falham no estágio WAITING_FOR_SANDBOX.

Para habilitar execução de sandbox, monte o socket:

```yaml
# docker-compose.override.yaml
services:
  app:
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

## Volumes

| Volume | Conteúdo |
|---|---|
| `openhandsnet-data` | SQLite, chaves DataProtection, workspaces |
| `openhandsnet-workspace` | Bind interno dos workspaces |

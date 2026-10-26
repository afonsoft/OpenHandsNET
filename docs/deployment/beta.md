# Deploy beta (host único com Docker)

Guia mínimo para subir uma beta do OpenHandsNET em uma VM/host com Docker
para testes. Versão em inglês: [`../en/deployment/beta.md`](../en/deployment/beta.md).

## Pré-requisitos

- Docker + compose plugin no host
- Acesso a um LLM/gateway (OmniRoute, OpenAI-compat, Ollama remoto)
- Imagem: publicada em `ghcr.io/afonsoft/openhandsnet` via tag `v*`
  (`.github/workflows/release.yml`) ou build local.

## Subir

```bash
cp .env.exemplo .env
# edite .env: OPENHANDSNET_LLM__BaseUrl / ApiKey / Model (OmniRoute),
# OPENHANDSNET_Sandbox__PreviewTokenSecret (recomendado)
docker compose -f docker-compose.beta.yaml up -d
# imagem local: docker compose -f docker-compose.beta.yaml up -d --build
```

App: `http://<host>:3032` — primeiro usuário registrado vira admin.

## Acesso a sandboxes (obrigatório p/ criar conversas)

O `docker-compose.beta.yaml` monta `/var/run/docker.sock` e liga
`Sandbox__DockerSocketMount=true` — necessário para o app criar
containers de sandbox no host. Trade-offs em
[`dind.md`](dind.md): é o isolamento mais fraco — aceitável para beta
single-user; produção deve usar `tecnativa/docker-socket-proxy`,
Docker rootless (`DOCKER_HOST=unix:///run/user/<uid>/docker.sock`) ou o
provider remoto (`Sandbox__Provider=remote` + Runtime API).

## Modelo de rede (SPEC-115)

Só front+API são expostos. VNC/noVNC, IDE e serviços do sandbox passam
pelo proxy same-origin da API (`/api/v1/sandboxes/{id}/surfaces/{port}/`);
terminal via SignalR. **Não publique portas de containers de sandbox.**
Detalhes: [`../security/sandbox-networking.md`](../security/sandbox-networking.md).

## Variáveis essenciais no `.env`

| Var | Para quê |
|---|---|
| `OPENHANDSNET_LLM__BaseUrl` / `__ApiKey` / `__Model` | gateway LLM (ex.: OmniRoute) |
| `OPENHANDSNET_Sandbox__DefaultImage` | imagem do runtime dos sandboxes |
| `OPENHANDSNET_Sandbox__PreviewTokenSecret` | tokens de preview assinados |
| `OPENHANDSNET_Sandbox__EgressMode` | `unrestricted` (beta) / `allowlist` / `deny` |
| `OPENHANDSNET_PORT` | porta externa (default 3032) |

## Smoke test

1. `GET /api/v1/health` → 200
2. Registrar usuário → login
3. Criar conversa com repo de teste → task `READY`
4. Abrir VNC/IDE — tudo via `/api/v1/sandboxes/…/surfaces/` (sem acesso
   direto ao container)

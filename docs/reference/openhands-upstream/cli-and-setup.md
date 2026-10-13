# Extrato — CLI Cloud + Local Setup

> `/openhands/usage/cli/cloud` + `/openhands/usage/run-openhands/local-setup`,
> capturado 2026-10-12. Fonte primária para SPEC 06.

## CLI cloud commands

```bash
openhands login [--server-url https://…]   # OAuth2 Device Flow; abre browser
openhands logout [--server-url …]
openhands cloud -t "task" | -f file.txt [--server-url …]
```
Env: `OPENHANDS_CLOUD_URL` (default `https://app.all-hands.dev`).
Cloud vs local: cloud = compute/persistência/compartilhamento remotos;
local = sua máquina + seus LLM keys.

## Local setup

```bash
uv tool install openhands --python 3.12
openhands serve [--gpu] [--mount-cwd]   # GUI server (Docker)
# ou
docker run -it --rm --pull=always \
  -e AGENT_SERVER_IMAGE_REPOSITORY=ghcr.io/openhands/agent-server \
  -e AGENT_SERVER_IMAGE_TAG=1.26.0-python -e LOG_ALL_EVENTS=true \
  -v /var/run/docker.sock:/var/run/docker.sock -v ~/.openhands:/.openhands \
  -p 3000:3000 --add-host host.docker.internal:host-gateway \
  docker.openhands.dev/openhands/openhands:1.8
```
Requisitos: Docker (Desktop/WSL2), 4GB RAM. Setup inicial exige LLM provider +
model + API key; Tavily opcional p/ search engine.

## Enterprise (docs /enterprise + OpenHands-Cloud)

- Self-hosted/VPC; SSO SAML+OIDC; GitHub Ent/GitLab SH/Azure DevOps/Bitbucket DC/
  Jira DC/Slack; sandboxes containerizados; BYOK; RBAC multi-user.
- Componentes cloud (ARCHITECTURE.md): app (UI+API+webhooks), Runtime API
  (warm pod pool), Runtime pods, Image Loader; K8s+PostgreSQL+Redis+Keycloak+
  LiteLLM Proxy+LB+blob store.
- Tabela OSS/Cloud/Enterprise: OSS=1 user local BYOK; Cloud=SaaS hosted;
  Enterprise=unlimited users, SSO, RBAC, self-hosted.

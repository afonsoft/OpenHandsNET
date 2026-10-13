# Upstream Baseline — fontes clonadas para análise

Snapshot dos repositórios usados como fonte de verdade na análise de migração
(2026-10-12). Re-auditorias devem comparar contra estes SHAs.

| Repositório | HEAD SHA | Licença | Papel |
|---|---|---|---|
| `OpenHands/OpenHands` | `d7e14e2` | MIT | Agent Canvas (frontend React/TS) |
| `OpenHands/software-agent-sdk` | `acca181` | MIT | SDK + Agent Server (API canônica) |
| `OpenHands/OpenHands-CLI` | `0403f1a` | MIT | CLI/TUI + cloud mode |
| `OpenHands/enterprise` | `693589e` | **Polyform Trial** | App server + SaaS (conceitos apenas) |
| `OpenHands/OpenHands-Cloud` | `d242d12` | **Polyform Trial** | Helm/K8s deploy (conceitos apenas) |
| `OpenHands/automation` | `7cda26a` | MIT | Automações/webhooks |
| `afonsoft/open-webui` | `5a7d920` | (fork) | Referência .NET 10 / Blazor WASM |

Docs consultadas (Mintlify, `docs.openhands.dev`):
- `/enterprise` — visão enterprise (SSO, RBAC, BYOK, integrações DC)
- `/openhands/usage/cloud/openhands-cloud` — onboarding cloud
- `/openhands/usage/cloud/cloud-api` — API keys, start-task, polling, V0→V1
- `/openhands/usage/cloud/api-reference/events/search-events` — OpenAPI events
- `/openhands/usage/run-openhands/local-setup` — `openhands serve`, docker run
- `/openhands/usage/cli/cloud` — `openhands login|cloud` CLI

# OpenHandsNET

Port open-source (MIT) do ecossistema **OpenHands** para **.NET 10 / C# 14 /
Blazor WebAssembly** — agente autônomo de engenharia de software com conceitos
Cloud + Enterprise e UX inspirada no Devin Web, sobre a base de
[`afonsoft/open-webui`](https://github.com/afonsoft/open-webui).

> Status: **especificação** (`.specs/`). Implementação ainda não iniciada.

## Visão

- **EventStream V1** no núcleo (`kind` discriminador, ULID, `parent_id`) —
  toda ação/observação do agente é um evento persistido e transmitido.
- **Sandbox por conversa** (Docker.DotNet): workspace isolado, bash session,
  browser Playwright, file/git ops.
- **API `/api/v1` compatível com OpenHands V1** + realtime via SignalR.
- **UI Devin-like**: chat + planner + terminal + browser + files + diff (Monaco).
- **CLI** `openhandsnet`: login device-flow, `cloud`, `run` headless, `serve`, TUI.
- **Enterprise open-source**: OIDC, API keys `sk-*`, orgs/RBAC, quota, auditoria.

## Layout do repositório

```
.specs/             SPECs SDD (8 módulos — comece por .specs/README.md)
migration-plan/     research (api-inventory, domains, risks) + roadmap
docs/reference/     baseline upstream + extratos das docs oficiais
src/  cli/  tests/  (a criar — ver .specs/01_architecture_overview.md)
```

## Fontes

- Upstream MIT: `OpenHands/software-agent-sdk`, `OpenHands/OpenHands` (Canvas),
  `OpenHands/OpenHands-CLI`, `OpenHands/automation` (baselines em
  `docs/reference/upstream-baseline.md`).
- Upstream Polyform (conceitos apenas — **não portamos código**):
  `OpenHands/enterprise`, `OpenHands/OpenHands-Cloud`.

## Licença

MIT — implementação original. Marcas e conceitos de terceiros pertencem a seus
detentores.

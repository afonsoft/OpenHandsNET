# OpenHandsNET

Port open-source (MIT) do ecossistema **OpenHands** para **.NET 10 / C# 14 /
Blazor WebAssembly** — agente autônomo de engenharia de software com conceitos
Cloud + Enterprise e UX inspirada no Devin Web, sobre a base de
[`afonsoft/open-webui`](https://github.com/afonsoft/open-webui).

> Status: **especificação + foundation** — solução .NET 10 scaffolded
> (SPEC-01). Domínios chegam nas SPECs 02–13.

## Visão

- **EventStream V1** no núcleo (`kind` discriminador, ULID, `parent_id`) —
  toda ação/observação do agente é um evento persistido e transmitido.
- **Sandbox por conversa** (Docker.DotNet): workspace isolado, bash session,
  browser Playwright, file/git ops.
- **API `/api/v1` compatível com OpenHands V1** + realtime via SignalR.
- **UI Devin-like**: chat + planner + terminal + browser + files + diff (Monaco).
- **CLI** `openhandsnet`: login device-flow, `cloud`, `run` headless, `serve`, TUI.
- **Enterprise open-source**: OIDC, API keys `sk-*`, orgs/RBAC, quota, auditoria.

## Quickstart

Pré-requisito: .NET SDK 10 (`dotnet --version`).

```bash
dotnet build OpenHandsNET.slnx          # build (9 projetos)
dotnet test OpenHandsNET.slnx           # testes
dotnet run --project src/OpenHandsNET.Api   # app em http://localhost:8080
cp .env.exemplo .env && docker compose up -d --build   # container em :3032
```

Configuração por env vars com prefixo `OPENHANDSNET_` (ex.:
`OPENHANDSNET_Database__Provider=PostgreSql`). Banco default: SQLite em
`data/openhandsnet.db` (migrado no boot). Endpoints: `GET /api/v1/health`,
`/api/v1/ready`, `/api/v1/config`.

## Layout do repositório

```
.specs/             SPECs SDD (14 módulos — comece por .specs/README.md)
migration-plan/     research (api-inventory, domains, risks) + roadmap
docs/reference/     baseline upstream + extratos das docs oficiais
src/                Domain · Application · Infrastructure · Api · Client (WASM)
cli/                OpenHandsNET.Cli (Spectre.Console — SPEC-06)
tests/              UnitTests · IntegrationTests · Client.Tests (bUnit)
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

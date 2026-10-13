# SPEC — 01 · Architecture Overview

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `architecture-overview` |
| Type | `Infra / Docs` |
| Stack | `.NET 10 · C# 14 · Blazor WASM · ASP.NET Core Minimal APIs` |
| Repository | `afonsoft/OpenHandsNET` |
| Branch | `main` (SPECs) |
| Status | `Done` (PR [#15](https://github.com/afonsoft/OpenHandsNET/pull/15)) |
| Ticket | [#2](https://github.com/afonsoft/OpenHandsNET/issues/2) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | — |

## 1. User Story

**As a** mantenedor do OpenHandsNET
**I want** uma estrutura de solução .NET canônica com dependências unidirecionais
**So that** todos os domínios (eventos, sandbox, API, UI, CLI) tenham um lar óbvio
e o código permaneça testável e consistente com o ecossistema afonsoft.

**Problem context:** o guia de migração (`openhandsnet_full_specification_and_migration_guide.md`,
seção 7) propõe `Core/Runtime/Web/Cli`. O padrão já validado em
`afonsoft/open-webui` usa Clean Architecture de 5 projetos; adotá-lo maximiza
reuso de layout/chat e reduz divergência de convenções.

## 2. Scope

**In scope:**
- `OpenHandsNET.slnx` com 6 projetos + testes.
- Clean Architecture: `Domain ← Application ← Infrastructure ← Api`; `Client → Application`; `Cli` consome REST/SignalR.
- Bootstrap: `Program.cs` minimal, `DatabaseMigrator` no startup, `MapStaticAssets()` p/ WASM, Serilog + OpenTelemetry.
- Config: `appsettings*.json` + env vars (`OPENHANDSNET_*`), `.env.exemplo`.
- Dockerfile multi-stage (`:3032 → :8080`) e `docker-compose.yaml`.

**Out of scope:** implementação de domínios (SPECs 02–08), K8s/Helm (fase 3).

## 3. Technical Context

```
OpenHandsNET.slnx
├── src/
│   ├── OpenHandsNET.Domain          # records de eventos (kind), entidades, enums — zero deps
│   ├── OpenHandsNET.Application     # DTOs, interfaces (IEventService, ISandboxService, IAppConversationService, IAgentLlmProvider)
│   ├── OpenHandsNET.Infrastructure  # EF Core (SQLite/PG), Docker.DotNet, Playwright, webhooks, secrets store
│   ├── OpenHandsNET.Api             # Minimal APIs /api/v1/* + SignalR hubs + hosting WASM
│   └── OpenHandsNET.Client          # Blazor WASM (layout baseado em open-webui-net)
├── cli/
│   └── OpenHandsNET.Cli             # Spectre.Console + System.CommandLine
├── tests/
│   ├── OpenHandsNET.UnitTests       # xUnit + Shouldly
│   ├── OpenHandsNET.IntegrationTests# Testcontainers (Docker sandbox real)
│   └── OpenHandsNET.Client.Tests    # bUnit
├── .specs/  migration-plan/  docs/
└── Dockerfile, docker-compose.yaml, .env.exemplo, CLAUDE.md, README.md
```

Regras de dependência (hard): `Domain` sem refs; `Application → Domain`;
`Infrastructure → Application`; `Api → Infrastructure`; `Client → Application`;
`Cli` → **somente contratos HTTP** (sem project reference ao Api).

**Files to read before implementing:**
- `open-webui-net/CLAUDE.md`, `src/OpenWebUI.Api/Program.cs` (padrão de boot)
- `migration-plan/research/stack-research.md`

## 4. Requirements

### RF-001: Solução e projetos
- `dotnet build OpenHandsNET.slnx` compila os 6 projetos + testes em .NET 10.
- `OpenHandsNET.Api` sobe e serve o Client WASM em `http://localhost:8080`.

### RF-002: Boot e persistência
- `DatabaseMigrator` aplica EF migrations no startup (baseline de bases legadas).
- SQLite default (`data/openhandsnet.db`); `Database__Provider=PostgreSql` + `Database__ConnectionString` alternam provider.

### RF-003: Configuração
- `OpenHandsNetOptions` bind de `OPENHANDSNET_*` env vars; secrets nunca em appsettings commitados; `.env.exemplo` documenta tudo.

### RF-004: Observabilidade
- Serilog estruturado; OTel traces (`conversation` root span) + métricas; correlação `conversation_id`/`sandbox_id` em todo log.

### RF-005: Container
- `docker compose up -d --build` expõe app em `:3032`; volume para SQLite + workspace.

**Invariants:** dependências forward-only; `async/await` com `Async` suffix e
`CancellationToken` obrigatório; records imutáveis para eventos/DTOs; nunca
`.Result`/`.Wait()`; documentação XML em APIs públicas; docs pt-BR, código inglês.

## 5. API Contract

Esta SPEC não define endpoints (ver SPEC 04). Expõe apenas:
`GET /api/v1/config` (info pública: versão, `deployment_mode`, features) e
`GET /api/v1/health` | `/api/v1/ready` (liveness/readiness).

## 6. Critérios de Aceite

- [ ] `dotnet build` + `dotnet test` verdes em Linux e no CI.
- [ ] `dotnet run --project src/OpenHandsNET.Api` serve UI em `:8080`.
- [ ] Violação de dependência (ex.: `Domain` referenciando `Infrastructure`) falha o build (teste de arquitetura).
- [ ] `docker compose up` funciona com `.env.exemplo` copiado.

## 7. Task Plan

1. `dotnet new slnx` + projetos + refs (verify: build).
2. `Program.cs` boot: Serilog, OTel, EF Core, `DatabaseMigrator`, `MapStaticAssets` (verify: run).
3. Options + `.env.exemplo` + README quickstart (verify: env override).
4. Dockerfile multi-stage + compose (verify: compose up).
5. Testes de arquitetura (NetArchTest) + CI build (verify: test).

## 8. Deliverables — arquivos a criar/modificar

```text
OpenHandsNET.slnx                     # dotnet new slnx
global.json                           # pin SDK 10.0.x (rollForward latestMinor)
Directory.Build.props                 # LangVersion=preview/14, Nullable=enable, ImplicitUsings,
                                      # TreatWarningsAsErrors, EnforceCodeStyleInBuild
Directory.Packages.props              # CPM (central package management)
src/OpenHandsNET.Domain/              # csproj sem deps
src/OpenHandsNET.Application/         # contratos: IEventService, ISandboxService,
                                      # IAppConversationService, IAgentLlmProvider, ISkillService,
                                      # IHookService, IGitService, ISecretStore, IBillingProvider
src/OpenHandsNET.Infrastructure/      # EF Core, DatabaseMigrator, Docker.DotNet, secrets
src/OpenHandsNET.Api/Program.cs       # Serilog → OTel → Options → EF → Migrator →
                                      # MapStaticAssets → route groups → hubs → SPA fallback
src/OpenHandsNET.Api/appsettings.json | appsettings.Development.json
src/OpenHandsNET.Client/              # Blazor WASM — portar shell de
                                      # open-webui-net/src/OpenWebUI.Client (Layout, Pages, Services)
cli/OpenHandsNET.Cli/
tests/OpenHandsNET.UnitTests/ tests/OpenHandsNET.IntegrationTests/ tests/OpenHandsNET.Client.Tests/
src/OpenHandsNET.Infrastructure/Persistence/DatabaseMigrator.cs  # MigrateAsync no boot
src/OpenHandsNET.Api/Options/OpenHandsNetOptions.cs              # bind OPENHANDSNET_*
Dockerfile  docker-compose.yaml  .dockerignore  .env.exemplo  .gitignore  nuget.config
tests/OpenHandsNET.UnitTests/Architecture/DependencyRulesTests.cs  # NetArchTest forward-only
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/stack-research.md` — decisões de stack e divergências do guia.
- `migration-plan/research/domain-candidates.md` — mapa de domínios.
- `docs/reference/upstream-baseline.md` — SHAs dos clones upstream.
- `CLAUDE.md` — regras do repo.

### Referência .NET (padrão a copiar)
- Estrutura: https://github.com/afonsoft/open-webui/tree/5a7d920/src
- Boot `Program.cs`: https://github.com/afonsoft/open-webui/blob/5a7d920/src/OpenWebUI.Api/Program.cs
- Client WASM: https://github.com/afonsoft/open-webui/tree/5a7d920/src/OpenWebUI.Client
- `CLAUDE.md` upstream: https://github.com/afonsoft/open-webui/blob/5a7d920/CLAUDE.md

### Docs Microsoft / libs
- .NET 10: https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview
- C# 14: https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-14
- `dotnet sln` / `.slnx`: https://learn.microsoft.com/dotnet/core/tools/dotnet-sln
- Hosted services: https://learn.microsoft.com/aspnet/core/fundamentals/host/hosted-services
- Options pattern: https://learn.microsoft.com/aspnet/core/fundamentals/configuration/options
- EF Core: https://learn.microsoft.com/ef/core/ · SQLite: https://learn.microsoft.com/ef/core/providers/sqlite/ · Npgsql: https://www.npgsql.org/efcore/
- Serilog ASP.NET: https://github.com/serilog/serilog-aspnetcore
- OpenTelemetry .NET: https://opentelemetry.io/docs/languages/net/ e https://learn.microsoft.com/dotnet/core/diagnostics/observability-with-otel
- Docker multi-stage: https://learn.microsoft.com/aspnet/core/host-and-deploy/docker/building-net-docker-images
- NetArchTest: https://github.com/BenMorris/NetArchTest
- xUnit: https://xunit.net/ · Shouldly: https://github.com/shouldly/shouldly · bUnit: https://bunit.dev/
- Health checks: https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks
- CPM: https://learn.microsoft.com/nuget/consume-packages/central-package-management

## 10. Guardrails de execução (Devin)

- **Branch:** código sempre via `feature/spec-01-…` + PR; nunca commitar em `main`.
- **Workflows:** não criar/alterar `.github/workflows/` sem aprovação explícita.
- **Secrets:** proibido commitar `.env`, tokens, `data/*.db`, `bin/`, `obj/`.
- **Pacotes:** versões estáveis >7 dias publicadas; sem `*`/`latest`/pré-release.
- **Pré-requisitos Devin:** .NET SDK 10 (`dotnet --version`); Docker opcional
  nesta SPEC (só para compose/smoke — pular se indisponível).
- Documentação pt-BR; código, projetos e APIs em inglês.

## 11. Definition of Done

- [ ] Todos os RFs implementados e critérios de aceite (§6) verificados.
- [ ] `dotnet build OpenHandsNET.slnx -c Release` e `dotnet test` verdes.
- [ ] `dotnet run --project src/OpenHandsNET.Api` serve o WASM em `:8080`.
- [ ] `docker compose up -d --build` funciona com `.env.exemplo` copiado para `.env`.
- [ ] `DependencyRulesTests` reprova referência reversa (provar com teste falho intencional).
- [ ] SPEC marcada `Done`; issue vinculada fechada pelo PR.

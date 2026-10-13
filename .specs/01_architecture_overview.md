# SPEC — 01 · Architecture Overview

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `architecture-overview` |
| Type | `Infra / Docs` |
| Stack | `.NET 10 · C# 14 · Blazor WASM · ASP.NET Core Minimal APIs` |
| Repository | `afonsoft/OpenHandsNET` |
| Branch | `main` (SPECs) |
| Status | `Approved` |
| Depends on | — |

## 1. User Story

**As a** mantenedor do OpenHandsNET
**I want** uma estrutura de solução .NET canônica com dependências unidirecionais
**So that** todos os domínios (eventos, sandbox, API, UI, CLI) tenham um lar óbvio
e o código permaneça testável e consistente com o ecossistema afonsoft.

**Problem context:** o guia de migração (`docs/reference/openhandsnet_…_guide.md`,
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

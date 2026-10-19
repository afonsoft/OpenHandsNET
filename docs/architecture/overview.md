# Arquitetura — OpenHandsNET (overview)

Camadas e componentes. Fonte: `CLAUDE.md` estrutura do projeto +
`.specs/` ativas + `docs/specs/` (arquivo). Atualizado: 2026-10-19.

```mermaid
flowchart TB
    subgraph Client["Cliente"]
        WASM["OpenHandsNET.Client<br/>Blazor WASM — chat, IDE, admin"]
        CLI["OpenHandsNET.Cli<br/>Spectre.Console — login/run/serve/eval"]
    end

    subgraph Edge["API Edge"]
        API["OpenHandsNET.Api<br/>Minimal APIs /api/v1 · SignalR hubs<br/>WASM hosting · auth OIDC+sk-*/svc_*"]
    end

    subgraph Core["Núcleo"]
        APP["Application<br/>contratos/DTOs/interfaces"]
        DOM["Domain<br/>eventos V1 · entidades — zero deps"]
        INF["Infrastructure<br/>EF Core Sqlite+PG · agent loop<br/>Docker.DotNet · secrets · webhooks<br/>SCM · knowledge · automations"]
        APP --> DOM
        INF --> APP
    end

    subgraph Runtime["Runtime / Sandbox"]
        RTAPI["OpenHandsNET.RuntimeApi<br/>orquestração fora-de-processo"]
        SBOX["OpenHandsNET.Sandboxing<br/>ISandboxEngine · Squid egress"]
        AGT["OpenHandsNET.SandboxAgent<br/>PID1 HTTP no container"]
        RTAPI --> SBOX --> AGT
    end

    subgraph Ext["Externo"]
        GH[("GitHub App / GitLab /<br/>Bitbucket / Azure DevOps")]
        LLM[("LLM gateways<br/>SK · OmniRoute · BYOR LiteLLM")]
        K8S[("Docker / Kubernetes")]
        CHAT[("Slack · Linear · Jira · SCIM")]
    end

    WASM -->|"HTTP + SignalR"| API
    CLI -->|"HTTP + SignalR"| API
    API --> INF
    API --> RTAPI
    INF --> DOM
    INF --> K8S
    SBOX --> K8S
    INF --> GH
    INF --> LLM
    INF --> CHAT
    EVAL["OpenHandsNET.Eval<br/>harness de avaliação"] --> APP
```

## Dependências (regra de camadas)

`Api → Infrastructure → Application → Domain` · `Client → Application` ·
`Cli` consome só contratos HTTP/SignalR · `RuntimeApi/Sandboxing/SandboxAgent`
formam o runtime separado (SPEC-35).

## Persistência e tempo real

- EF Core 10 — SQLite default, PostgreSQL enterprise (`Npgsql`), migrations
  duplas (`Migrations/` + `Migrations/Postgres/`), `DatabaseMigrator` no boot.
- SignalR — durable events + `streaming_delta` efêmeros; backplane
  Redis/Valkey para multi-réplica (SPEC-36).
- Cache/locks distribuídos: `IDistributedCache` + `ICoordinationBus`.

## ADRs

- [ADR-0001 — Estrutura do harness agente](0001-harness-structure.md)

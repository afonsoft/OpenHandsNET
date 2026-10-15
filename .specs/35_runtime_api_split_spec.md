# SPEC — 35 · Runtime-API Split (sandbox orchestration out-of-process)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `runtime-api-split` |
| Type | `Infra / Architecture` |
| Stack | `.NET 10 · ASP.NET Core · Docker.DotNet → REST` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` (enterprise wave; origem SPEC-32) |
| Ticket | #74 |
| Depends on | `03` (sandbox), `14` (ops/warm pool), `16` (lifecycle), `17` (image blueprint) |
| Touches | `ISandboxService`, `DockerSandboxService`, `WarmPoolService`, `SandboxSleepService`, `OrphanSweepService`, hosting/compose |

## 1. User Story

**As a** operador de uma instância multi-host
**I want** o lifecycle de sandboxes servido por um serviço dedicado
`openhandsnet-runtime-api` (REST interno) em vez de in-process no Api
**So that** o API possa escalar horizontalmente e sandboxes podem ser
provisionados em nós/hosts distintos — paridade com o `runtime-api` do
OpenHands-Cloud (conceitos apenas — Polyform).

**Contexto:** hoje `DockerSandboxService` roda in-process no
`OpenHandsNET.Api` contra o Docker socket local. Isso acopla escala do
API à escala do host Docker e impede multi-host. O contrato
`ISandboxService` (SPEC-03) já é o seam correto: trocar a impl por um
cliente HTTP não muda consumidores.

## 2. Scope (RF)

### RF-001 — Serviço `openhandsnet-runtime-api`

Novo projeto `src/OpenHandsNET.RuntimeApi` (ASP.NET Core minimal API,
sem UI, sem SignalR público) hospedando:

- `POST /runtime/v1/sandboxes` — create+start a partir de `SandboxSpec`;
- `POST /runtime/v1/sandboxes/{id}/pause|stop|resume|delete`;
- `GET  /runtime/v1/sandboxes/{id}` — estado reconciliado;
- `GET  /runtime/v1/sandboxes?status=&conversation_id=`;
- `GET  /runtime/v1/healthz` + `/readyz` (Docker socket, disco, cgroup);
- warm pool + sleep/wake + orphan sweep passam a viver **aqui**
  (`WarmPoolService`, `SandboxSleepService`, `OrphanSweepService`
  movem-se do Api para o RuntimeApi).

Auth interna: mTLS **ou** bearer `runtime_api_key` (nunca `sk-*` de
usuário); rede privada apenas — sem exposição pública.

### RF-002 — `HttpSandboxService` no Api

Implementação de `ISandboxService` que delega ao runtime-api via
`HttpClient` tipado (retry + circuit breaker, Polly já usado? — ver
`Infrastructure/Resilience`). Seleção por config:

```yaml
Sandbox:
  Provider: local | remote        # default: local (in-process atual)
  Remote:
    BaseUrl: http://runtime-api:8090
    ApiKey: ${RUNTIME_API_KEY}
```

`Provider=local` preserva 100% do comportamento atual (zero breaking).

### RF-003 — Identidade e ownership

O RuntimeApi **não** conhece EF do produto — recebe `conversation_id`
opaco e devolve `sandbox` records; a persistência de `Sandbox`/`Spec`
continua no Api (o RuntimeApi é stateless sobre state efêmero do
Docker; reconciliação via labels no container:
`ohn.conversation_id`, `ohn.sandbox_id`, `ohn.spec_hash`).

### RF-004 — Compose/deploy

`docker-compose.yaml` ganha profile `split` com serviço `runtime-api`;
`docker-compose.enterprise.yaml` referencia. `doctor` ganha check
`runtime-api reachable` quando `Provider=remote`.

## 3. API Contract

Contrato interno REST espelha `ISandboxService` 1:1 (mesmos DTOs —
mover `SandboxSpec`/`Sandbox` records para `Application` já são
shared). Versionado `/runtime/v1/`. Erros: `409` estado inválido,
`503` Docker indisponível, `507` disco cheio.

## 4. Data model

Sem migration. Labels Docker para reconciliação stateless.

## 5. NFR / Security

- RuntimeApi nunca exposto publicamente (bind interno / rede docker).
- Secrets by-ref (SPEC-33) continuam resolvidos no `ConversationStartWorker`
  do Api — env vars vão no body do create para o RuntimeApi **somente**
  pela rede interna (nunca em eventos).
- Warm pool distribuído: quando multi-runtime-api, cada instância cuida
  do pool do próprio host (pool key inclui `runtime_id`).

## 6. Task Plan

1. Extrair `DockerSandboxService`+`WarmPool`+`Sleep`+`OrphanSweep` para
   assembly compartilhado `OpenHandsNET.Sandboxing` (usado pelos dois
   hosts).
2. `RuntimeApi` host + endpoints + auth interna.
3. `HttpSandboxService` + config `Sandbox:Provider`.
4. Compose profile `split` + `doctor` check.
5. Testes: contract tests do HttpSandboxService contra fake server;
   integração Testcontainers do RuntimeApi.

## 7. Deliverables

`src/OpenHandsNET.RuntimeApi`, `src/OpenHandsNET.Sandboxing`,
`HttpSandboxService`, compose profile, testes.

## 8. Referências

- OpenHands-Cloud `runtime-api` (provisioning, warm pools, idle cleanup)
  — **conceitos apenas** (Polyform), SPEC-32 §3.
- SPEC-14 (warm pool, sizing), SPEC-16 (sleep/wake), SPEC-17 (image).

## 9. Guardrails

- `Provider=local` permanece o default — OSS single-host não ganha
  complexidade.
- Nenhum endpoint público novo; API pública `/api/v1` inalterada.
- RuntimeApi stateless quanto a DB — EF fica no Api.

## 10. DoD

- `Provider=remote` + compose `split`: conversa completa (start → run →
  sleep → wake → delete) funciona ponta a ponta.
- Warm pool cria/reclama sandboxes via RuntimeApi.
- `doctor` detecta runtime-api down quando `remote`.
- `dotnet test` verde.

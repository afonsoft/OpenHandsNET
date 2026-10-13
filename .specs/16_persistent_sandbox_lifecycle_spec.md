# SPEC — 16 · Persistent Sandbox Lifecycle (sleep / wake / GC)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `persistent-sandbox-lifecycle` |
| Type | `Feature / Infra` |
| Stack | `.NET 10 · Docker.DotNet · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#36](https://github.com/afonsoft/OpenHandsNET/issues/36) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox_spec`, `10_persistence_efcore`, `14_enterprise_operations_spec` |
| Touches | `DockerSandboxService`, `SandboxTimeoutWatcher`, `ConversationStartWorker`, `AppConversationService`, `SandboxEndpoints`, `AppConversationEndpoints` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** que o container da minha conversa durma quando o trabalho termina e
acorde quando eu volto a falar — sendo destruído só quando eu arquivar/apagar
a conversa
**So that** o contexto do workspace (processos, caches, estado do fs) sobrevive
entre mensagens sem queimar CPU/RAM, e eu nunca perco o container por acidente.

**Problem context:**

- Hoje `POST /send-message` numa conversa cujo sandbox está `Paused` retorna
  `409 ConflictSandboxNotRunning` (`AppConversationService.ValidateSendMessageAsync`)
  — o usuário precisa de um resume manual, e não existe resume automático.
- O container só dorme pelo `SandboxTimeoutWatcher` quando `ExpiresAt` vence
  (default 60 min contado do **start**, não da última atividade) — uma conversa
  ativa pode dormir no meio do trabalho.
- `docker pause`/`unpause` já existem (`DockerSandboxService.PauseAsync` /
  `ResumeAsync`), mas nenhum fluxo os dispara além do timeout.
- Delete de conversa já remove o container (`AppConversationService.DeleteAsync` →
  `sandboxes.DeleteAsync`), mas o **diretório do workspace no host fica órfão**
  para sempre em `Sandbox:WorkspaceRoot`.
- Paridade upstream: `sandbox_idle_seconds` → pause; `sandbox_dead_seconds` →
  delete; `sandbox_status ∈ {ERROR,MISSING}` → conversa read-only
  (docs `enterprise/conversations-and-sandboxes`).

## 2. Scope

**In scope:**

- **RF-001 — Sleep ao finalizar o run**: quando o `AgentRunner` termina um run
  (`Finished`/`Error`/`idle`) e não há mensagem do usuário pendente, o sandbox é
  pausado após uma graça configurável `Sandbox:IdleSleepSeconds` (default `300`,
  `0` = dormir imediatamente, `-1` = nunca dormir no finish). A graça é
  cancelável — uma nova mensagem dentro dela mantém o sandbox acordado.
- **RF-002 — Wake-on-message**: `send-message`/`resume` num sandbox `Paused` faz
  `ResumeAsync` + renova `ExpiresAt` e prossegue normalmente (nunca 409 por
  `Paused`). `Stopped` (ver RF-003) → `docker start` + health check antes de
  aceitar o tráfego.
- **RF-003 — Sleep stop reaper (opcional)**: `Sandbox:SleepStopAfterHours`
  (default `0` = desligado). Containers `Paused` há mais que N horas levam
  `docker stop` (libera RAM; fs/workspace persistem) e viram `Stopped`.
  Resume de `Stopped` faz `docker start` — processos morrem, arquivos não.
- **RF-004 — Lease baseado em atividade**: `ExpiresAt` renova em cada mensagem
  enviada e ao fim de cada run — o watcher nunca pausa uma conversa ativa.
- **RF-005 — Delete só no delete/archive**: o container e seu `SandboxRecord` só
  são removidos quando a conversa é deletada/arquivada (já é o caminho atual) —
  reforçar: `SandboxTimeoutWatcher` e `OrphanSweepService` **nunca** removem
  container de sandbox registrada viva.
- **RF-006 — Workspace GC**: delete de conversa remove `data/workspaces/{convId}`
  (já hoje fica órfão); archive **preserva** o workspace no disco (reativável).
- **RF-007 — Read-only em ERROR/MISSING**: `sandbox_status` em `Error`/`Missing`
  mantém a conversa legível mas `send-message` retorna `410 Gone` semântico —
  paridade upstream (transcript sempre preservado).

**Out of scope:** agent-server HTTP in-container e blueprint (SPEC-17),
multi-conversa por sandbox (upstream `sandbox grouping`), snapshot/restore de
fs para storage externo (workspace archive upstream é artefato interno).

## 3. Technical Context

**Estado atual (evidência):**

- `DockerSandboxService.cs:150-182` — `PauseAsync`/`ResumeAsync` via
  `docker pause`/`unpause`; `Missing` quando o container sumiu.
- `SandboxTimeoutWatcher.cs:43` — sweep a cada 1 min: `Status=="Running" &&
  ExpiresAtTicks < now` → `PauseAsync`. Não renova lease por atividade.
- `AppConversationService.cs:207-218` — `Paused` cai em
  `ConflictSandboxNotRunning` (o gap do wake).
- `AppConversationService.cs:173-190` — delete = `Archived` + remove container;
  workspace no host não é tocado.
- `SandboxStatus` já tem `Paused`; precisa de `Stopped` (RF-003).
- `ISandboxService` não expõe `StopAsync` — adicionar.

**Upstream (referências §9):** idle → pause, dead → delete; pause "retains the
conversation and recoverable workspace while releasing active runtime capacity";
ERROR/MISSING → read-only; archive capturado no cleanup.

## 4. Design

```
run Finished ──► agenda sleep (IdleSleepSeconds) ──► Paused   (docker pause)
Paused >SleepStopAfterHours ──► Stopped            (docker stop, RAM livre)
send-message ──► Paused? unpause | Stopped? start ──► renew ExpiresAt ──► run
delete conv  ──► remove container + workspace dir (GC)
archive conv ──► remove container, workspace fica
```

- `SandboxSleepService` (scoped): `ScheduleSleepAsync(sandboxId)` usa
  `Channel`/timer por sandbox; cancela em wake/nova msg.
- `ResumeAsync` ganha ramo `Stopped → StartContainerAsync` + health probe
  (`exec true` no container) antes de declarar `Running`; falha → `Error`.
- `ExpiresAt` renova via `TouchLease` em `send-message` e `OnRunFinished`.

## 5. API Contract

- `POST /api/v1/app-conversations/{id}/send-message` — sem mudança de contrato;
  `Paused`/`Stopped` viram wake implícito (200 em vez de 409).
- `POST /api/v1/sandboxes/{id}/pause|resume` — já existem; `resume` aceita
  `Stopped`.
- `DELETE /api/v1/app-conversations/{id}` — sem mudança; agora também faz GC
  do workspace.
- `SandboxDto.status` passa a admitir `"Stopped"`.
- `send-message` em `Error`/`Missing` → `410 Gone` (upstream parity) em vez de
  `503`.

## 6. NFR

- Wake de `Paused` < 500 ms (unpause é syscall cgroup); de `Stopped` < 5 s.
- Nunca remover container registrado fora do delete de conversa.
- `docker pause` indisponível (rootless/cgroup v1) → fallback `stop` com warning.
- Métricas: `openhandsnet_sandbox_sleeps_total`, `openhandsnet_sandbox_wakes_total{from=pause|stop}`.

## 7. Task Plan

1. `SandboxStatus.Stopped` + `ISandboxService.StopAsync` + resume por `start`.
2. `SandboxSleepService` + hook no fim do run (`AgentRunner`/`RunnerHost`).
3. Wake implícito em `ValidateSendMessageAsync` → `EnsureRunningAsync`.
4. Lease renewal por atividade + stop reaper no watcher.
5. Workspace GC no delete de conversa.
6. Testes: unit (sleep scheduling/cancel, transitions) + integration docker
   (pause→send→wake E2E, stop→resume, GC).

## 8. Deliverables

- Sleep/wake E2E funcional; GC de workspace; `Stopped` status; métricas.

## 9. Referências

- https://docs.openhands.dev/enterprise/conversations-and-sandboxes — lifecycle,
  pause "releases active runtime capacity", read-only em ERROR/MISSING.
- https://docs.openhands.dev/openhands/usage/cloud/api-reference/sandbox/pause-sandbox,
  resume-sandbox, delete-sandbox — contrato V1 upstream.
- DeepWiki `OpenHands-Cloud`: `sandbox_idle_seconds`→pause,
  `sandbox_dead_seconds`→delete, jobs `reaper`/`retention`/`snapshotter`.

## 10. Guardrails

- Container só morre por delete de conversa — nunca por watcher/sweep.
- Pause default (não stop) — preserva processos vivos (Devin-like "sleeping").
- Sem alterar `.github/workflows/`.

## 11. DoD

- [ ] Run Finished + graça → `Paused`; mensagem na graça cancela.
- [ ] `send-message` em `Paused`/`Stopped` acorda e roda sem 409.
- [ ] `Stopped` via `SleepStopAfterHours` retoma via `docker start`.
- [ ] Delete de conversa remove container **e** `data/workspaces/{id}`.
- [ ] Archive preserva workspace; ERROR/MISSING → read-only + 410.
- [ ] `dotnet build` + `dotnet test` verdes.

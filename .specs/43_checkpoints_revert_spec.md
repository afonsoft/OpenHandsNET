# SPEC — 43 · Checkpoints + Revert (Workdir Snapshots)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `checkpoints-revert` |
| Type | `Feature / Infrastructure + Client` |
| Stack | `.NET 10 · ISandboxExecutor (git exec) · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | `TBD` |
| Depends on | `03` (executor), `12` (git), `16` (sleep/wake), `24` (files_changed) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `CheckpointService` (git-dir separado + alternates), `CheckpointEndpoints`, `CheckpointList.razor`; padrão opencode `src/snapshot` |
| Touches | `src/OpenHandsNET.Infrastructure/Checkpoints` (novo), `src/OpenHandsNET.Api/Endpoints`, `src/OpenHandsNET.Client` (CheckpointList) |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** snapshots automáticos do workdir por turno, com diff e revert
granular num click
**So that** uma ação ruim do agente nunca é irreversível — hoje o único
freio é `git_commit` manual do agente; não existe checkpoint nenhum.

**Contexto:** o fork implementa o padrão opencode: git-dir **separado**
(`data/checkpoints/repo/{hash}` + `--work-tree`) com `objects/info/
alternates` apontando p/ o object store do repo — snapshots baratos sem
poluir o `.git` do usuário; fallback manifesto sem git. Adaptação: o
workdir aqui vive **dentro do sandbox** (`/workspace/repo`) — tudo via
`ISandboxExecutor.ExecAsync` (git CLI), zero mudança no sandbox-agent.
Decisão travada: **sem fallback manifesto** — repo sempre tem git
(gate `HasRepository`; sem repo a feature desabilita).

## 2. Scope (RF)

### RF-001 — `ICheckpointService` (Infrastructure)

- Git-dir separado por conversa: `/workspace/.ohn-checkpoints` —
  `git init --bare` uma vez; `objects/info/alternates` →
  `/workspace/repo/.git/objects` (compartilha object store).
- `SnapshotAsync(conv, label)`: `GIT_DIR=.ohn-checkpoints
  GIT_WORK_TREE=/workspace/repo git add -A` + `commit` (autor
  `openhands <ohn@local>`) → retorna `{hash, files_changed,
  insertions, deletions}` (via `diff-tree --stat` vs pai).
- `ListAsync`: `git log` do checkpoint dir →
  `[{hash, ts, label, stats}]` (mais novo primeiro, cap 50).
- `GetAsync(hash)`: diffstat + lista de arquivos do snapshot vs pai.
- Lock por workdir (conversa já é 1:1 sandbox — serializa via
  `SemaphoreSlim` por conversation id).

### RF-002 — Triggers de snapshot

- **Auto pós-turno**: ao fim de cada turno do assistente (hook no
  `AgentRunner` quando a run transiciona p/ `Finished`/`Paused`),
  `Checkpoints:Auto=true` default; label = `turn {n} {tool_resumo}`.
- **Tool `checkpoint`** no catálogo builtin (SPEC-25): agente snapshota
  sob demanda (p.ex. antes de refactor arriscado) — retorna hash.
- **Manual**: botão "Snapshot" na UI (RF-004).

### RF-003 — Revert com guarda de drift

- `POST /{id}/checkpoints/{hash}/revert {force?}`:
  - Run ativa (`ExecutionStatus == Running`) → **409** `run_active`.
  - Drift: alterações no workdir **posteriores** ao checkpoint (diff
    HEAD-checkpoint-dir vs workdir) → **409** `{conflicts:[paths]}`
    exceto `force=true`.
  - Revert = `GIT_DIR=… --work-tree=repo reset --hard {hash}`; arquivos
    untracked criados depois do snapshot permanecem (listados em
    `conflicts.untracked` na resposta).
  - Pós-revert: emite `files_changed` (UI/IDE atualizam) + novo snapshot
    `pre-revert {hash}` automático (revert é reversível).

### RF-004 — `CheckpointList.razor`

- Lista `{label, ts, stats}` + expand → diffstat truncado + botão
  **Revert** → modal de confirmação (mostra conflitos do drift quando
  409, oferece `force`).
- Casa: painel SCM da aba IDE (SPEC-42, soft-dep) + seção na aba
  `commits` atual (fallback standalone).

### RF-005 — Retenção

- `Checkpoints:MaxCount` (default 50) — prune dos mais antigos pós-
  snapshot; `Checkpoints:Auto`/`MaxCount` em config (não por conversa).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/app-conversations/{id}/checkpoints` | `[{hash,ts,label,files_changed,ins,del}]` |
| GET | `/api/v1/app-conversations/{id}/checkpoints/{hash}` | `{hash,ts,label,diffstat,files[]}` |
| POST | `/api/v1/app-conversations/{id}/checkpoints/{hash}/revert` | `{force?}` → `{reverted,conflicts?}`; 409 `run_active`/`drift` |
| POST | `/api/v1/app-conversations/{id}/checkpoints` | snapshot manual `{label?}` → `{hash}` |

Erros: `404` sem repo (`no_repository`), `503` `sandbox_not_running`
(sleeping — client oferece wake, SPEC-16).

## 4. Data model

Sem migration — estado derivado do git-dir no sandbox (stateless).
Config `Checkpoints:{Auto,MaxCount}` em `appsettings`/options.

## 5. NFR / Security

- Tudo via `ExecAsync` server-side com paths fixos escapados — o client
  nunca injeta comando livre; `hash` validado `^[0-9a-f]{7,64}$`.
- Revert exige owner/org (mesmo guard das rotas git).
- Snapshots não saem do sandbox — nenhum blob de código ao banco.
- Egress/egress: nenhum (git local).

## 6. Task Plan

1. `ICheckpointService` + init git-dir + alternates + Snapshot/List/Get
   via exec; testes de unidade (executor fake) + integração Testcontainers.
2. Triggers: pós-turno no `AgentRunner` + tool `checkpoint` + endpoint
   manual.
3. Revert + drift guard + run-active 409 + pre-revert snapshot.
4. Endpoints + DTOs + auth wiring.
5. `CheckpointList` + modal + embed SCM (42) + aba commits; bUnit.
6. Retenção + docs + `dotnet test` verde.

## 7. Deliverables

`Infrastructure/Checkpoints/CheckpointService.cs`,
`Api/Endpoints/CheckpointEndpoints.cs`, DTOs Application,
`Client/Components/Conversation/CheckpointList.razor`, tool `checkpoint`,
`files_changed` pós-revert, testes unit+integração+client.

## 8. Referências

- `afonsoft/open-webui` (MIT): `Infrastructure/Services/CheckpointService.cs`
  (git-dir+alternates, drift guard, 409 run-ativa), `Api/Endpoints/
  CheckpointEndpoints.cs`, `Components/CheckpointList.razor`,
  `docs/specs/SPEC-20261009-checkpoints-revert.md`.
- opencode `src/snapshot` (padrão origem, conceito).
- `.specs/12` (GitService/exec), `.specs/16` (sleep/wake), `.specs/24`
  (`files_changed`), `.specs/25` (tool catalog).

## 9. Guardrails

- Git-dir fora do `.git` do usuário; **nunca** commita no repo do usuário
  sem ser via tool `git_commit` explícita.
- Revert nunca com run ativa; drift sempre explícito (nunca force default).
- Sem fallback manifesto — `HasRepository` false desabilita a feature.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Turno do agente → checkpoint listado com stats corretos.
- Modifico arquivo → revert → workdir restaura exatamente; `files_changed`
  dispara; snapshot `pre-revert` criado.
- Run ativa → 409; drift sem force → 409 com paths; force → reverte.
- Testcontainers: fluxo snapshot→edit→revert verde; `dotnet test` verde.

# Session 2026-10-16 — SPEC-43 Checkpoints + Revert

## State
- Branch `feature/spec43-checkpoints` (worktree `ohn-spec43`, base `acab9b7`).
- Issue #101. SPECs 41/42 merged (`1e37ddb`, `acab9b7`).

## Done
- `ICheckpointService` + `CheckpointService`: bare git-dir
  `/workspace/.ohn-checkpoints` + `objects/info/alternates → repo/.git/objects`;
  Snapshot/List/Get/Drift/Revert via `ISandboxExecutor.ExecAsync`;
  `SemaphoreSlim` por conversa (static ConcurrentDictionary).
- Endpoints: `GET/POST /{id}/checkpoints`, `GET /{id}/checkpoints/{hash}`,
  `POST .../revert` — 404 `no_repository`, 503 `sandbox_not_running`,
  409 `run_active`, 409 drift `{conflicts,untracked}`; pre-revert snapshot
  + `files_changed` event.
- Triggers: `AgentRunner.AutoCheckpointAsync` em Finished + Paused
  (ct neutro — ct cancelado no catch); tool builtin `checkpoint`.
- `CheckpointList.razor` (embed SCM + aba commits): lista+expand,
  modal revert, 409 → mostra conflitos → botão vira `revert (force)`
  (primeiro confirm SEMPRE `force:false`).
- DI: `Checkpoints:{Auto=true,MaxCount=50}` + scoped service; rotas no
  `Program.cs`.

## Gotchas
- Prune por count em cadeia linear: `update-ref` no branch move p/ TRÁS e
  órfã os mais novos — ERRADO. Correto: `git replace --graft <N-ésimo>`
  (vira raiz → log/rev-list truncam) + `reflog expire` + `gc --prune=now`.
- `bash:5` (busybox) não tem git — `apk add --no-cache git` funciona
  (egress liberado); readiness do sandbox vem depois do `READY` do
  start-task — poll `/exec "true"` até 200.
- bUnit: `ShouldNotContain` enxerga atributos — `aria-label="Revert to …"`
  faz `ShouldNotContain("Revert to")` falhar sempre; asserir o elemento
  (`.modal-backdrop`).

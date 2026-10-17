# Checkpoints + Revert (SPEC-43)

Workdir snapshots outside the user's repo git tree — safety for letting
the agent edit freely.

## Model

- A separate git dir (`/workspace/.ohn-checkpoints/`) pointing at the
  worktree — the project's `.git` is never touched.
- Automatic snapshot at the end of each turn (`Finished`) and on
  `Paused`; manual via the `checkpoint` tool (agent) or the UI button.
- Retention `Checkpoints:MaxCount` (default 50): truncation via
  `git replace --graft` — keeps the newest N, preserving the chain
  above the graft.

## Revert

`POST /{id}/checkpoints/{hash}/revert` with a drift guard:

1. `force=false` (default): compares the current workdir against the
   top snapshot — on drift (changes after the checkpoint), `409`
   returns the conflicting file list.
2. `force=true`: `git checkout -f` discards the drift.

Reverts are blocked while `ExecutionStatus=Running` (409
`run_active`).

## UI

`CheckpointList` (IDE SCM tab + fallback on the commits tab): short
hash, label, `+ins/-del` stats, revert button → confirmation modal
with the conflicts list and a `revert (force)` option.

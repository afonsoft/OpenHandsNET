# Automations (SPEC-47)

Scheduled routines and webhook triggers that fire **start-task
conversations** with the owner's credentials. Runs go through the
normal pipeline — plan quotas, permissions and V1 events are identical
to a UI-started conversation.

## Model

| Table | Contents |
|---|---|
| `automations` | name, prompt (≤8KB), llm_model?, selected_repository?, schedule (kind/time/interval/weekday), enabled, next_run_at(+ticks), last_run_at |
| `automation_runs` | started/finished/status(`running\|succeeded\|failed\|skipped`), conversation_id, error |
| `automation_hooks` | sha256 token (plaintext shown **once**), last_used_at |

`schedule_kind`: `interval` (minutes ≥5), `daily`/`weekly` (`HH:mm` UTC,
weekday 0–6), `once` (disarms after firing). `ComputeNextRun` is pure and
truth-table tested.

## Scheduler

`AutomationScheduler` (BackgroundService) ticks every 30s under a
distributed lock — one replica at a time fires due automations and
reconciles `running` runs against the linked conversation's
`ExecutionStatus` (`Finished→succeeded`, `Error/Archived→failed`).
A failing automation never blocks the others.

## API

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/api/v1/automations` | list/create (validation: prompt ≤8KB, interval ≥5) |
| GET/PATCH/DELETE | `/api/v1/automations/{id}` | detail/update — `{enabled}` toggles |
| POST | `/api/v1/automations/{id}/run-now` | 202 `{run_id, conversation_id}`; 409 while running |
| GET | `/api/v1/automations/{id}/runs?take=`, `/api/v1/automations/runs?from&to` | history |
| POST/DELETE | `/api/v1/automations/{id}/hooks`, `/api/v1/automations/hooks/{hookId}` | one-time token |
| POST | `/api/v1/hooks/{token}` | **anonymous** → 202; 404 on invalid (doesn't leak existence); rate-limit 10/min per token + 60/min per IP |

## `routine` tool

Builtin (`Risky:true` → confirmation-mode gate) operating on the
conversation owner's automations: `action=list|create|enable|disable|
delete`. Thin adapter over the same `AutomationService` — no parallel
path.

## Origin and audit

- Created conversations get an `agent_diagnostic` `code=automation`
  with `automation:{id}` — visible on the timeline.
- Audit log: `automation_fired` + `automation_run_created` (actor,
  hook/ip when applicable).
- Quota denial → `AutomationRun(failed, error="quota: …")`.

## UI

`/automations` (sidebar): table with human schedule, next run, enabled
toggle, run-now and an "upcoming runs" list (top-10).
`/automations/{id}`: full editor + run history (conversation links) +
hook creation with the token shown exactly once.

> Automation prompts must never contain secrets — they are plain user
> messages stored in the database.

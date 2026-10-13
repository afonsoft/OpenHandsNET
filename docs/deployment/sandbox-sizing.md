# Sandbox Sizing Profiles (SPEC-14 RF-001)

Every sandbox runs with cgroup limits from a **named profile**. Values are
upper bounds (`HostConfig.Memory`, `NanoCPUs`, `PidsLimit`), not reservations.

| Profile | CPU | Memory | PIDs |
|---|---|---|---|
| `small` | 1.0 | 2 GiB | 256 |
| `medium` (default) | 2.0 | 4 GiB | 512 |
| `large` | 4.0 | 8 GiB | 1024 |

## Resolution order

1. `sandbox_profile` on `POST /app-conversations` (per conversation)
2. `{"sandbox_profile": "…"}` in org `settings` (`PATCH /orgs/{id}/settings`)
3. `OPENHANDSNET_Sandbox__Profile` (global default, `medium`)
4. Named `SandboxSpec.profile` on custom specs — overrides numbers only when
   set; explicit spec resources otherwise win

An unknown profile name logs a warning and keeps the spec's explicit values.
Conversations expose the resolved name via `sandbox_profile` in the DTO;
sandboxes expose `pool` (`cold`|`warm`).

## When to raise/lower

- **`small`** — doc edits, read-only questions, CI smoke runs
- **`medium`** — default; typical feature work (~npm install + tests)
- **`large`** — monorepo builds, heavy test suites, `npm run build` on large
  frontends — watch `openhandsnet_sandbox_seconds`/`docker stats` for real
  headroom before fleet-wide bumps

Upstream sizing guide: <https://docs.openhands.dev/enterprise/sizing-guide>

# Sandbox Sizing Profiles (SPEC-14 RF-001)

Every sandbox runs with cgroup limits from a **named profile**. Values
are upper bounds (`HostConfig.Memory`, `NanoCPUs`, `PidsLimit`), not
reservations.

| Profile | CPU | Memory | PIDs |
|---|---|---|---|
| `small` | 1.0 | 2 GiB | 256 |
| `medium` (default) | 2.0 | 4 GiB | 512 |
| `large` | 4.0 | 8 GiB | 1024 |

## Resolution order

1. `sandbox_profile` on `POST /app-conversations` (per conversation)
2. `{"sandbox_profile": "…"}` in org `settings` (`PATCH /orgs/{id}/settings`)
3. `OPENHANDSNET_Sandbox__Profile` (global default, `medium`)
4. Named `SandboxSpec.profile` on custom specs — overrides numbers only
   when set; explicit spec resources otherwise win

An unknown profile name logs a warning and keeps the spec's explicit
values. Conversations expose the resolved name via `sandbox_profile` in
the DTO; sandboxes expose `pool` (`cold`|`warm`).

## When to raise/lower

- **`small`** — doc edits, read-only questions, CI smoke runs
- **`medium`** — default; typical feature work (~npm install + tests)
- **`large`** — monorepo builds, heavy test suites, `npm run build` on
  large frontends — watch `openhandsnet_sandbox_seconds`/`docker stats`
  for real headroom before fleet-wide bumps

## Deployment sizing (SPEC-84)

Size deployments by **peak concurrent sandboxes** (one user can run
several sandboxes at once). The "users" column is a rough estimate —
size on sandbox peaks, not headcount. Per-sandbox unit at the default
profile (`medium`): **2 vCPU + 4 GiB + ~10 GiB workspace** (limits, not
reservations — real usage is usually far lower).

### Formula

```
Total CPU ≈ peak × profile CPU   (medium: peak × 2 vCPU)
Total RAM ≈ peak × 4 GiB + 4–8 GiB platform (app+PG+valkey+litellm+keycloak)
Disk      ≈ peak × 10 GiB (workspaces) + 20 GiB base — on a separate volume
```

### Compose (single VM)

| Peak sandboxes | Minimum VM | Data disk (separate from boot) |
|---|---|---|
| 5 | 8 vCPU / 32 GiB | 100 GiB SSD |
| 15 | 16 vCPU / 64 GiB | 250 GiB SSD |
| 30 | 32 vCPU / 128 GiB | 400 GiB SSD |
| 50+ | 64 vCPU / 256 GiB | 750 GiB SSD — or Helm (below) |

The workspace (`Sandbox:WorkspaceRoot` → `data/workspaces`) and data dir
consume real bytes as used — **put them on an expandable volume separate
from the boot disk** and grow before it fills.

### Helm (k8s)

Use **two node pools**: a tainted sandbox pool and an untainted platform
pool — a burst of sandboxes must not evict control-plane components.

| Peak sandboxes | Sandbox nodes (min–max) | Platform nodes | Volume storage |
|---|---|---|---|
| 10 | 1×16 vCPU/64 GiB | 2×8 vCPU/32 GiB | 100 GiB |
| 25 | 1–3×16 vCPU/64 GiB | 2 | 250 GiB |
| 50 | 1–5×16 vCPU/64 GiB | 2 | 500 GiB |
| 100 | 1–10 | 3 | 1 TiB |
| 200+ | 2–20 | 3 | 2 TiB+ — PostgreSQL on a dedicated node or external |

- Minimums assume **autoscaling**; without a fast autoscaler, raise the
  minimum toward your typical daily peak.
- **PostgreSQL**: in-cluster by default; above ~200 peak sandboxes use a
  dedicated node or external (`external-postgres.md`).
- Track the observed sandbox pod peak and adjust with headroom;
  OOMKills/evictions show up in `kubectl top`/`describe pod`.

Upstream sizing guide: <https://docs.openhands.dev/enterprise/sizing-guide>

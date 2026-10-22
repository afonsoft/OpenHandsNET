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

## Deployment sizing (SPEC-84)

Dimensionamento em nível de deployment é feito por **peak concurrent
sandboxes** (um usuário pode ter várias sandboxes ao mesmo tempo). A
"coluna de usuários" é estimativa aproximada — size pelo pico de
sandboxes, não por headcount. Unidade por sandbox no profile default
(`medium`): **2 vCPU + 4 GiB + ~10 GiB de workspace** (limites, não
reservas — o uso real costuma ser bem menor).

### Fórmula

```
CPU total ≈ peak × CPU do profile   (medium: peak × 2 vCPU)
RAM total ≈ peak × 4 GiB + 4–8 GiB de plataforma (app+PG+valkey+litellm+keycloak)
Disco     ≈ peak × 10 GiB (workspaces) + 20 GiB base — em volume separado
```

### Compose (VM única)

| Peak sandboxes | VM mínima | Disco de dados (separado do boot) |
|---|---|---|
| 5 | 8 vCPU / 32 GiB | 100 GiB SSD |
| 15 | 16 vCPU / 64 GiB | 250 GiB SSD |
| 30 | 32 vCPU / 128 GiB | 400 GiB SSD |
| 50+ | 64 vCPU / 256 GiB | 750 GiB SSD — ou Helm (abaixo) |

O workspace (`Sandbox:WorkspaceRoot` → `data/workspaces`) e o data dir
consomem bytes reais conforme o uso — **coloque-os em volume expansível
separado do disco de boot** e cresça antes de encher.

### Helm (k8s)

Use **dois node pools**: um pool de sandboxes (taint) e um pool de
plataforma (sem taint) — uma rajada de sandboxes não pode expulsar
componentes do plano de controle.

| Peak sandboxes | Sandbox nodes (min–max) | Platform nodes | Volume storage |
|---|---|---|---|
| 10 | 1×16 vCPU/64 GiB | 2×8 vCPU/32 GiB | 100 GiB |
| 25 | 1–3×16 vCPU/64 GiB | 2 | 250 GiB |
| 50 | 1–5×16 vCPU/64 GiB | 2 | 500 GiB |
| 100 | 1–10 | 3 | 1 TiB |
| 200+ | 2–20 | 3 | 2 TiB+ — PostgreSQL em node dedicado ou externo |

- Mínimos assumem **autoscaling**; sem autoscaler rápido, suba o mínimo
  para o pico diário típico.
- **PostgreSQL**: in-cluster default; acima de ~200 peak sandboxes use
  node dedicado ou externo (`external-postgres.md`).
- Acompanhe o pico observado de pods de sandbox e ajuste com folga;
  OOMKill/evictions aparecem em `kubectl top`/`describe pod`.

Upstream sizing guide: <https://docs.openhands.dev/enterprise/sizing-guide>

# OpenHandsNET

[![ci](https://github.com/afonsoft/OpenHandsNET/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/afonsoft/OpenHandsNET/actions/workflows/ci.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_OpenHandsNET&metric=alert_status)](https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main)
[![Maintainability](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_OpenHandsNET&metric=sqale_rating)](https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main)
[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_OpenHandsNET&metric=code_smells)](https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main)
[![Security](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_OpenHandsNET&metric=security_rating)](https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_OpenHandsNET&metric=coverage)](https://sonarcloud.io/summary/overall?id=afonsoft_OpenHandsNET&branch=main)

Open-source (MIT) port of the **OpenHands** ecosystem to **.NET 10 / C# 14 /
Blazor WebAssembly** — an autonomous software-engineering agent with Cloud +
Enterprise concepts and a Devin Web–inspired UX, built on top of
[`afonsoft/open-webui`](https://github.com/afonsoft/open-webui).

**AI-Driven Development (ADD)**: this project is built by agents — the
workflow is *agent-first* (issues, SPECs, implementation, tests, PRs).
SPECs follow SDD (Spec-Driven Development) as the agent-consumed source
of truth: specs written for agents, executed by agents, reviewed by humans.

> Status: **working product** — SPECs 01–64 + 68/69 delivered and
> archived in [`docs/specs/`](docs/specs/) (agent, sandbox, API, full
> UI, enterprise ops, SCM/ChatOps/ticketing integrations, enterprise
> identity, edge security, indexing/insights/knowledge, browser
> profiles). Active SPECs in [`.specs/`](.specs/).
> Quality gates: `dotnet format` + tests in CI; SonarCloud analysis
> (badges above) — see `ci.yml`.
> Português: [README.md](README.md).

## Vision

- **V1 EventStream** at the core (`kind` discriminator, ULID, `parent_id`) —
  every agent action/observation is a persisted, streamed event.
- **Sandbox per conversation** (Docker.DotNet / Kubernetes): isolated
  workspace, bash session, Playwright browser, file/git ops, sleep/wake,
  warm pool.
- **`/api/v1` API compatible with OpenHands V1** + realtime via SignalR
  (ephemeral deltas + durable events).
- **Devin-like UI** (Blazor WASM): streaming chat with reasoning, rich
  composer (@mentions, /slash, uploads, drafts), IDE workbench (Monaco +
  SCM + search), checkpoints with revert, attention inbox, i18n in
  8 locales, and a **Desktop** tab streaming the sandbox live via noVNC
  (SPEC-113).
- **Open-source enterprise**: OIDC + `sk-*` API keys, orgs/RBAC,
  multi-tenancy, plans/quotas/billing, audit, egress allowlist,
  pluggable security analyzer, by-reference secrets, S3 artifacts.
- **Automation**: scheduled routines (interval/daily/weekly/once) +
  rate-limited anonymous webhook triggers + a `routine` agent tool.
- **`openhandsnet` CLI**: device-flow login, headless `run`, `serve`,
  `doctor --preflight`, `eval`, TUI.

## Comparison

| Area | OpenHands (OSS) | OpenHands Cloud/Ent | **OpenHandsNET** | Devin Web |
|---|---|---|---|---|
| License | MIT | Polyform (closed) | **MIT** | Closed (SaaS) |
| Stack | Python + React | Python + k8s | **.NET 10 + Blazor WASM** | Proprietary |
| Event model | V1 `kind`/ULID | V1 + delta events | **Full V1 + `streaming_delta`** | Proprietary |
| Sandbox | Docker | k8s + Sysbox multi-host | **Docker in prod; k8s engine (SPEC-49)** | Proprietary |
| Warm pool / sleep-wake | Partial | Yes | **Docker + k8s warm-pool (SPEC-49)** | Yes |
| LLM routing | Static config | LiteLLM proxy | **OmniRoute catalog + roles + BYOR LiteLLM (SPEC-50)** | Proprietary |
| Security | SecurityAnalyzer | IdP orgs + analyzer | **pluggable analyzer + org policies (SPEC-48)** | Proprietary |
| Secrets | LookupSecret by-ref | Same | **by-reference (`SecretNamesJson`)** | Internal vault |
| Memory | `.openhands/memory/` | Same | **project + org scopes** | Knowledge/playbooks |
| Subagents | task tool | Same | **`task` + presets + depth cap** | Yes |
| Permissions | allow/deny/ask | Same + org policy | **`allow/deny/ask` + plan mode + org policies (SPEC-48)** | Proprietary |
| Artifacts | workspace | S3/GCS filestore | **workspace + S3 provider** | Proprietary |
| Deploy | docker run/compose | Helm + KOTS/k0s | **compose + Helm chart (SPEC-49)** | SaaS only |
| Multi-replica | — | Valkey/Redis backplane | **`IDistributedCache` + SignalR backplane** | Yes |
| UI | Agent Canvas (React) | Cloud UI | **Devin-like Blazor: chat, IDE, surfaces** | UX reference |
| Live desktop | — | VNC | **Desktop tab with noVNC + same-origin surface proxy (SPEC-113)** | Live desktop |
| CLI | `openhands` TUI | cloud mode | **`openhandsnet` parity + eval + doctor** | `devin` API |
| i18n | en | en | **8 locales (pt-BR default)** | en |
| Automations | GitHub webhooks | internal schedules | **routines + hook triggers + `routine` tool** | Devin automations |
| API keys | — | `sk-*` + org binding | **`sk-*` + org binding + BYOR** | Yes |
| Eval | OSS benchmarks | — | **`openhandsnet eval` + asserts + llm_judge** | — |
| GitHub App | — | yes | **App + verified webhooks + workspace PRs (SPEC-52)** | yes |
| SCM providers | — | GitHub | **GitHub + GitLab + Bitbucket + Azure DevOps (SPEC-53)** | GitHub+ |
| ChatOps | — | — | **Slack app: mention/slash → session, thread sync, auto-triage (SPEC-54)** | yes |
| Ticketing | — | — | **Linear + Jira triggers → start-task + write-back (SPEC-55)** | yes |
| Service users | — | — | **`svc_*` scoped identities + impersonation (SPEC-57)** | yes |
| IdP/SCIM | — | Keycloak | **SCIM 2.0 + IdP groups→role/tier, any IdP (SPEC-58)** | SSO |
| Edge security | — | — | **IP rules + input guard + per-actor rate limit (SPEC-59)** | proprietary |
| Repo indexing | — | — | **index + read-only Ask (SPEC-60)** | codebase context |
| Session insights | — | — | **cost/latency/tool diagnostics (SPEC-61)** | yes |
| Knowledge | playbooks | playbooks | **suggestion queue + human review + groom (SPEC-63)** | knowledge |
| Browser profiles | — | — | **encrypted storage state + site cookies + TOTP (SPEC-64)** | yes |

Legend: **bold** = current OpenHandsNET state; "in SPEC-N" = approved,
immediate roadmap.

## Quickstart

Prerequisite: .NET SDK 10 (`dotnet --version`).

```bash
dotnet build OpenHandsNET.slnx          # build (9 projects)
dotnet test OpenHandsNET.slnx           # tests
dotnet run --project src/OpenHandsNET.Api   # app at http://localhost:8080
cp .env.exemplo .env && docker compose up -d --build   # container at :3032
```

Configuration via `OPENHANDSNET_`-prefixed env vars (e.g.
`OPENHANDSNET_Database__Provider=PostgreSql`). Default database: SQLite at
`data/openhandsnet.db` (migrated on boot). Endpoints: `GET /api/v1/health`,
`/api/v1/ready`, `/api/v1/config`.

Full installation guides:
[Docker](docs/en/installation/docker.md) ·
[Host](docs/en/installation/host.md) ·
[Environment variables](docs/en/installation/environment-variables.md).

## Screenshots

| Home | Settings | Automations |
|---|---|---|
| ![home](docs/images/app-home.png) | ![settings](docs/images/app-settings.png) | ![automations](docs/images/app-automations.png) |

| Admin | Live sandbox desktop (noVNC — SPEC-113) |
|---|---|
| ![admin](docs/images/admin-usage.png) | ![desktop](docs/images/desktop-novnc.png) |

## Repository layout

```
.specs/             SDD/ADD specs (60+ modules — start with .specs/README.md)
migration-plan/     research (api-inventory, domains, risks) + roadmap
docs/               guides (deployment, ui, api, features) — index at docs/README.md
docs/en/            English documentation
charts/openhandsnet Helm chart (k8s — completed in SPEC-49)
deploy/             auxiliary manifests (litellm, otelcol, prometheus)
src/                Domain · Application · Infrastructure · Api · Client (WASM)
                    · Sandboxing · RuntimeApi · SandboxAgent · Eval
cli/                OpenHandsNET.Cli (Spectre.Console — SPEC-06/31)
tests/              UnitTests · IntegrationTests · Client.Tests (bUnit)
                    · SandboxAgent.Tests
```

## Documentation

- **[docs/README.md](docs/README.md)** — full index (pt-BR).
- **[docs/en/](docs/en/README.md)** — English docs.
- Highlights: [installation](docs/en/installation/) ·
  [deployment/local](docs/en/deployment/local.md) ·
  [kubernetes](docs/en/deployment/kubernetes.md) ·
  [security](docs/en/deployment/security.md) ·
  [automations](docs/en/automations.md) · [cli](docs/en/cli.md) ·
  [eval](docs/en/eval.md) · [detailed comparison](docs/en/comparison.md).

## Sources

- Upstream MIT: `OpenHands/software-agent-sdk`, `OpenHands/OpenHands` (Canvas),
  `OpenHands/OpenHands-CLI`, `OpenHands/automation` (baselines in
  `docs/reference/upstream-baseline.md`).
- Upstream Polyform (concepts only — **we do not port code**):
  `OpenHands/enterprise`, `OpenHands/OpenHands-Cloud`.

## License

MIT — original implementation. Third-party marks and concepts belong to
their owners.

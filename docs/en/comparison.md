# Detailed comparison — OpenHandsNET × OpenHands × Devin Web

Parity analysis by domain. Sources: `.specs/32` (gap analysis),
`docs/reference/upstream-baseline.md` (analyzed upstream SHAs),
`docs.openhands.dev` docs. Português: [../comparison.md](../comparison.md).

## License and distribution

| | OpenHands OSS | OpenHands Cloud/Ent | OpenHandsNET | Devin Web |
|---|---|---|---|---|
| License | MIT | Polyform Trial | **MIT** | Proprietary |
| Model | self-host | SaaS + self-host | **self-host (+cloud future)** | SaaS |
| Code | open | closed | **open** | closed |

OpenHandsNET is the only **open-source option with enterprise parity**
(RBAC, orgs, quotas, audit) in the OpenHands lineage — upstream keeps
those features behind Polyform.

## Agent runtime

| Capability | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Loop | `AgentBase.step()` | same | `AgentRunner` + `IAgentRunnerHost` |
| Events | V1 + coalesced deltas | same | full V1 + ephemeral `streaming_delta` (SPEC-34) |
| Sandbox | Docker | k8s+Sysbox multi-host | Docker (prod); k8s engine — completed in SPEC-49 |
| Warm pool | — | yes | Docker yes; k8s in SPEC-49 |
| Sleep/wake | — | yes | yes (SPEC-16); k8s in SPEC-49 |
| Sandbox-agent | REST agent-server | same | v2 `/exec/stream` + `/activity` ring (SPEC-24) |
| Surfaces | web terminal | + preview | gateway+terminal+VSCode+preview+ACP (SPEC-40) |
| Runtime split | — | runtime-api | `openhandsnet-runtime-api` (SPEC-35) |

## LLM and context

| | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Providers | static config | LiteLLM proxy | OmniRoute catalog + roles + fallback (SPEC-23); BYOR LiteLLM in SPEC-50 |
| Condensing | `condense()` + summarizer | same | meter + condenser + ledger + triggers (SPEC-22) |
| Subagents | `task` | same | `task`/`task_status`/`task_result` + presets + depth cap (SPEC-28) |
| Memory | `.openhands/memory/` | same | project + org scopes, `remember/recall/forget/promote` (SPEC-30) |
| Eval | WebArena/OSWorld | — | `openhandsnet eval` + asserts + `llm_judge` (SPEC-39) |

## Security

| | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Analyzer | pattern/LLM/policy | same | pluggable `ISecurityAnalyzer` (SPEC-33); per-org policy in SPEC-48 |
| Permissions | `allow/deny/ask` | + org policy | `allow/deny/ask` + plan mode (SPEC-29); org in SPEC-48 |
| Secrets | LookupSecret by-ref | same | `SecretNamesJson` — value only at the edge (SPEC-33) |
| Redaction | output | same | `SecretRedactor` on events + command_output |
| Egress | — | allowlist | Squid sidecar domain allowlist (SPEC-19) |
| Auth | single-user | Keycloak/RBAC | cookie + `sk-*` + generic OIDC + orgs/RBAC (SPEC-08) |
| Audit | — | yes | `IAuditService` + admin pages |
| Resume | tools fingerprint | same | `agent_contract` + `resume_drift` fail-open; strict in SPEC-48 |

## UI / UX

| | OSS | Cloud/Ent | OpenHandsNET | Devin Web |
|---|---|---|---|---|
| Framework | React Canvas | React | **Blazor WASM** | React |
| Chat | canvas + stream | same | streaming + reasoning + lifecycle + heartbeat (SPEC-41) | reference |
| IDE | file viewer | editor | Monaco workbench + SCM + search + tests (SPEC-42) | VS Code-like |
| Checkpoints | — | snapshots | per-turn snapshots + revert w/ drift guard (SPEC-43) | snapshots |
| Inbox | — | notifications | attention inbox + `ask_user` tool (SPEC-44) | attention UI |
| Composer | basic | same | @mentions /slash drafts uploads edit-resend (SPEC-45) | rich |
| i18n | en | en | **8 locales, pt-BR default** (SPEC-46) | en |
| Surfaces | terminal | + preview | terminal/VSCode/preview/ACP/gateway (SPEC-40) | similar |

## Enterprise ops

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| IdP | Keycloak | generic OIDC (any IdP) |
| Orgs/RBAC | yes | yes — orgs, roles, `perm:*` policies (SPEC-08) |
| Multi-tenant | yes | `MultiTenancy:Enabled` + `IOrgScoped` query filters |
| Plans/quotas | yes | conversations/day, concurrent runs, tokens/month, sandbox-s (SPEC-20) |
| Usage/billing | Stripe | `IUsageService` metering + plans CRUD (PSP integration out of scope) |
| LLM proxy | LiteLLM | internal OmniRoute; BYOR LiteLLM in SPEC-50 |
| Filestore | S3/GCS/MinIO | `IArtifactStore` workspace + S3 (SPEC-34) |
| Multi-replica | Valkey | `IDistributedCache` + backplane + locks (SPEC-36) |
| Deploy | Helm+KOTS+k0s | compose + full Helm chart (SPEC-49) |
| Automations | schedules | routines + webhook triggers + `routine` (SPEC-47) |
| GitHub App | yes | **GitHub App + verified webhooks + workspace PRs (SPEC-52)** |
| OpenAPI | yes | published OpenAPI + golden snapshot (SPEC-51) |

## Integrations (wave A)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| SCM | GitHub App | GitHub App + GitLab + Bitbucket + Azure DevOps via `IScmProvider` (SPECs 52–53) |
| ChatOps | — | Slack app: mention/slash → session, thread sync, `IChatSink` auto-triage (SPEC-54) |
| Ticketing | — | Linear + Jira webhooks → start-task + comment/transition write-back (SPEC-55) |
| Share | — | public `/share/{token}` links + trajectory download (SPEC-56) |

## Identity and edge (wave B)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| Service users | — | scoped `svc_*` identities + `created_as_user_id` impersonation (SPEC-57) |
| Provisioning | SCIM Keycloak | SCIM 2.0 provider + `scim_*` tokens + IdP groups→role/tier (SPEC-58) |
| Edge | external WAF | IP access rules + `IInputGuard` + per-actor rate limit (SPEC-59) |

## Intelligence (wave C)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| Codebase | internal index | repo indexing + read-only Ask with citations (SPEC-60) |
| Insights | analytics | session insights: cost/latency/tools/errors (SPEC-61) |
| Knowledge | playbooks | suggestion queue + human review + auto-groom (SPEC-63) |
| Browser | — | browser profiles: encrypted storage state + site cookies + TOTP (SPEC-64) |

## Devin Web and Cursor Cloud (SaaS references)

Comparison with the SaaS products OpenHandsNET emulates — Devin Web
(app.devin.ai) and Cursor Background Agents — plus OpenHands Cloud.

| Capability | Devin Web | Cursor Cloud Agents | OpenHands Cloud | OpenHandsNET |
|---|---|---|---|---|
| Parallel sessions | yes (list + search) | yes | yes | yes — conversations + sub-sessions (`task`) |
| VM per session | yes (snapshot/restore) | dedicated VM | pod/sandbox | Docker container per conversation (+k8s in SPEC-49) |
| Sleep/wake | memory snapshot | suspend/resume | yes | `docker pause`/`stop` + idle sleep (SPEC-16) |
| Desktop/stream | live Desktop tab | logs + diffs | preview + VSCode | terminal/VSCode/preview/ACP surfaces (SPEC-40) |
| Embedded IDE | VS Code web | web editor | VS Code | Monaco+SCM+search+tests workbench (SPEC-42) |
| Checkpoints | snapshots | diff revert | snapshots | per-turn snapshots + revert (SPEC-43) |
| Secrets | masked org/user secrets | env vars | BYO LLM keys | `SecretNamesJson` + `sk-*` device keys (SPEC-33/08) |
| Browser preview | port previews | — | preview | preview surfaces + `RoutingMode` |
| Automations | triggers+schedules+webhooks | schedules | schedules | routines + webhooks + `routine` tool (SPEC-47) |
| Slack/ChatOps | native bot | Slack | — | Slack app + auto-triage (SPEC-54) |
| PR loop | opens PR + CI watch | opens PR | GitHub App | GitHub App + workspace PRs (SPEC-52) |
| Billing/quotas | ACU + tags | usage | Stripe | metering + plans CRUD (SPEC-20) |
| Skills/playbooks | playbooks+skills | rules | microagents | skills settings + `task` presets + knowledge (SPEC-63) |
| CLI | `devin` CLI | `cursor-agent` | `openhands` | `openhandsnet` CLI (Spectre) |
| Public API | REST v3 | REST | REST V1 | `/api/v1` + golden OpenAPI (SPEC-51) |

### Structural differences

- **Devin Web** runs a full VM per session (filesystem + processes
  persist across sleeps via snapshot). OpenHandsNET uses Docker
  containers with bind-mounted workspaces — lighter, no process-memory
  restore; sleep/wake preserves workspace + events, not the heap.
- **Cursor Cloud Agents** focuses on the task→PR loop (agent runs, opens
  a PR, exits). No interactive chat or surfaces — OpenHandsNET already
  covers that flow via GitHub App + automations.
- **OpenHands Cloud** is the closest reference (same lineage): parity by
  domain in the tables above; remaining gaps are multi-host hosting
  (k8s+sysbox — SPEC-49) and PSP billing.

## Parity roadmap (prioritized)

Items that bring OpenHandsNET closest to Devin Web, by value:

1. ~~**Sandbox resource clamp**~~ — `NanoCPUs` is now clamped to host
   NCPU (`large` specs work on small hosts). *Done.*
2. **Per-image warm pool** — exists (`WarmPoolSize`/`WarmPoolImages`);
   next: TTL + proactive refresh (SPEC-49 covers the reaper).
3. **Session resume on a new sandbox** — resume reuses the event ledger;
   rehydrating a checkpoint into a fresh sandbox (Devin's VM "wake") is
   missing. SPEC-62 in progress covers part of it.
4. **Live desktop/streaming** — a screen surface from the sandbox
   (VNC/stream) is the biggest UX gap vs Devin; terminal/VSCode/preview
   exist but no framebuffer. No SPEC — candidate for the next one.
5. **Billing tags / cost codes** — metering exists per user/org; arbitrary
   tag grouping (cost centers) does not.
6. **PSP billing** — out of scope (Stripe-like); metering already emits
   the required counters.

## Known remaining gaps

1. **SPEC-62** — managed conversations / cross-sandbox delegation (in progress).
2. KOTS-style installer (scoping decision) and analytics pipeline — no SPEC.
3. PSP billing (Stripe-like) — metering exists; payment integration out
   of scope.

## What OpenHandsNET has that upstream doesn't

- **Single .NET stack**: agent, API, UI and CLI in one language/VM.
- **Full IDE workbench** (Monaco + SCM + search) — upstream shows files;
  doesn't edit inline in the conversation.
- **Persisted attention inbox** with projection on the conversation
  record.
- **Multi-locale i18n** (upstream is en-only).
- **`routine` tool** — the agent schedules its own automations.
- **Checkpoints with revert** — per-turn snapshot outside the user's
  repo git tree.

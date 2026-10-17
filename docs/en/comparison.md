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
| Deploy | Helm+KOTS+k0s | compose + basic Helm (full chart — SPEC-49 approved) |
| Automations | schedules | routines + webhook triggers + `routine` (SPEC-47) |
| GitHub App | yes | **phase 3 — not implemented** |
| OpenAPI | yes | **SPEC-51** |

## Known remaining gaps

1. **SPEC-48** — per-org `security_policies` + fail-closed resume.
2. **SPEC-49** — k8s warm-pool/sleep + complete Helm + real-cluster
   validation.
3. **SPEC-50** — BYOR LiteLLM (403 stubs today).
4. **SPEC-51** — published OpenAPI + snapshot.
5. **Phase 3 (no SPEC yet)** — GitHub App/auto-PR, SCM providers
   GitLab/Bitbucket/AzureDevOps, KOTS-style installer (scoping decision),
   analytics pipeline.

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

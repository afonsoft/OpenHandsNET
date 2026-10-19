# Comparação detalhada — OpenHandsNET × OpenHands × Devin Web

Análise de paridade por domínio. Fontes: `.specs/32` (gap analysis),
`docs/reference/upstream-baseline.md` (SHAs upstream analisados),
docs `docs.openhands.dev`. English: [en/comparison.md](en/comparison.md).

## Licença e distribuição

| | OpenHands OSS | OpenHands Cloud/Ent | OpenHandsNET | Devin Web |
|---|---|---|---|---|
| Licença | MIT | Polyform Trial | **MIT** | Proprietário |
| Modelo | self-host | SaaS + self-host | **self-host (+cloud futuro)** | SaaS |
| Código | aberto | fechado | **aberto** | fechado |

O OpenHandsNET é a única opção **open-source com paridade enterprise**
(RBAC, orgs, quotas, audit) na linhagem OpenHands — o upstream guarda
essas features atrás de Polyform.

## Runtime do agente

| Capability | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Loop | `AgentBase.step()` | idem | `AgentRunner` + `IAgentRunnerHost` |
| Eventos | V1 + deltas coalesced | idem | V1 completo + `streaming_delta` efêmero (SPEC-34) |
| Sandbox | Docker | k8s+Sysbox multi-host | Docker (prod); engine k8s — conclusão na SPEC-49 |
| Warm pool | — | sim | Docker sim; k8s em SPEC-49 |
| Sleep/wake | — | sim | sim (SPEC-16); k8s em SPEC-49 |
| Sandbox-agent | agent-server REST | idem | v2 `/exec/stream` + `/activity` ring (SPEC-24) |
| Surfaces | terminal web | + preview | gateway+terminal+VSCode+preview+ACP (SPEC-40) |
| Runtime split | — | runtime-api | `openhandsnet-runtime-api` (SPEC-35) |

## LLM e contexto

| | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Providers | config estática | LiteLLM proxy | OmniRoute catalog + roles + fallback (SPEC-23); BYOR LiteLLM em SPEC-50 |
| Condensação | `condense()` + summarizer | idem | meter + condenser + ledger + triggers (SPEC-22) |
| Subagentes | `task` | idem | `task`/`task_status`/`task_result` + presets + depth cap (SPEC-28) |
| Memória | `.openhands/memory/` | idem | project + org scopes, `remember/recall/forget/promote` (SPEC-30) |
| Eval | WebArena/OSWorld | — | `openhandsnet eval` + asserts + `llm_judge` (SPEC-39) |

## Segurança

| | OSS | Cloud/Ent | OpenHandsNET |
|---|---|---|---|
| Analyzer | pattern/LLM/policy | idem | `ISecurityAnalyzer` plugável (SPEC-33); policy por org em SPEC-48 |
| Permissões | `allow/deny/ask` | + org policy | `allow/deny/ask` + plan mode (SPEC-29); org em SPEC-48 |
| Secrets | LookupSecret by-ref | idem | `SecretNamesJson` — valor só na borda (SPEC-33) |
| Redação | saída | idem | `SecretRedactor` em events + command_output |
| Egress | — | allowlist | Squid sidecar por domínio (SPEC-19) |
| Auth | single-user | Keycloak/RBAC | cookie + `sk-*` + OIDC genérico + orgs/RBAC (SPEC-08) |
| Audit | — | sim | `IAuditService` + páginas admin |
| Resume | fingerprint tools | idem | `agent_contract` + `resume_drift` fail-open; strict em SPEC-48 |

## UI / UX

| | OSS | Cloud/Ent | OpenHandsNET | Devin Web |
|---|---|---|---|---|
| Framework | React Canvas | React | **Blazor WASM** | React |
| Chat | canvas + stream | idem | streaming + reasoning + lifecycle + heartbeat (SPEC-41) | referência |
| IDE | file viewer | editor | workbench Monaco+SCM+search+tests (SPEC-42) | VS Code-like |
| Checkpoints | — | snapshots | snapshots/turno + revert c/ drift guard (SPEC-43) | snapshots |
| Inbox | — | notifications | attention inbox + `ask_user` tool (SPEC-44) | attention UI |
| Composer | básico | idem | @menções /slash drafts uploads edit-resend (SPEC-45) | rico |
| i18n | en | en | **8 locales, pt-BR default** (SPEC-46) | en |
| Surfaces | terminal | + preview | terminal/VSCode/preview/ACP/gateway (SPEC-40) | similares |

## Enterprise ops

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| IdP | Keycloak | OIDC genérico (qualquer IdP) |
| Orgs/RBAC | sim | sim — orgs, roles, `perm:*` policies (SPEC-08) |
| Multi-tenant | sim | `MultiTenancy:Enabled` + `IOrgScoped` query filters |
| Planos/quotas | sim | conversas/dia, runs concorrentes, tokens/mês, sandbox-s (SPEC-20) |
| Usage/billing | Stripe | metering `IUsageService` + plans CRUD (integração PSP fora do escopo) |
| LLM proxy | LiteLLM | OmniRoute interno; BYOR LiteLLM em SPEC-50 |
| Filestore | S3/GCS/MinIO | `IArtifactStore` workspace + S3 (SPEC-34) |
| Multi-réplica | Valkey | `IDistributedCache` + backplane + locks (SPEC-36) |
| Deploy | Helm+KOTS+k0s | compose + Helm chart completo (SPEC-49) |
| Automações | schedules | routines + webhook triggers + `routine` (SPEC-47) |
| GitHub App | sim | **GitHub App + webhooks verificados + workspace PRs (SPEC-52)** |
| OpenAPI | sim | OpenAPI publicado + golden snapshot (SPEC-51) |

## Integrações (onda A)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| SCM | GitHub App | GitHub App + GitLab + Bitbucket + Azure DevOps via `IScmProvider` (SPECs 52–53) |
| ChatOps | — | Slack app: menção/slash → sessão, thread sync, auto-triage `IChatSink` (SPEC-54) |
| Ticketing | — | Linear + Jira webhooks → start-task + write-back comentário/transição (SPEC-55) |
| Share | — | links públicos `/share/{token}` + download de trajetória (SPEC-56) |

## Identidade e edge (onda B)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| Service users | — | identidades `svc_*` escopadas + impersonation `created_as_user_id` (SPEC-57) |
| Provisioning | SCIM Keycloak | SCIM 2.0 provider + tokens `scim_*` + grupos IdP→role/tier (SPEC-58) |
| Edge | WAF externo | IP access rules + `IInputGuard` + rate limit por ator (SPEC-59) |

## Inteligência (onda C)

| | Cloud/Ent | OpenHandsNET |
|---|---|---|
| Codebase | index interno | repo indexing + Ask read-only c/ citações (SPEC-60) |
| Insights | analytics | session insights: custo/latência/tools/erros (SPEC-61) |
| Knowledge | playbooks | suggestion queue + human review + groom automático (SPEC-63) |
| Browser | — | browser profiles: storage state criptografado + site cookies + TOTP (SPEC-64) |

## Gaps residuais conhecidos

1. **SPEC-62** — managed conversations / delegate cross-sandbox (em andamento).
2. KOTS-style installer (decisão de escopo) e analytics pipeline — sem SPEC.
3. PSP billing (Stripe-like) — metering existe; integração de pagamento
   fora do escopo.

## O que o OpenHandsNET tem e o upstream não

- **Stack .NET única**: agente, API, UI e CLI na mesma linguagem/VM.
- **IDE workbench completo** (Monaco + SCM + search) — upstream mostra
  files; não edita inline na conversa.
- **Attention inbox persistido** com projeção no registro da conversa.
- **i18n multi-locale** (upstream é en-only).
- **Tool `routine`** — o agente agenda suas próprias automações.
- **Checkpoints com revert** — snapshot por turno fora da árvore git do
  repo do usuário.

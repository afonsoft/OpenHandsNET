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

## Devin Web e Cursor Cloud (referências SaaS)

Comparativo com os produtos SaaS que o OpenHandsNET emula — Devin Web
(app.devin.ai) e Cursor Background Agents (cursor.com/agents) — e com o
OpenHands Cloud.

| Capability | Devin Web | Cursor Cloud Agents | OpenHands Cloud | OpenHandsNET |
|---|---|---|---|---|
| Sessões paralelas | sim (lista + busca) | sim | sim | sim — conversas + sub-sessões (`task`) |
| VM por sessão | sim (snapshot/restore) | VM dedicada | pod/sandbox | container Docker por conversa (+k8s em SPEC-49) |
| Sleep/wake | memória snapshot | suspend/resume | sim | `docker pause`/`stop` + idle sleep (SPEC-16) |
| Desktop/stream | Desktop tab ao vivo | logs + diffs | preview + VSCode | surfaces terminal/VSCode/preview/ACP (SPEC-40) |
| IDE embutido | VS Code web | web editor | VS Code | workbench Monaco+SCM+search+tests (SPEC-42) |
| Checkpoints | snapshots | diff revert | snapshots | snapshots/turno + revert (SPEC-43) |
| Secrets | org/user secrets mascarados | env vars | BYO LLM keys | `SecretNamesJson` + device keys `sk-*` (SPEC-33/08) |
| Browser preview | previews de porta | — | preview | surfaces preview + `RoutingMode` |
| Automações | triggers+schedules+webhooks | schedules | schedules | routines + webhooks + tool `routine` (SPEC-47) |
| Slack/ChatOps | bot nativo | Slack | — | Slack app + auto-triage (SPEC-54) |
| PR loop | abre PR + CI watch | abre PR | GitHub App | GitHub App + workspace PRs (SPEC-52) |
| Billing/quotas | ACU + tags | usage | Stripe | metering + plans CRUD (SPEC-20) |
| Skills/playbooks | playbooks+skills | rules | microagents | skills settings + `task` presets + knowledge (SPEC-63) |
| CLI | `devin` CLI | `cursor-agent` | `openhands` | `openhandsnet` CLI (Spectre) |
| API pública | REST v3 | REST | REST V1 | `/api/v1` + OpenAPI golden (SPEC-51) |

### Diferenças estruturais

- **Devin Web** roda uma VM completa por sessão (filesystem + processos
  persistem entre sleeps via snapshot). OpenHandsNET usa containers
  Docker com workspace bind-mounted — mais leve, sem restore de memória
  de processo; sleep/wake preserva workspace + eventos, não o heap.
- **Cursor Cloud Agents** foca no loop task→PR (agente roda, abre PR,
  encerra). Não tem chat interativo nem surfaces — OpenHandsNET já cobre
  esse fluxo via GitHub App + automações.
- **OpenHands Cloud** é a referência mais próxima (mesma linhagem):
  paridade por domínio nas tabelas acima; os gaps restantes são
  hospedagem multi-host (k8s+sysbox — SPEC-49) e billing PSP.

## Roadmap de paridade (priorizado)

Itens que mais aproximam o OpenHandsNET do Devin Web, em ordem de valor:

1. ~~**Clamp de recursos do sandbox**~~ — `NanoCPUs` agora é clampado ao
   NCPU do host (specs `large` funcionam em hosts pequenos). *Feito.*
2. **Warm pool por imagem** — já existe (`WarmPoolSize`/`WarmPoolImages`);
   próximo passo: TTL + refresh proativo (SPEC-49 cobre o reaper).
3. **Resume de sessão em sandbox novo** — hoje resume reusa o evento
   ledger; falta reidratar checkpoint em sandbox novo (paridade com o
   "wake" de VM do Devin). SPEC-62 em andamento cobre parte disso.
4. ~~**Desktop/streaming ao vivo**~~ — surface `desktop` (Xvfb + x11vnc +
   noVNC :6080, opt-in `SANDBOX_DESKTOP=1` default-on) embutida na aba
   Desktop da conversa via proxy same-origin `/api/v1/sandboxes/{id}/surfaces/{port}/`
   (funciona em `RoutingMode=internal`). *SPEC-113 entregue.*
5. **Billing tags / cost codes** — metering existe por usuário/org;
   agrupamento arbitrário por tag (cost centers) não existe.
6. **PSP billing** — fora de escopo (Stripe-like), metering já emite os
   contadores necessários.

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

# Gap analysis competitivo — Sandcastle × OpenHands (Cloud/Enterprise) × Devin (Web/Enterprise)

Data: 2026-11-10. Fontes: docs oficiais (`docs.openhands.dev` release notes
0.70/0.81/0.90.1/0.103, `docs.devin.ai`), blogs
(`openhands.dev/blog` — Verification Stack, massive refactors;
`devin.ai/blog` — stacked PRs), código local (`src/`, `.specs/`,
`docs/specs/`). Complementa — e atualiza —
`migration-plan/research/competitive-analysis-devin-openhands-cloud.md`
(2026-10-18): a "Onda A" daquela análise (SPECs 52–64) está **entregue**;
esta revisão cobre o que os dois concorrentes lançaram desde então.

## 1. Sumário executivo

Os dois líderes convergiram no mesmo ponto: **fechar o loop do PR**. O
agente revisa o próprio PR, corrige até o CI ficar verde e prova que
testou — sem o humano tocar no teclado. É exatamente aí que está o maior
gap do Sandcastle: o pipeline hoje termina em "PR criado + status
`sandcastle/run`", enquanto OpenHands e Devin operam o ciclo inteiro
review → autofix → QA → merge-ready.

Desde out/2026, OpenHands Enterprise lançou (0.70–0.103): super-admin
dashboard com gestão de IdPs, org-scoped secrets, budget dashboard do
usuário, OAuth MCP + catálogo, automations visíveis na org + **git sync
de automations**, Azure DevOps completo, **conversas multi-repo**,
**Model Router** gerenciado por admin, Bitbucket Data Center e
**service accounts**. Devin lançou: **Devin Review + Auto-Fix** (commit
status + findings por severidade + push de commits corretivos),
**stacked PRs** (API nativa do GitHub), **Security 2.0 / Code Scans**
(`/scan` → findings entity → remediation sessions), vídeo 60fps +
download, `/handoff` no CLI, **Local Hands** (sessão cloud executa na
máquina do usuário com aprovação), **plugins + marketplace** (3 escopos),
Outposts e custom roles enterprise.

## 2. O que mudou desde out/2026

### 2.1 OpenHands Enterprise (release notes oficiais)

| Versão | Destaques relevantes |
|---|---|
| 0.70 (22/set) | UI alinhada ao Agent Canvas; **org-scoped secrets**; **Your Budget** dashboard (uso + budget mensal); **OAuth MCP** (catálogo GitLab/Atlassian Rovo); **automations visíveis na org** (criador + identidade; admin desativa); **Automation Git Sync** (versiona automations num repo, review via PR) |
| 0.81 (13/out) | **Super Admin Dashboard** `/super-admin` (users/orgs/membership + guided setup); CRUD admin de oauth_providers; pause real de containers; pause+delete de sandboxes idle no worker; filtros/dedup de automations; fim do limite de 64KiB de state |
| 0.90.1 (23/out) | Super Admin gerencia **IdPs** (preset picker, branding, self-registration, match por e-mail); **SAML SP-initiated** + metadata; Keycloak 26.8; **Azure DevOps** completo (repos cross-org, PR+work-item events → automations, Entra sign-in, clone); budget efetivo restante; LiteLLM: não retenta rate-limit, zero-cost models, Azure AI Foundry param-dropping |
| 0.103 (06/nov) | **Multi-Repository Conversations** (web/Slack/Jira; clone de privados de qualquer provider conectado); **Model Router** gerenciado por admin (default ON); **Bitbucket Data Center**; **Service Accounts** (beta — automations rodam como service account); TLS cert próprio; NetworkPolicy de egress opt-in; login por padrão de e-mail; distrust emSign CA |

### 2.2 OpenHands OSS — Verification Stack (mai/jun 2026)

Pipeline público em `OpenHands/extensions` (skills + workflow):

- **Layer 1 — Critic model**: modelo pequeno pontua a trajetória do agente
  durante a run; score baixo → para cedo ou retenta (treinado em traces de
  produção).
- **Layer 2 — Repo-level**: workflow GitHub Actions (`pull_request`
  opened/ready/`review-this` label/reviewer request) rodando skills
  `/codereview` + `/github-pr-review` → comentários inline com prioridade
  (🔴 Critical, 🟠 Important, 🟡 Suggestion, 🟢 Nit); **QA agent** com
  metodologia Understand → Setup → Exercise → Report (PASS/FAIL/PARTIAL +
  evidência: comandos, outputs, screenshots).
- **`iterate` skill**: orquestra push → poll checks → fix → push até verde
  ou blocker humano. Segundo o blog, ~90% dos PRs internos passam pelo bot.

### 2.3 Devin Web/Enterprise (docs + release notes 2026)

- **Devin Review**: auto-review por repo+usuário inscritos (open/push/
  ready); commit status `Devin Review` com condição de passe
  (completed/threads resolved/no findings); findings tipados
  (bugs/security/flags investigate/note) com severidade+confiança;
  **Auto-Fix** empurra commits na branch do PR (gate por allowlist de
  bots — "All bots" causa loop infinito); **re-review skip** quando o
  diff vs base não mudou.
- **CI autofix**: trigger `check_run` nas automations (filtro
  `conclusion=failure`) → sessão lê logs, traça erro→código, empurra fix,
  comenta root cause.
- **Code Scans / Security 2.0**: `/scan` no composer → card de setup →
  sessão-mãe "Agentic MapReduce" (particiona repo entre Devins paralelos);
  **Findings** com pipeline Unassigned → Assigned → PR open e estados
  Open/Reviewed/Dismissed; "Fix with Devin" → remediation sessions;
  scan profiles + effort; Auto Scan schedules; API v3
  `GET /v3/organizations/{org}/code-scans/findings` (filtros + cursor).
- **Stacked PRs**: GitHub native stacked PRs (cada PR tem base na head da
  camada abaixo; merge bottom-up atômico); Devin anuncia o stack,
  monitora CI/review/mergeability por camada e **rebaseia downstream**
  automaticamente em feedback ou merge de camada inferior; converte PR
  grande em stack sob demanda. GitHub.com only (sem GHE/GitLab).
- **Testing & recordings**: pós-PR, testing mode (setup → plan → record →
  attach); vídeo com anotações em momentos-chave; 60fps opcional;
  download no player.
- **Sessão/CLI**: `devin --cloud`, `/cloud`, `/handoff` (local→cloud),
  `/pickup` (cloud→local, checkout do PR branch), `/ssh`; **Local Hands**
  (sessão cloud pede comandos/arquivos na máquina do usuário, aprovação
  por tool call); session insights sob demanda (+ auto para L/XL) via
  API; voice mode com modelo de fala live; side-by-side thread Slack
  mostra quem está na call.
- **Plugins**: bundle de skills+rules+hooks+MCP+subagents; instalável de
  GitHub/git URL/subpasta/local; escopos personal/org/enterprise com
  manifest pessoal sincronizado à cloud; marketplace
  (`CognitionAI/devin-marketplace`) + aviso de segurança + `Connect MCP`;
  enterprise pode ocultar marketplace e filtrar MCPs.
- **DeepWiki**: wiki auto-gerada por repo (diagramas, docs, source links)
  com `.devin/wiki.json`, esforço low/med/high (custo em ACU), Ask Devin
  usa a wiki; MCP público `mcp.deepwiki.com/mcp`
  (`read_wiki_structure`/`read_wiki_contents`/`ask_question`) e privado
  `mcp.devin.ai/mcp` com bearer; suporte multi-branch.
- **Enterprise**: custom roles (account-level + org-level, permissões
  granulares, assign a usuários ou IdP groups), SCIM 2.0 completo,
  IP access lists via API v3, **Outposts** (sessões rodam em workers na
  rede do cliente; fila esperando outpost online; allowlist dedicada para
  git traffic; default-platform via API).

### 2.4 Mercado/benchmarks

- SWE-bench Verified **saturada e contaminada** (top modelos ~74–83%;
  OpenAI parou de reportar). Métricas atuais: **SWE-bench Pro** (642
  tasks v2, ~30% do split público sinalizado quebrado) e
  **Terminal-Bench** (2.0/2.1→4.1: 66–89 tasks, verifiers endurecidos,
  leaderboard com custo/tokens — Opus 5.5 em 65.8%).
- Métrica que importa para produto: **taxa de PR aceito em produção**
  (~35–50% relatado) e time-to-green.
- **A2A 1.0** estável sob Linux Foundation/AAIF (150+ orgs, integrações
  Google/Microsoft/AWS), mas adoção real de agent cards ainda modesta
  (~600 repos públicos); MCP consolidado como padrão de ferramentas.

## 3. Estado do Sandcastle (evidência de código)

| Capacidade | Evidência |
|---|---|
| Webhook SCM | `GitHubWebhookService` só roteia `@sandcastle` em `issue_comment`, `pull_request_review_comment`, `pull_request_review` — **não** escuta `pull_request`, `check_run`, `check_suite`, `workflow_run`, `status`, `push` (`src/Sandcastle.Infrastructure/Scm/GitHubWebhookService.cs:234-249`) |
| PR create + status | `ScmPullRequestService.CreateAsync` + `ScmStatusReporter` posta `sandcastle/run` no head SHA (`Scm/ScmPullRequestService.cs`) |
| Provider SCM | `IScmProvider`: `CreatePullRequestAsync`, `GetPullRequestAsync`, `CreateIssueCommentAsync`, `ReplyToReviewCommentAsync`, `SetCommitStatusAsync` — **sem** `CreateReviewAsync` (inline comments), `GetPullRequestFilesAsync`, `GetCheckRunsAsync`, `GetCheckRunLogsAsync` (`Application/Scm/ScmContracts.cs:102-138`) |
| Automations | Apenas schedule (`interval/daily/weekly/once`) + token hooks — **sem triggers por evento SCM** (`Application/Automations/AutomationContracts.cs`) |
| Delegação | `IDelegationService` real (SPEC-62): DelegateAsync, ListSiblings, WakeParentOnCompletion; depth=2, siblings=8 |
| Swarm | `SwarmOrchestrator` é **stub** (`Task.Delay`, `Application/Swarm/SwarmOrchestrator.cs`) |
| Indexação | `RepoIndexer.RunIndexingAsync` varre `Directory.GetCurrentDirectory()` do **servidor de API** — não clona o repo, ignora `branch`, não grava `CommitSha` (`Infrastructure/Indexing/RepoIndexer.cs:117-128`) |
| Ask | `IAskModeService.AskAsync` + `PromoteToAgentAsync` com citações (SPEC-60) |
| Insights | `SessionInsightService.AnalyzeSessionAsync(id, force)` on-demand + stats — alinhado ao Devin pós-mudança |
| MCP server | `Sandcastle.Mcp` stdio, 12 tools `sc_*` — sem wiki/ask/findings |
| Slack | `ChatOpsReporter` posta `summary` do agente com `SecretRedactor` mas **sem sanitizar** `@channel`/`<!here>` (`Infrastructure/ChatOps/ChatOpsReporter.cs:41-49`) |
| Commit author | `GitOptions.AuthorName/AuthorEmail` global (`sandcastle-agent`) — sem config por usuário/org, sem `Co-authored-by` |
| Multi-repo | `selected_repository` único em `CreateConversationRequest` |
| Findings | nenhuma entidade `finding`/`scan`/`review` persistida |

## 4. Matriz de gaps confirmados (2026-11)

| # | Gap | Concorrente | SC | Prioridade | Esforço |
|---|---|---|---|---|---|
| G1 | **PR review agent**: auto-review em `pull_request` (open/sync/ready), comentários inline com prioridade, regras do repo, commit status com pass-condition, skip re-review em diff inalterado, findings persistidos | OH Verification Stack L2 · Devin Review | ❌ | **P0** | Alto |
| G2 | **CI autofix loop**: triggers `check_run`/`check_suite`/`workflow_run`/`status` → sessão lê logs → fix na branch → poll até verde; cap 3 tentativas; anti-loop por autor | Devin autofix · OH iterate | ❌ | **P0** | Alto |
| G3 | **QA agent com evidência**: Understand→Setup→Exercise→Report; PASS/FAIL/PARTIAL + screenshots/vídeo anexados ao PR | OH QA plugin · Devin recordings | ◐ (Playwright no sandbox, SPEC-131; sem pipeline nem evidência) | **P1** | Alto |
| G4 | **Code scans**: `/scan` → sessão-mãe fan-out (delegate) → findings entity + página org + "Fix with Sandcastle" | Devin Security 2.0 | ❌ (stub Swarm) | **P1** | Alto |
| G5 | **Batch refactor**: grafo de dependências → lotes → verify/fix → PR por lote (opcional stacked) + tracking | OH Refactor SDK · Devin stacked PRs | ❌ | **P1** | Alto |
| G6 | **Repo wiki + MCP**: indexação real por repo/branch (hoje lê o cwd da API!), wiki gerada (tópicos/diagramas/citações), esforço configurável, exposta via MCP HTTP | Devin DeepWiki | ◐ (Ask só; index quebrado) | **P1** | Alto |
| G7 | **Multi-repo conversations**: N repos por conversa, clone paralelo, tools multi-root | OH 0.103 · Devin `/repo` | ❌ | **P1** | Médio-alto |
| G8 | Quick wins de produto (abaixo) | ambos | ❌ | **P2** | Baixo |
| G9 | Plugin bundles + marketplace (skills+hooks+MCP+rules; escopos user/org; manifest) | Devin plugins · OH `extensions` | ◐ (skills soltas) | **P2** | Alto |
| G10 | CLI handoff local↔cloud + `--cloud` + `/pickup` | Devin CLI | ❌ | **P2** | Médio |
| G11 | Local Hands / runner híbrido (sessão cloud executa na máquina do usuário c/ aprovação) | Devin Local Hands | ◐ (runtime-api split) | **P2** | Alto |
| G12 | Automação enterprise: git sync de automations, automations visíveis na org, service accounts como identidade de run | OH 0.70/0.103 | ❌ | **P2** | Médio |
| G13 | Super-admin dashboard (users/orgs/membership + IdP presets) | OH 0.81/0.90 | ◐ (admin UI existe; sem super-admin cross-org) | **P2** | Médio |
| G14 | Stacked PRs nativos (GitHub stacked API), sessão vídeo 60fps, Sentry/PagerDuty/Teams, benchmark adapters (Terminal-Bench/SWE-Pro), knowledge analytics, OpenAI Responses gateway, Bitbucket DC | ambos | ❌/◐ | **P2** | variável |
| G15 | A2A protocol (agent card + tasks), voice mode, data-analyst agent | ecossistema | ❌ | **P3** | — |

### Quick wins (G8 — bundle de uma SPEC)

1. **Sanitizar pings Slack**: `@channel`, `@here`, `<!channel>`, `<!here>`,
   `<!everyone>` em todo texto outbound (`ChatOpsReporter`, `SlackChatSink`)
   — summary do agente pode pingar o canal inteiro.
2. **Commit authorship configurável**: author por usuário/org +
   `Co-authored-by: {user}` trailer (hoje só global `GitOptions`).
3. **Preview renderizado** de `.md`/`.patch` no file viewer/artifacts.
4. **Budget restante**: card "Your Budget" (alocação mensal, consumo,
   restante) no Settings — `IUsageService` já mede.
5. **Contador "X/Y repos indexados"** na UI de Ask/indexing.

## 5. Roadmap → SPECs

| Onda | SPECs | Racional |
|---|---|---|
| A — loop do PR | **140** pr-review-agent (G1) · **141** ci-autofix-loop (G2) · **142** qa-agent-evidence (G3) | O que os dois vendem como diferencial #1; reusa webhook + provider + delegate |
| B — escala | **143** code-scans (G4) · **144** batch-refactor (G5) | Sobre o motor pai/filha do delegate (SPEC-62); substitui stub Swarm |
| C — conhecimento | **145** repo-wiki-mcp (G6) | Corrige indexer + entrega wiki/MCP; sinérgico com Ask |
| D — produto | **146** multi-repo-conversations (G7) · **147** product-polish (G8) | Paridade OH 0.103 + wins baratos |
| E — ecossistema | (futuro) G9–G14: plugins/marketplace, CLI handoff, Local Hands, automação git sync/service accounts, super-admin, stacked PRs nativos, integrações, benchmarks | SPECs quando priorizados |
| F — fronteira | (futuro) G15: A2A, voice, analyst | Observar adoção; fora da v1.0 |

Decisões conservadoras padrão (override por SPEC se aprovado):
review começa comment-only (nunca bloqueia merge por default);
autofix para em 3 tentativas; A2A fora da v1.0; stacked PRs só em G5 como
modo opcional e GitHub-only.

## 6. Posição defensável a manter

Sandcastle continua a **única opção MIT** com recursos enterprise reais
(RBAC `perm:*`, SCIM, quotas, auditoria, BYOR) na linhagem OpenHands —
upstream cobra isso via Polyform, Devin é fechado. Entregar G1–G3 em
código aberto é diferencial direto: é exatamente o que os dois cobram.

## 7. Limitações

- Pesquisa via `web_search`/`webfetch` (Tavily rate-limit intermitente) —
  a página de novidades set/2026 do OH e partes da doc Devin foram lidas
  via snippets/llms.txt, não integralmente.
- Gaps verificados por busca de termo + leitura dos arquivos centrais,
  não por revisão completa do código.
- `OpenHands/extensions` (skills/plugins de review/QA/iterate) — licença
  não confirmada nesta revisão; SPECs implementam conceitos próprios.

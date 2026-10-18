# Análise competitiva — OpenHandsNET × OpenHands Cloud × Devin Web

Data: 2026-10-18. Fontes: docs oficiais (`docs.openhands.dev`, `docs.devin.ai`),
DeepWiki (`OpenHands/OpenHands`, `OpenHands/software-agent-sdk`), código local
(`src/`, `.specs/`). Baseline upstream: `docs/reference/upstream-baseline.md`
(SHAs ~2026-10-12). Complementa `docs/comparison.md` (visão por SPEC) com a
visão **por produto/mercado**.

## 1. Sumário executivo

- **OpenHands Cloud** é um SaaS de "conversas em sandbox": escopo
  deliberadamente estreito — start-task assíncrono, search de eventos,
  pause/resume de sandbox, integrações GitHub/GitLab/Bitbucket/Slack/Jira Cloud.
  O diferencial enterprise (SSO/SAML, RBAC multiusuário, SCM self-hosted,
  Azure DevOps, Jira DC) está no pacote **Enterprise** (Polyform/self-hosted).
- **Devin Web** é uma plataforma completa de "funcionário digital": sessões
  com IDE/browser/shell/computer-use, **Ask mode** (Q&A read-only sobre repos
  indexados), **DeepWiki**, playbooks, knowledge com sugestões e "dreaming",
  **managed Devins** (orquestração paralela entre sessões), automações por
  gatilho, Security Swarm/Code Scans, API v3 com service users + RBAC +
  impersonation, Outposts (workers self-hosted), Devin Connect (túnel
  privado), SCIM, IP allowlist, guardrails de prompt-injection, gravação de
  testes em vídeo, VMs Windows/macOS, e cobertura enorme de integrações
  (Slack/Teams/GitHub/GHE/GitLab/Bitbucket/Azure DevOps/Linear/Jira/
  PagerDuty/Pylon/Databricks/MongoDB/Artifactory…).
- **OpenHandsNET** já cobre ~90% do escopo funcional do OpenHands Cloud e
  ultrapassa o upstream OSS em vários pontos (IDE workbench, checkpoints,
  attention inbox, i18n, routine tool, OmniRoute, enterprise de verdade em
  MIT). Contra Devin, o gap não é o "loop do agente" — é a **camada de
  produto ao redor**: integrações prontas, identidade enterprise (SCIM/IdP
  groups), insights de sessão, orquestração multi-sessão e periféricos de UX.

## 2. Posicionamento em uma página

| Eixo | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Natureza | SaaS de conversas+sandbox | Plataforma "AI software engineer" | Self-host MIT, paridade cloud+enterprise |
| Código | núcleo MIT; Cloud/Ent fechado | fechado | **aberto (MIT)** |
| Tenancy | single-user (Cloud) / orgs (Ent) | orgs + enterprise multi-org | orgs/RBAC nativo |
| Foco | API-first de conversas | fluxo de trabalho E2E (ticket→PR) | paridade programática + UX Devin-like |

## 3. Comparativo detalhado por domínio

### 3.1 Agente, loop e eventos

| Capacidade | OpenHands Cloud/SDK | Devin Web | OpenHandsNET (estado) |
|---|---|---|---|
| Modelo de eventos | V1 discriminated union (`kind`) | proprietário | **V1 completo** (SPEC-02) + `streaming_delta` efêmero (SPEC-34) |
| Loop | `Agent.step()`, agent-server | proprietário | `AgentRunner` + `IAgentRunnerHost` (SPEC-09) |
| Send-message durante execução | sim (`send-message`) + **pending-message queue** | sim (+ steering rico) | `POST /{id}/send-message` — injeção em run ativa via runner host; **sem endpoint de fila "pending" separado** |
| Pause/resume | sim | sim | sim (SPEC-16, wake-on-message) |
| **Fork de conversa** | sim (SDK `fork`) | — | **não** |
| Goal loop (judge-driven) | sim (SDK conversation-goals) | — (Ask/Agent modes) | **não** |
| Critic / iterative refinement | sim (SDK, experimental) | Devin Review (outra superfície) | **não** |
| Stuck detector | sim | sim | parcial — heartbeat/watchdog (SPEC-41) |
| Ask-oracle (2ª opinião stateless) | sim | — | **não** (OmniRoute permitiria) |
| Subagentes | `task` tool (mesmo sandbox) | subagents CLI + **managed sessions** (VMs separadas) | `task`/`task_status`/`task_result` in-sandbox (SPEC-28) |
| **Orquestração multi-sessão** | automations + API externa | **managed Devins**: coordenador cria sessões filhas em VMs próprias, wake on child-event | **não** — `task` roda no mesmo sandbox; `routine` agenda, não spawns siblings |

### 3.2 Sandbox e runtime

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Engine | k8s multi-host (Ent) | VMs (Linux **+ Windows + macOS**) | Docker (prod) + `KubernetesSandboxEngine` (SPEC-37/49) |
| Warm pool | sim | sim | sim (Docker + k8s `ClaimWarmAsync`) |
| Sleep/wake | pause/resume | sim | sim (pause/stop/resume + PVC retain) |
| **Sandbox specs selecionáveis** | `search_sandbox_specs` por conversa | environments/blueprints por repo | blueprint `openhandsnet.yaml` + `SandboxProfile` — verificar paridade de "spec por conversa" |
| Surfaces | terminal web + preview | IDE VS Code + browser interativo + shell + computer-use desktop | gateway + terminal + VS Code + preview + ACP (SPEC-40) |
| **Computer-use / desktop GUI** | — | sim (desktop completo, Android emulator, iOS sim) | **não** (somente browser headless Playwright, SPEC-18) |
| **Browser auth profiles** | secrets | sim — login 1×, perfil salvo p/ próximas sessões | **não** — secrets by-ref, sem persistência de perfil/cookies |
| **Gravação de sessão** | rrweb browser recording (SDK) | **vídeo de testes como prova** | screenshots pontuais (BrowserTab) — sem recording/replay |
| Egress allowlist | Ent | security profiles | Squid sidecar por domínio (SPEC-19) |

### 3.3 Superfícies de trabalho (UI)

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Chat/streaming | canvas + stream | chat + Progress tab unificado | streaming + reasoning + heartbeat (SPEC-41) |
| IDE | file viewer | VS Code web completo | **Monaco workbench + SCM + search + tests** (SPEC-42) — supera ambos |
| **Ask mode (read-only Q&A)** | — | **sim** — code search indexado + citações → handoff p/ Agent | **não** — maior gap de UX vs Devin |
| Side chats | — | sim (Q&A sobre a sessão sem interromper) | **não** |
| Inbox/notificações | notificações | attention UI | **attention inbox** persistido (SPEC-44) |
| Composer | básico | rico | @menções /slash drafts uploads edit-resend (SPEC-45) |
| Checkpoints | snapshots (Ent) | snapshots | snapshots/turno + revert c/ drift guard (SPEC-43) |
| Compartilhamento | **public share** read-only | links internos de sessão | **não** |
| i18n | en | en | **8 locales** (SPEC-46) |
| Command palette | sim | sim | sim (SPEC-26) |

### 3.4 LLM e modelos

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Roteamento | LiteLLM proxy | **Adaptive router + Fusion** (lead+sidekick) | OmniRoute catalog + roles + fallback (SPEC-23) |
| BYOK | BYOR LiteLLM (Ent/Cloud) | ChatGPT plan linking; enterprise model allowlists | BYOR LiteLLM virtual keys (SPEC-50) |
| Condensação | summarizer | proprietário | meter + condenser + ledger + triggers (SPEC-22) |
| Model switching mid-run | `switch_profile`/`switch_acp_model` | sim | sim (mesmos endpoints; bloqueado durante run ativa — upstream igual) |

### 3.5 API e extensibilidade

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Conversas | start-task + search + send-message + patch + delete + **download zip** | v3 sessions CRUD + messages + tags + events | paridade quase total (`/api/v1/app-conversations*`) — falta `/download` zip? *(CLI `export` existe; verificar endpoint HTTP)* |
| Eventos | search/count/batch-get | session events API | search + SignalR live |
| Sandboxes | search/start/pause/resume/delete/specs | (não expostos como tal) | paridade |
| **Service users + RBAC na API** | API key por usuário | `cog_*` service users c/ roles + **`create_as_user_id` impersonation** + PATs | `sk-*` keys — **sem service users escopados nem impersonation** |
| Webhooks | sim (agent-server) | automations | `WebhookEndpoints` + automation triggers (SPEC-47) |
| OpenAPI publicado | sim | v3 OpenAPI yaml | **sim** (SPEC-51, `/openapi/v1.json` + Scalar) |
| MCP | client + marketplace | client + marketplace + **Devin MCP reverso** (ferramentas externas controlam o Devin) | client + OAuth (SPEC-07) — **sem MCP server próprio exposto** |
| Plugins/pacotes | plugins (skills+hooks+MCP+agents) + registry `OpenHands/extensions` | plugins + marketplace + team marketplace | skills + hooks soltos — **sem formato de bundle/marketplace** |
| Skills determinísticas | keyword + **path-triggered (glob→inject)** + org/user/global | SKILL.md + rules | triggers + invoke + repo/org (SPEC-11/25) — **path-triggered a verificar** |

### 3.6 Integrações

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| GitHub | app + PR comments | app + stacked PRs + PR templates + bot-comment settings | **fase 3 — não implementado** (`git_commit` apenas) |
| GitLab/Bitbucket/Azure DevOps | Cloud: GitLab+Bitbucket; Ent: +GHE+AzDO+BBDC | todos + self-managed | **não** |
| Slack/Teams | Slack app (Cloud) + Jira Cloud | Slack + Teams + auto-triage channel | **não** (só webhooks genéricos) |
| Linear/Jira | Jira Cloud (Cloud), Jira DC (Ent) | ambos | **não** |
| Incidentes | — | PagerDuty + Devin Oncall | webhook triggers cobririam parcialmente |
| Outros | — | Databricks/MongoDB/Pylon/Artifactory | — |

### 3.7 Enterprise, identidade e compliance

| | OpenHands Cloud/Ent | Devin Web | OpenHandsNET |
|---|---|---|---|
| SSO | SAML/SSO (Ent) | OIDC+SAML+SCIM+IdP groups | OIDC genérico — **sem SAML, sem SCIM, sem IdP-group→role** |
| RBAC | multi-user (Ent) | custom roles granulares | orgs + roles + `perm:*` policies (SPEC-08) |
| **IP access lists** | — | sim (v3 API) | **não** (há `ApiRateLimitMiddleware`; allowlist CIDR é gap fácil) |
| Quotas | sim | **usage policies por usuário + IdP tier + "request more"** | quotas por plano (SPEC-20) — **sem limites por usuário/tier de IdP** |
| Auditoria | sim | audit logs (v3 enterprise) | `IAuditService` + admin UI |
| **Guardrails de input** | security analyzer (ações) | **AI Guardrails — screening de prompt-injection/exfiltração na entrada** | `ISecurityAnalyzer` nas **ações do agente** — input do usuário não é screenado |
| **Attribution/copyright filter** | — | sim | não |
| Chaves/cripto | — | CMK (KMS) | secrets mascarados + SHA-256 (hard rule) |
| Rede privada | Ent self-host | **Devin Connect** (túnel outbound/PrivateLink) + Outposts | self-host nativo resolve o caso — mas **não há modelo "control-plane SaaS + workers remotos"** |

### 3.8 Automação e operações

| | OpenHands Cloud | Devin Web | OpenHandsNET |
|---|---|---|---|
| Schedules | automations (cron + eventos GitHub/webhook) | automations + scheduled sessions | routines + webhook triggers + `routine` tool (SPEC-47) |
| **Templates prontos** | prebuilt (PR reviewer, repo monitor, Slack monitor) | automations em linguagem natural + galeria | **não** — automação é do-zero |
| **Session insights** | — | análise pós-sessão (issues, learnings, prompt melhorado) | `agent_diagnostic` events existem — **não há produto de insights** |
| Eval | WebArena/OSWorld | interno | `openhandsnet eval` + llm_judge (SPEC-39) |
| Deploy | Helm+KOTS+k0s (Ent) | SaaS + dedicated + Outposts | compose + Helm completo (SPEC-49) |
| Usage/billing | Stripe (Cloud) | ACU metering + analytics | `IUsageService` metering (sem PSP — escopo OK) |
| Knowledge/memória | `.openhands/memory` | **knowledge notes + sugestões + dreaming (dedup auto)** | remember/recall/forget/promote multi-escopo (SPEC-30) — **sem pipeline de sugestões/revisão** |

## 4. O que OpenHandsNET tem que nenhum dos dois tem

1. **MIT + paridade enterprise** — orgs/RBAC/quotas/audit/BYOR em código aberto
   (upstream cobra via Polyform; Devin é fechado).
2. **IDE workbench embutido** com edição inline, SCM e busca (nem OH Cloud nem
   Devin permitem editar com esse nível de integridade na mesma UI — Devin tem
   VS Code web completo; OH Cloud só file viewer).
3. **i18n real** (8 locales, pt-BR default).
4. **Checkpoints com revert** e drift guard.
5. **`routine` tool** — o agente cria as próprias automações.
6. **Stack única .NET 10** — agente, API, UI, CLI num só runtime.
7. **Attention inbox persistido** com projeção por conversa.

## 5. Matriz de gaps priorizada

Legenda: ✅ existe · ◐ parcial · ❌ ausente

| # | Gap | OH Cloud | Devin | OHNET | Esforço | Impacto |
|---|---|---|---|---|---|---|
| G1 | GitHub App: PRs, comments, @mention triggers, status checks | ✅ | ✅ | ❌ (fase 3) | Alto | **crítico** — é o fluxo principal dos dois produtos |
| G2 | Slack (e depois Teams): iniciar sessão, thread sync, notificações | ✅ Slack | ✅ | ❌ | Médio | **crítico** — canal de entrada nº 1 enterprise |
| G3 | Providers SCM: GitLab → Bitbucket → Azure DevOps | ✅ | ✅ | ❌ | Médio/provider | alto p/ enterprise self-host |
| G4 | Ticketing: Linear/Jira → start conversation | ✅ Jira | ✅ ambos | ❌ | Médio | alto — caso de uso nº 1 do Devin |
| G5 | Public share de conversa (link read-only) | ✅ | ◐ | ❌ | Baixo | médio — marketing/depuração |
| G6 | Service users (`svc_*`) c/ escopos RBAC + `create_as_user_id` | ◐ | ✅ | ❌ | Médio | alto — automações/CI auditáveis |
| G7 | SCIM 2.0 + IdP-group→org/role mapping | ◐ SSO | ✅ | ❌ | Médio-alto | gate de procurement enterprise |
| G8 | Ask mode (Q&A read-only sobre repo indexado) | ❌ | ✅ | ❌ | **Alto** | alto — feature-assinatura do Devin |
| G9 | Session insights (análise pós-run: issues, prompt sugerido) | ❌ | ✅ | ◐ (eventos `agent_diagnostic`) | Médio | médio-alto |
| G10 | Spawn sibling conversations (tool `delegate`/`spawn_conversation` + wake coordenador) | ◐ via API | ✅ managed Devins | ❌ | Médio | alto — multiplica throughput |
| G11 | Pending-message queue (fila explícita p/ msg pré-READY) | ✅ | ✅ | ◐ (send-message valida sandbox) | Baixo | médio |
| G12 | Browser auth profiles + site cookies/TOTP secrets | ◐ | ✅ | ❌ | Médio | médio — desbloqueia tasks web autenticadas |
| G13 | IP access list (CIDR) na API/webapp | ❌ | ✅ | ❌ | **Baixo** | médio |
| G14 | Templates/prebuilt automations (PR reviewer, repo monitor) | ✅ | ✅ | ❌ | Baixo | médio — onboarding |
| G15 | Path-triggered skills (glob→inject determinístico) | ✅ | ✅ rules | ◐ | Baixo | médio |
| G16 | Plugin bundles + registry/marketplace | ✅ | ✅ | ❌ | Alto | médio |
| G17 | Fork de conversa | ✅ | ❌ | ❌ | Baixo | baixo-médio |
| G18 | Gravação de sessão (rrweb/vídeo) como prova | ◐ rrweb | ✅ | ❌ | Médio | médio — confiança/PR review |
| G19 | Goal-loop (judge-verified completion) | ✅ | ◐ | ❌ | Médio | médio |
| G20 | AI guardrails no input (prompt-injection screen) | ◐ | ✅ | ❌ | Médio | médio (segurança) |
| G21 | Usage limits por usuário + tiers IdP + request-more | ◐ | ✅ | ◐ (por plano) | Médio | médio |
| G22 | MCP server reverso (externos controlam o OpenHandsNET) | ❌ | ✅ Devin MCP | ❌ | Médio | diferenciador ecosistema |
| G23 | Workers remotos/"outposts" (control plane↔data plane) | ❌ | ✅ | ◐ runtime-api split dá a base | Alto | enterprise distribuído |
| G24 | Side chats (Q&A sobre a sessão sem interromper) | ❌ | ✅ | ❌ | Médio | UX |
| G25 | Download trajectory zip (endpoint) | ✅ | ◐ | ◐ CLI export | **Baixo** | paridade API |
| G26 | Devin Coach (nudge de qualidade do prompt) | ❌ | ✅ | ❌ | Médio | baixo |
| G27 | Multi-OS sandboxes (Windows/macOS) | ❌ | ✅ | ❌ | Muito alto | fora de escopo razoável |
| G28 | Computer-use desktop (GUI apps, emulator Android/iOS) | ❌ | ✅ | ❌ | Muito alto | fora de escopo razoável |
| G29 | Voice mode | ❌ | ✅ | ❌ | Alto | baixo |
| G30 | App deployments (hosting de apps construídos) | ❌ | ✅ | ❌ | Alto | baixo p/ self-host |
| G31 | Attribution filtering (copyright scan do código gerado) | ❌ | ✅ | ❌ | Alto | compliance enterprise |
| G32 | Dynamic workflows (DSL Python determinístico) | ❌ | ✅ | ❌ | Alto | médio — eval+subagents cobrem parcialmente |
| G33 | DeepWiki-like repo docs/diagrams | ❌ | ✅ | ❌ | **Alto** | sinérgico com G8 (mesma indexação) |

## 6. Recomendações

### Onda A — "Fluxo de trabalho E2E" (o buraco maior: ticket→PR)

O OpenHandsNET tem um **motor de agente maduro num produto que ainda exige que
o usuário abra a UI para tudo**. Os dois concorrentes são usados majoritariamente
via gatilhos externos. Sugerido como SPECs 52–56:

1. **SPEC-52 — GitHub App + SCM providers**: GitHub App próprio (instalação
   por org), criação de PR a partir do workspace, resposta a review comments,
   `@openhandsnet` mention → start-task, commit status de volta ao PR.
   Abstrair `IScmProvider` para GitLab/Bitbucket/AzureDevOps em SPEC-53.
2. **SPEC-54 — ChatOps**: Slack app (slash `/openhandsnet`, @mention, thread
   sync bidirecional via SignalR/webhook). Teams depois.
3. **SPEC-55 — Ticketing triggers**: Linear + Jira → start-task com contexto
   do ticket; write-back de status/link da conversa.
4. **SPEC-56 — Public share + trajectory download**: token de compartilhamento
   read-only + `GET /app-conversations/{id}/download` (zip) — paridade OH Cloud.

### Onda B — "Enterprise readiness" (gates de procurement)

5. **SPEC-57 — Service users + impersonation**: `svc_*` keys com escopos RBAC,
   `create_as_user_id`, atribuição de usage — necessário p/ automações de CI.
6. **SPEC-58 — SCIM 2.0 + IdP groups**: provisioning/deprovisioning e mapeamento
   grupo-IdP→org/role (+ usage tiers por grupo, evolução do SPEC-20).
7. **SPEC-59 — Hardening de borda**: IP allowlist CIDR (org/global),
   screening de prompt-injection no `send-message` (novo `IInputGuard`
   antes do `ISecurityAnalyzer`), rate-limit por identidade.

### Onda C — "Inteligência de produto" (o que faz Devin parecer mágico)

8. **SPEC-60 — Repo indexing + Ask mode**: índice de código (ripgrep→embeddings
   opcional), endpoints de code search, modo de conversa read-only com citações
   e handoff "plan→agent session". Base para G33 (docs auto-gerados).
9. **SPEC-61 — Session insights**: job pós-run que emite `session_summary`
   (issues, dead-ends, tokens por fase, prompt sugerido) usando LLM role
   `analysis` do OmniRoute; surface na UI + CLI `insights`.
10. **SPEC-62 — Managed conversations**: tool `delegate` (cria app-conversation
    irmã via API interna, corre em sandbox próprio) + wake do coordenador em
    evento-filho + agregação de resultados. É o "managed Devins" do Devin sobre
    a infra de `task`/`routine` que já existe.
11. **SPEC-63 — Knowledge pipeline**: sugestões automáticas de memória pós-sessão
    (fila de revisão, dedup via recall+LLM, promote org) — evolui SPEC-30 para
    o modelo "knowledge + suggestions" do Devin.
12. **SPEC-64 — Browser profiles**: storage state persistido por site (cookies/
    TOTP via secrets), reaproveitando SPEC-18 + SecretNamesJson.

### Onda D — paridade pontual e ecossistema

13. Fork de conversa · pending-message queue endpoint · path-triggered skills ·
    templates de automação · MCP server reverso (`openhandsnet-mcp`) ·
    session recording (screenshots→vídeo em artifacts) · goal-loop mode.
    Cada um vira SPEC pequena quando priorizado.

### Não perseguir (fora de escopo honesto)

Multi-OS VMs (G27), computer-use desktop (G28), voice mode (G29), app hosting
(G30), attribution filtering (G31) — diferenciadores do Devin que dependem de
infra proprietária ou rendem pouco para um self-host open-source. Revisitar
somente se houver demanda real.

## 7. Leitura estratégica

- **Contra OpenHands Cloud**: paridade funcional está essencialmente alcançada.
  O que falta é a **periferia SaaS**: GitHub app, Slack, Jira, public share,
  pending queue. Todos são projetos de integração, não de arquitetura.
- **Contra Devin**: o fosso não é sandbox nem loop — é **produto**: Ask
  mode/indexação, managed sessions, insights, knowledge pipeline, ChatOps e a
  superfície de integrações enterprise (SCIM, service users, IP allowlist).
  A boa notícia: quase todos os gaps são **incrementais sobre SPECs já
  entregues** (SPEC-47 automations, SPEC-28 task, SPEC-30 memory, SPEC-18
  browser, SPEC-20 quotas) — não exigem repensar a arquitetura.
- **Vantagem defensável a preservar**: MIT + self-host + enterprise real +
  stack .NET única + IDE workbench + i18n. Nenhum concorrente combina isso.

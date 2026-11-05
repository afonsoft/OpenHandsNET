# SPEC — 123 · Tool infra — hooks, permissions, deferred loading, prompt assets

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `tool-infra-hooks-permissions` |
| Type | `Infra` |
| Status | `In review` |
| Ticket | #325 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/` (+`ChatTools`-equivalente), `Application/Agent/`, `SystemPrompt/`, `agents/`, `prompts/` |

## 1. User Story

**As a** operador do OpenHandsNET, **I want** a infraestrutura de
tools no nível do OWUIC — hooks de ciclo de vida, permissões por
ruleset + classificador inteligente, carregamento diferido de tools
(tool_search), guarda SSRF e prompts/agents embutidos — **so that**
runs escalem em contexto e segurança sem reescrever o loop do agente.

## 2. Contexto / escopo

AS-IS: `ToolRegistry` + `PermissionedToolRegistry` +
`AutonomyGuardrail` + `SecretRedactor` + `SubagentPresets` +
`AgentCatalogService` (SPEC-95: descobre `*.agent.md` em camadas
mas **não embute nenhum** — `find` = 0 arquivos) +
`SystemPromptComposer`/contributors (SPEC-90).

OWUIC (`ChatTools/`): `BuiltinToolRegistry` com tiers, +
infraestrutura inteira ausente aqui:

| Item OWUIC | Função | OHN |
|---|---|---|
| `HookConfigLoader`/`CommandHookRunner`/`LifecycleHookService`/`PromptHookEvaluator`/`HookDefinition` | hooks command/lifecycle/prompt (repo/user config) | — |
| `PermissionRuleset`/`ToolApprovalPolicy`/`SmartPermissionClassifier`/`ToolCallRiskClassifier`/`CommandRiskClassifier` | ruleset declarativo + judge LLM (`prompts/smart-permission-judge.md`) | `PermissionedToolRegistry`+`AutonomyGuardrail` (sem ruleset/judge) |
| `DeferredToolState`/`ToolIndexService`/`tool_search`/`tool_install` | deferred loading — tools entram no contexto só quando descobertas | — |
| `SsrfGuard` | blocklist de hosts em fetch/browser | verificar egress allowlist (SPEC-19) |
| `PlanFileService`/`PlanSpecTemplate` | `.openwebui/plans/*.md` | — (tool-side em SPEC-121) |
| `agents/*.agent.md` ×4 embutidos | general/explore/researcher/code-reviewer | 0 embutidos |
| `prompts/advisor-system.md` | segundo-opinion advisor | — |
| `ExternalAccessRegistry` | caps de acesso externo | parcial (egress) |

Fora de escopo: tools novos (SPEC-121/122); UI (SPEC-120).

## 3. Requisitos

- RF-001 — **Hooks engine**: `HookDefinition` +
  `LifecycleHookService` — pontos `session_start`, `pre_tool`,
  `post_tool`, `user_prompt_submit`, `agent_stop`; fonte
  `.openhandsnet/hooks.json|yaml` (repo) + user config; command hooks
  executam via executor com timeout+output cap; prompt hooks podem
  injetar contexto (via contributor existente). Bloqueio de hook =
  erro de tool auditável.
- RF-002 — **Permission ruleset**: `PermissionRuleset` declarativo
  (yaml/json em repo+user): regras `tool|pattern` →
  `allow|deny|confirm` com escopo (session/workspace); precedência
  deny > confirm > allow; integra `PermissionedToolRegistry` atual
  (não substituir — composição).
- RF-003 — **Smart permission judge**: `SmartPermissionClassifier`
  — chamada LLM leve (modelo pequeno via `ModelRouter`) avaliando
  tool-call borderline com prompt
  `prompts/smart-permission-judge.md` embutido; cache por assinatura;
  fail-safe → `confirm` (nunca allow silencioso).
- RF-004 — **Risk classifiers**: `CommandRiskClassifier` (bash:
  destrutivo/rede/escrita-fora-workspace → confirm) e
  `ToolCallRiskClassifier` genérico; alimentam RF-002/003 e o card de
  permissão (SPEC-120 RF-002).
- RF-005 — **Deferred tool loading**: `ToolIndexService` (índice
  name+description+embedding-lite/keyword p/ tools não-core +
  `mcp__*`), `DeferredToolState` por run e tool `tool_search`
  (retorna top-N schemas sob demanda) — catálogo MCP cresce sem
  inchar o system prompt. Core tools sempre carregados.
- RF-006 — **SSRF verify**: auditar `web_fetch`/`browser`/futuros
  fetchers contra allowlist de egresso (SPEC-19); se houver bypass
  (DNS/direct-ip/metadata 169.254.x), portar `SsrfGuard` — verdict
  documentado na SPEC em implementação.
- RF-007 — **Plan files**: `PlanFileService` — `.openhandsnet/plans/`
  no workspace (cria/lista/lê planos; par com `write_plan` SPEC-121).
- RF-008 — **Prompt assets embutidos**: `prompts/advisor-system.md`
  (advisor = segunda opinião consultiva invocável via tool futura ou
  contrib) + `prompts/smart-permission-judge.md` (RF-003) como
  EmbeddedResource; convenção igual `Skills/Embedded/*.md` no csproj.
- RF-009 — **Agent defaults embutidos**: `agents/explore.agent.md`,
  `researcher.agent.md`, `code-reviewer.agent.md`, `general.agent.md`
  (frontmatter name/description/tools/blocking/max_nesting — parser
  SPEC-95 já existe; conteúdo adaptado dos presets OHN +
  referências OWUIC).
- RF-010 — **ExternalAccessRegistry**: registro único das saídas
  externas (browser/fetch/MCP/webhooks) p/ auditoria — compõe com
  egress existente.

## 4. Acceptance

- [ ] Hook `pre_tool` de repo bloqueia uma tool-call (teste prova
      deny auditável com reason).
- [ ] Ruleset `deny: bash:rm -rf` dispara deny; `confirm` abre fluxo
      de confirmação atual; judge LLM retorna veredito estruturado
      com fail-safe confirm.
- [ ] `tool_search` lista apenas schemas relevantes; catálogo com
      100+ `mcp__*` não infla system prompt (assert no teste de
      composição).
- [ ] `agents/*.agent.md` embutidos aparecem em `agent_catalog`
      (camada builtin < user < workspace já existente).
- [ ] SSRF audit documentado; guard ativo se gap confirmado.
- [ ] `dotnet test` verde; sem regressão em `AutonomyGuardrail`.

## 5. API Contract

Sem endpoints novos. Arquivos novos no workspace/repo:
`.openhandsnet/hooks.{json,yaml}`, `.openhandsnet/permissions.yaml`
(formato documentado em `docs/features/`), `.openhandsnet/plans/`.
Tool novo: `tool_search` (schema padrão). Eventos: `hook_fired`
(audit), `permission_decision` (rule/judge/verdict) — modelo V1.

## 6. Task Plan

1. Hooks engine + `.openhandsnet/hooks` + testes.
2. Ruleset + integração `PermissionedToolRegistry`.
3. Risk classifiers + judge + prompt embutido.
4. `ToolIndexService`+`tool_search`+`DeferredToolState`.
5. SSRF audit/guard + `PlanFileService`.
6. Prompts+agents embutidos + `ExternalAccessRegistry` + docs.

## 7. Riscos / notas

- Judge LLM por tool-call adiciona latência/custo — cache por
  assinatura (tool+args-hash) e modelo barato via `ModelRouter`/
  `OmniRouteModelCatalog` são obrigatórios.
- Hooks executam código arbitrário do repo — mesmo threat-model do
  agente (untrusted input SPEC-91); documentar que hooks rolam dentro
  do sandbox, nunca no host.
- Deferred loading muda o prompt por turno — compatível com
  `ConversationHistoryBuilder`/condensação; medir regressão de
  cache de prompt (prefix stability) e documentar.

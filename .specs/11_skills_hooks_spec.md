# SPEC — 11 · Skills & Hooks

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `skills-and-hooks` |
| Type | `Feature / API` |
| Stack | `.NET 10 · EF Core · Markdown` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | [#12](https://github.com/afonsoft/OpenHandsNET/issues/12) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `02_event_stream_spec`, `10_persistence_efcore_spec` |
| Consumed by | `09_agent_engine_spec` (system prompt + invoke_skill + hooks no loop) |

## 1. User Story

**As a** usuário/organização do OpenHandsNET
**I want** skills reutilizáveis (markdown com triggers) injetadas no agente e
hooks de ciclo de vida executados em pontos fixos
**So that** o comportamento do agente seja extensível por conteúdo e
configuração — microagents no upstream — sem recompilar código.

**Problem context:** upstream tem `sdk/skills/` (skills markdown + triggers +
`invoke_skill` tool) e `sdk/hooks/` (executor de hooks de conversa que emitem
`HookExecutionEvent`). O start-task upstream tem estágio `SETTING_UP_SKILLS`.
Esta SPEC entrega CRUD+storage das skills, matching de trigger, injeção no
prompt e execução auditável de hooks.

## 2. Scope

**In scope:**
- `Skill {id, name, description, content_md, triggers[{type:keyword|task|always,
  value}], source:user|org|builtin, enabled}` — paridade do modelo upstream
  (`skills/skill.py` + `types.py`).
- Escopo: skills de usuário (`/api/v1/skills` CRUD), org (SPEC 08) e `builtin`
  (seed a partir de `skills/` embutidos no Api).
- `ISkillService`: CRUD + `ResolveForConversationAsync(conversationId, message)`
  → skills ativas por trigger matching.
- Injeção no agente: `SystemPromptBuilder` (SPEC 09) concatena skills
  `always`/matched; tool `invoke_skill(name)` injeta conteúdo sob demanda
  (paridade: a tool existe para skills não-always).
- Start-task: estágio `SETTING_UP_SKILLS` resolve e persiste `conversation_skills`.
- `HookConfig {name, event:on_conversation_start|on_message|on_tool_pre|
  on_tool_post|on_finish, action:emit_event|run_command|webhook, payload}`
  — `IHookService` dispara nos pontos fixos do `AgentRunner` e emite
  `HookExecutionEvent{hook_name,status,output?}`.
- UI: `SkillsSettingsPage` (list/create/edit/toggle) — dentro de settings do Client.

**Out of scope:** marketplace de skills (`marketplace/` upstream), fetch remoto
de skills (`fetch.py`), hooks que executam código arbitrário no host (só
`run_command` **dentro do sandbox**), ACP/hook scheduling, skill versioning.

## 3. Technical Context

**Upstream (evidência, MIT):**
- `sdk/skills/{skill,types,execute,installed,fetch,trigger,utils}.py`
- `sdk/hooks/{config,conversation_hooks,executor,manager,types}.py`
- `agent_server/hooks_router.py` + `hooks_service.py`; `skills` router no app_server.
- `tool/builtins/invoke_skill.py` — tool do agente.
- Start-task status `SETTING_UP_SKILLS` (api-inventory §2).

**Where:**
- `Domain/Skills/{Skill,SkillTrigger}.cs`, `Domain/Hooks/{HookConfig,HookEvent}.cs`
- `Application/Skills/ISkillService.cs`, `Application/Hooks/IHookService.cs`
- `Infrastructure/Skills/SkillService.cs`, `Infrastructure/Hooks/HookService.cs`
- `Api/Endpoints/SkillEndpoints.cs`, `Client/Pages/SkillsSettingsPage.razor`

## 4. Requirements

### RF-001: Skill model + CRUD
- `POST|GET /api/v1/skills`, `GET|PATCH|DELETE /api/v1/skills/{id}` — owner-scoped.
- `content_md` ≤ 32KB; `name` slug único por owner; `description` ≤ 1024 chars
  (paridade regra upstream de truncagem).
- `builtin` skills seed no boot (idempotente), read-only; `org` skills geridas
  por admins (SPEC 08).

### RF-002: Trigger matching
- `always`: sempre injetada; `keyword`: match case-insensitive em palavras da
  última mensagem; `task`: match por tipo de tarefa (tag do start-task).
- Ordem de injeção: builtin → org → user; dedupe por `name`.

### RF-003: invoke_skill
- Tool `invoke_skill{name}`: injeta `content_md` como `ObservationEvent`/`MessageEvent`
  de contexto (kind conforme SPEC 02); erro se skill inexistente → `AgentErrorEvent`
  não-fatal.

### RF-004: Hooks
- Eventos suportados: `on_conversation_start`, `on_message` (pré-LLM),
  `on_tool_pre`, `on_tool_post`, `on_finish`.
- Ações: `emit_event` (injeta `HookExecutionEvent` + opcional `MessageEvent`
  contexto), `run_command` (exec no sandbox via `ISandboxExecutor`, output no
  evento), `webhook` (POST async com retry — usa `WebhookDispatcher` SPEC 02).
- Falha de hook: `HookExecutionEvent(status=error)` + continua (nunca aborta
  conversa, salvo `blocking:true` opt-in).

### RF-005: Start-task `SETTING_UP_SKILLS`
- Worker (SPEC 04) resolve skills da conversa (user+org+builtin por triggers
  `always`) e grava `conversation_skills(conversation_id, skill_id)` antes de `READY`.

**Invariants:** skills são conteúdo, nunca código executável direto (só via
hooks/tools dentro do sandbox); skills de org não vazam para outras orgs
(query filter SPEC 10); conteúdo injetado entra no prompt como "context", jamais
como instruções de sistema com prioridade sobre guardrails.

## 5. API Contract

`GET|POST /api/v1/skills` · `GET|PATCH|DELETE /api/v1/skills/{id}` ·
`GET /api/v1/app-conversations/{id}/skills` (paridade: skills resolvidas da
conversa) · `GET /api/v1/app-conversations/{id}/hooks` (hooks + últimas execuções) ·
`GET|POST|PATCH|DELETE /api/v1/hooks` (configs por usuário/org).

## 6. Critérios de Aceite

- [ ] CRUD skill + trigger `keyword` dispara injeção quando mensagem contém a palavra.
- [ ] Skill `always` aparece no `SystemPromptEvent`/`system_prompt` montado.
- [ ] `invoke_skill` no loop retorna conteúdo da skill; inexistente → erro não-fatal.
- [ ] Hook `on_tool_post` emite `HookExecutionEvent` consultável via `events/search?kind__eq=HookExecutionEvent`.
- [ ] Start-task passa por `SETTING_UP_SKILLS` antes de `READY`.
- [ ] Skill de org A invisível para user de org B (multi-tenancy on).

## 7. Task Plan

1. Entities + EF config + seed builtin (verify: migration + seed test).
2. `SkillService` CRUD + trigger matching (verify: unit tests).
3. `SystemPromptBuilder` integration + `invoke_skill` tool (verify: loop test).
4. `HookService` + pontos no AgentRunner + `HookExecutionEvent` (verify: integration).
5. Endpoints + UI settings + `conversation_skills` no start-task (verify: contract + E2E).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Domain/Skills/{Skill,SkillTrigger,SkillSource}.cs
src/OpenHandsNET.Domain/Hooks/{HookConfig,HookEvent,HookAction}.cs
src/OpenHandsNET.Application/Skills/ISkillService.cs
src/OpenHandsNET.Application/Hooks/IHookService.cs
src/OpenHandsNET.Infrastructure/Skills/SkillService.cs
src/OpenHandsNET.Infrastructure/Skills/BuiltinSkillSeeder.cs   # skills/*.md embedded
src/OpenHandsNET.Infrastructure/Hooks/HookService.cs
src/OpenHandsNET.Infrastructure/Agent/SystemPromptBuilder.cs   # (SPEC 09 — integração)
src/OpenHandsNET.Infrastructure/Agent/Tools/InvokeSkillTool.cs # (SPEC 09 — integração)
src/OpenHandsNET.Api/Endpoints/{SkillEndpoints,HookEndpoints}.cs
src/OpenHandsNET.Client/Pages/SkillsSettingsPage.razor
tests/OpenHandsNET.UnitTests/Skills/{TriggerMatchingTests,SkillServiceTests}.cs
tests/OpenHandsNET.IntegrationTests/Skills/HookExecutionTests.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §2 (`SETTING_UP_SKILLS`), §6 (`hooks_router`).
- `.specs/09_agent_engine_spec.md` — `SystemPromptBuilder`, `invoke_skill`, hook points.

### Upstream — código-fonte (baseline `acca181`, MIT)
- Skills: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/skills
  (`skill.py`, `types.py`, `trigger.py`, `execute.py`, `installed.py`)
- Hooks: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/hooks
  (`config.py`, `conversation_hooks.py`, `executor.py`, `manager.py`, `types.py`)
- `invoke_skill` builtin: https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-sdk/openhands/sdk/tool/builtins/invoke_skill.py
- Hooks router (agent-server): …/openhands-agent-server/openhands/agent_server/hooks_router.py + `hooks_service.py`
- Marketplace (fase 3, referência): …/openhands-sdk/openhands/sdk/marketplace/

### Bibliotecas / framework
- Embedded resources em .NET (builtin skills): https://learn.microsoft.com/dotnet/core/extensions/create-resource-files
- Markdig (render/preview opcional na UI): https://github.com/xoofx/markdig

## 10. Guardrails de execução (Devin)

- Skills são **dados** (markdown) — nunca compilar/executar conteúdo de skill no host.
- `run_command` de hook só dentro do sandbox; `webhook` assíncrono com retry.
- Hooks `blocking:true` documentados como perigo (podem travar o loop) — default false.
- Truncar `content_md` injetado ao orçamento de contexto (`ICondenser`/budget do SPEC 09).
- Isolamento por owner/org via query filter (SPEC 10) — sem exceção manual.

## 11. Definition of Done

- [ ] CRUD + triggers + injeção + `invoke_skill` verificados end-to-end.
- [ ] Hooks nos 5 pontos emitem `HookExecutionEvent` correto.
- [ ] `SETTING_UP_SKILLS` no start-task; endpoints + UI funcionais.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.

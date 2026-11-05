# SPEC — 127 · Hooks command & prompt (extensibilidade pré/pós tool-call)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `hooks-command-prompt` |
| Type | `Feature` |
| Status | `In review` |
| Ticket | #332 (umbrella #329, Epic #317) — gap SPEC-123 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/` (hooks engine, runner), `src/OpenHandsNET.Domain` (eventos) |

## 1. User Story

**As a** operador avançado, **I want** hooks que executem comandos shell
e injetem contexto de prompt em pontos do ciclo do agente (pré/pós
tool-call, início de run), **so that** fluxos customizados (lint gate,
changelog, contexto dinâmico) funcionem sem alterar o runtime — como os
command/prompt hooks do OpenWebUiCode.

## 2. Contexto / escopo

- Já existem lifecycle hooks **não-bloqueantes** que emitem eventos
  (coberto por `SkillsHooksE2ETests`), mas não executam shell nem
  injetam texto no prompt.
- OWUIC tem três tipos: `command` (shell pré/pós), `prompt` (injeta
  markdown no contexto), `lifecycle` (já coberto).
- Hooks configurados via `.openhands/hooks/` ou frontmatter de skills.

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | `command` hook: `{ matcher: "tool:*_edit|apply_patch", event: "post", run: "<shell>" }` — executa via `ctx.Executor` no sandbox, exit≠0 reportado ao modelo como observação (não falha o run, exceto `blocking: true`). |
| RF-002 | `prompt` hook: `{ event: "run_start|pre_tool", inject: "<markdown>|file:<path>" }` — conteúdo entra como system/context note no próximo turno. |
| RF-003 | Configuração por arquivos `.openhands/hooks/*.hook.json` no workspace + camada embedded defaults. |
| RF-004 | `blocking: true` (opt-in) — hook post-tool falhando desfaz/registra e sinaliza ao modelo; default não-bloqueante preserva comportamento atual. |
| RF-005 | Eventos `hook_fired` no stream V1 (nome, matcher, exit code, duration). |
| RF-006 | Segurança: hooks rodam dentro do sandbox; secrets redacted via `SecretRedactor` no output; matcher validado (glob estrito). |

## 4. Acceptance

- [ ] Hook `post` com `run: "echo done"` dispara após `apply_patch` (evento no stream).
- [ ] Hook `prompt` injeta texto visível no system prompt do turno seguinte.
- [ ] `blocking: true` + exit≠0 → modelo recebe a falha como resultado da tool.
- [ ] Hook inválido (JSON ruim, matcher inválido) → skipped com warning, sem crash.
- [ ] E2E com hook real (lint fake) em teste de integração.

## 5. API Contract

Formato `.hook.json` (camelCase JSON, seguindo padrão `*.agent.md`):

```json
{
  "name": "lint-after-edit",
  "event": "post_tool",
  "matcher": "apply_patch|file_edit",
  "type": "command",
  "run": "python3 -m compileall src/",
  "blocking": false
}
```

## 6. Task Plan

1. `HookDefinition` record + parser `.hook.json` + loader camadas (embedded → user → workspace).
2. `HookEngine` — matcher por glob, dispatch command/prompt.
3. Wiring `AgentRunner` — pontos `run_start`, `pre_tool`, `post_tool`.
4. Evento `hook_fired` + persistência.
5. Testes unitários (parser, matcher, blocking) + integração.

## 7. Riscos / notas

- Command hooks = execução arbitrária por configuração — sandbox-bound e documentado; nunca rodar hooks de workspace não-confiável sem opt-in.
- Manter hooks existentes (lifecycle/event) funcionando — novo engine é aditivo.

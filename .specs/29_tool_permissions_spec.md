# SPEC — 29 · Tool Permissions (`allow`/`deny`/`ask`) & Plan Mode

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `tool-permissions` |
| Type | `Feature / Agent Core / Security` |
| Stack | `.NET 10 · AgentRunner · ToolRegistry` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` |
| Ticket | #62 |
| Depends on | `09` (agent engine), `25` (tool surface); alimenta `28` (presets) |
| Touches | `AgentLlmOptions`, `ToolRegistry`, `AgentRunner`, `AppConversationService`, `ConversationRecord` |

## 1. User Story

**As a** admin/usuário
**I want** regras `allow`/`deny`/`ask` por tool (glob `mcp__*` incluso),
no nível instância e por conversa — e um modo `plan` read-only
**So that** eu controlo finamente o que o agente pode fazer sem depender
só do confirmation mode global (hoje binário).

Referências: opencode `permission` (SPEC-27 F4), claude-code
`--allowedTools`/`--permission-mode`.

## 2. Scope (RF)

### RF-001 — Modelo de regras

`AgentPermissionRule { pattern, decision }` com `decision ∈
{"allow","deny","ask"}`; pattern suporta `*`/`mcp__**`/`bash` exatos.
Resolução (first-match, mais específico ganha: maior prefixo literal
antes do `*`):

```json
"AgentLlm": {
  "Permissions": [
    {"pattern":"file_write","decision":"ask"},
    {"pattern":"bash","decision":"ask"},
    {"pattern":"mcp__*","decision":"allow"},
    {"pattern":"web_fetch","decision":"deny"}
  ]
}
```

Default sem regra: `Risky=true → ask`, `Risky=false → allow` —
**retrocompatível** com confirmation mode (que vira `ask` global).

### RF-002 — Enforcement server-side

- `ToolRegistry.ForConversation(conv)` → `EffectiveToolCatalog` que
  **remove** tools `deny` (nunca anunciadas ao modelo — impossível
  chamar via injection).
- `ask` → `ConfirmationRequired` no dispatch (o fluxo de
  confirm/reject existente; `AskAsync` hub).
- `ConversationRecord.Permissions` (JSON) — override por conversa,
  setável no start-task (`CreateConversationRequest.permissions`) e
  via `POST /{id}/permissions` (admin/dono).
- Decisões registradas: `agent_diagnostic` `code="permission_deny"`
  quando o modelo tenta tool negada (não estava no catálogo) —
  observabilidade de drift.

### RF-003 — Plan mode

`POST /{id}/mode` `{mode:"plan"|"build"}`: `plan` = overlay de denies
(`file_write`, `file_edit`, `bash` mutável, `git_commit`, `browser`
mutante) — útil p/ "review/analyse-only". `ConversationRecord.Mode`
persiste; state event `conversation_mode`.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| PATCH | `/{id}/permissions` | set rules (dono) |
| POST | `/{id}/mode` | `plan|build` |

Events: `agent_diagnostic(permission_deny)`, `conversation_mode`.

## 4. Data model

Migration: `Conversations` + `Permissions` (jsonb/text, null),
+ `Mode` (string default `build`).

## 5. NFR / Security

- `deny` remove do catálogo — impossível via prompt injection.
- `ask` falha fechada se o usuário não responde em `AskTimeout`.
- Rules de org (SPEC-08 admin) = floor: conversa não pode liberar
  o que a org negou (merge: deny de org sempre vence).

## 6. Task Plan

1. `AgentPermissionRule` + `PermissionEvaluator` (first-match).
2. `ToolRegistry.ForConversation` filtrado + `ask` → dispatch gate.
3. Colunas + start-task + endpoints + plan mode.
4. Testes: deny some do catálogo, ask força confirmação, wildcard
   `mcp__*`, plan mode, org-floor.

## 7. Deliverables

`Agent/Permissions/*`, migration, endpoints, testes.

## 8. Referências

- opencode `permission` (allow/deny/ask por tool+agent).
- claude-code `--permission-mode`, `--allowedTools`.

## 9. Guardrails

- `deny` nunca vira instrução de prompt — remoção física do catálogo.
- Org deny é floor imutável pela conversa.
- `plan` não impede `finish` (agente ainda conclui com resumo).

## 10. DoD

- `deny` de `file_write` → tool ausente do catálogo do modelo.
- `ask` de `bash` → fluxo de confirmação executa/veta.
- `mode=plan` → mutation tools negadas; `dotnet test` verde.

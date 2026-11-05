# SPEC — 120 · Chat/run UX — tool-call cards + prompt cards + diffs

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `chat-run-ux` |
| Type | `Frontend` |
| Status | `In implementation` |
| Ticket | #322 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Client/Components/Conversation/*`, `Components/*`, `Services/` (ToolActivityMap, ToolArgsPreview, ToolCallView, GitDiffRender, ChatNotificationsService) |

## 1. User Story

**As a** usuário do OpenHandsNET acompanhando uma run de agente,
**I want** cards de tool-call ricos (ícone por tool, args resumidos,
resultado expandível, status/tempo), prompts de permissão/pergunta
dedicados e diffs legíveis, **so that** eu entenda o que o agente está
fazendo sem abrir JSON bruto — o mesmo padrão de leitura do
OpenWebUiCode e do feed de atividades do Devin Cloud.

## 2. Contexto / escopo

AS-IS: `EventCard.razor` genérico + `ConfirmationBanner` +
`ThinkingSection`, `ChatPane`, `DiffTab`, `RunActivity`-feed via
`AgentActivityBus` (SPEC-24). Funcional, mas abaixo do OWUIC:

OWUIC (evidência):

- `ToolCallCard.razor` + serviços `ToolActivityMap` (ícone/label/
  categoria por tool name), `ToolArgsPreview` (resumo humano de args:
  path/command/url truncados), `ToolCallView` (estado do card:
  running→done/error, duração, collapsible).
- `PermissionPromptCard.razor` / `QuestionPromptCard.razor`: cards
  inline no chat para aprovação de tool e `ask_user` — com opções,
  allow/deny, "always allow" por escopo, e textarea de resposta.
- `MessageBubble.razor`: action bar hover/`focus-within` (copy,
  regenerate, edit, read-aloud) — corrigida pelo design-review para
  `group-focus-within` (A2).
- `GitDiffRender` service: diff unificado → markup colorido com
  file-header/stats.
- `ChatNotificationsService`: notificação desktop (Notification API)
  quando run termina/precisa atenção em aba inativa.
- `RunActivityFeed`, `InsightsPanel`, `ChatWorkspacePanel` (jobs/MCP
  dots), `CheckpointList` (OHN já tem).
- `unseen_messages` concept: msgs do usuário chegando durante run.

Direção: evoluir `EventCard` para `ToolCallCard` por família de
eventos; portar os 3 serviços de apresentação de tool-call; unificar
permissão+pergunta em cards dedicados mantendo o fluxo atual de
confirmação (SPEC-29 tool_permissions).

Fora de escopo: novos tools do agente (SPEC-121/122); rendering de
markdown (SPEC-116 cobre `.markdown-body`+codeblocks); workspace IDE.

## 3. Requisitos

- RF-001 — **ToolCallCard**: card por tool-call com ícone+categoria
  (`ToolActivityMap`: mapear os 21 builtins + `mcp__*` prefix →
  ícone/label i18n), linha de args resumidos (`ToolArgsPreview`),
  corpo expansível com resultado, status spinner→check/error e
  duração (`ToolCallView` state).
- RF-002 — **PermissionPromptCard**: para tool-calls que exigem
  confirmação (Risky/allowlist) — botões allow/deny (+ "sempre para
  esta sessão/tool" se o backend suportar escopo — verificar
  `PermissionedToolRegistry`); substitui/integra `ConfirmationBanner`.
- RF-003 — **QuestionPromptCard**: renderiza `ask_user` como card
  com textarea + submit inline (hoje provavelmente texto plano).
- RF-004 — **Message actions**: barra de ações por mensagem
  (copy/resend) com `group-hover` **+ `group-focus-within`** — não
  repetir o bug A2 do OWUIC; touch mostra sempre.
- RF-005 — **GitDiffRender**: diff → markup com header por arquivo
  (+/− stats, chips add/del/context) usado em `DiffTab` e cards de
  `files_changed`/`git_commit`.
- RF-006 — **ChatNotificationsService**: `Notification.requestPermission`
  opt-in em settings; notifica ao concluir run / pedir atenção com a
  aba em `document.hidden`; click foca a conversa.
- RF-007 — **Unseen messages UX**: indicador "novas mensagens do
  usuário durante a run" (par com o tool `unseen_messages` da
  SPEC-121; aqui só a superfície — pill no topo do stream).
- RF-008 — **i18n/a11y**: strings ×8; live-region mantida
  (`role="log"` no stream); dots de status com texto alternativo
  (corrige o finding A6 no nosso equivalente).

## 4. Acceptance

- [ ] Tool-calls renderizam como cards por família (bash/file/git/
      browser/mcp) com args humanizados e duração.
- [ ] Permissão e `ask_user` aparecem como cards inline acionáveis;
      respostas fluem pelo caminho atual (sem mudança de protocolo).
- [ ] Actions de mensagem acessíveis por teclado (focus-within) e
      toque; copy funciona.
- [ ] `DiffTab` e eventos `files_changed` usam o renderer de diff
      com stats por arquivo.
- [ ] Notificação desktop dispara em run concluída com aba oculta
      (opt-in respeitado).
- [ ] bUnit nos componentes novos; drift guards + i18n parity verdes.

## 5. API Contract

Sem mudanças — consome o event stream atual (`kind` discriminator +
`CommandOutput`/`ToolCall` deltas, SPEC-24). Se faltar campo
`duration_ms`/`status` nos deltas de tool-call, emitir no backend
(registrar aqui antes de codar o card).

## 6. Task Plan

1. `ToolActivityMap` + `ToolArgsPreview` + `ToolCallView` + card.
2. `PermissionPromptCard`/`QuestionPromptCard` + wiring atual.
3. Message action bar.
4. `GitDiffRender` + integração DiffTab/files_changed.
5. `ChatNotificationsService` + setting opt-in.
6. Unseen-messages pill + i18n + bUnit + docs.

## 7. Riscos / notas

- Event model V1 é hard rule — os cards leem `kind`/`parent_id`,
  nunca `action`/`args` V0.
- Mapa de ícones deve cobrir `mcp__*` com ícone genérico + nome do
  server derivado do prefixo (`McpToolCatalog.TryParse` já existe no
  Application — reusar do client via contrato compartilhado).
- Deltas de tool-call podem não carregar duração — verificar
  `AgentActivityBus`/`DeltaBatcher` na tarefa 1.

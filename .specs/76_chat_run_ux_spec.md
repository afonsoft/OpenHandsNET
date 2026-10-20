# SPEC-76: Chat run UX — status pill, git chip, activity feed, sub-conversas

| Campo | Valor |
|---|---|
| Status | Draft |
| Gap key | GAP-architecture-chat-run-ux |
| Type | UX/Layout |
| Fonte | Análise comparativa `afonsoft/open-webui` × `ConversationPage`/`ChatPane` |

## User Story

Como usuário acompanhando uma run, quero feedback ao vivo da etapa atual,
atalho para o diff do branch e uma stream de eventos condensada, no padrão
Devin/open-webui.

## Contexto técnico

- `Pages/ConversationPage.razor` topbar: repo + ModelSelector +
  AgentModeToggle + badges de status/conexão.
- `Components/Conversation/EventCard.razor` — stream bruta de eventos.
- Referência: `ChatView.razor` (status pill + git chip + parent link),
  `RunActivityFeed.razor` (colapso "N passos"), `SubConversationPanel.razor`.

## Requisitos

1. **Status pill streaming**: quando `ExecutionStatus` ∈ {RUNNING, WORKING,
   SETTING_UP_*}, chip com spinner + label da etapa atual (último
   `*Event`/fase), `role="status"` + `aria-live="polite"`.
2. **Git chip**: `SelectedRepository` mostra branch; se o backend expuser
   diff stats, exibe `+add −rem`; clique navega para a aba `diff`.
3. **RunActivityFeed**: ao finalizar a run, os eventos colapsam em
   "N passos" expansível; durante a run, linhas amigáveis por tool_call
   (ícone + label + spinner/✓/✕) — args scrubbed preservados no expand.
4. **SubConversationPanel**: quando existir conversa filha (sub-agente),
   thread embutida ~16rem com scroll interno sob a tool call
   `delegate_task`/`conversation` — condicional à API expor `parent_id`.
5. Tudo em pt-BR/en via `LocalizationService`; sem regressão de SignalR.

## Critérios de aceite

- Pill visível só durante run ativa; some no estado final.
- Feed colapsado após finish, expande ao clique.
- Build 0w/0e; bUnit test cobrindo pill + colapso do feed.
- Se `parent_id` não existir na API, req. 4 fica gated (não bloqueia).

## Fora de escopo

PermissionPromptCard/QuestionPromptCard (parcialmente coberto por
ConfirmationBanner), anexos/mídia, voz.

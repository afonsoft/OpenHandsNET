# SPEC-77: IDE workbench — aprofundamento (paridade open-webui Fase B)

| Campo | Valor |
|---|---|
| Status | Draft |
| Gap key | GAP-architecture-ide-workbench |
| Type | UX/Layout |
| Fonte | `IdeActivityBar`/`IdeStatusBar`/`IdeOutlineView`/`Pages/Ide.razor` do open-webui × `Components/Ide/*` |

## User Story

Como usuário do workbench, quero os refinamentos do IDE estilo VS Code:
badge de dirty-count no SCM, zen mode e painéis adicionais.

## Requisitos

1. **SCM dirty badge** na activity bar do `IdeTab` (contagem de arquivos
   alterados, `99+` cap).
2. **Zen mode**: toggle no activity bar que esconde side panel e distrai-
   mentes (`aria-pressed`).
3. **IdeBottomPanel** (terminal/output mini) — avaliar reuso do
   `TerminalTab` embutido vs. painel dedicado.
4. **IdeOutlineView** (símbolos do arquivo aberto, via Monaco/document
   symbol provider quando disponível).
5. `/ide` standalone page — **condicional**: só se o produto quiser IDE
   fora do contexto de conversa; caso contrário, permanece dentro do tab.

## Critérios de aceite

- Badge atualiza em tempo real com o SCM view.
- Zen mode restaura estado ao desligar.
- Build 0w/0e; bUnit cobrindo badge + zen toggle.

## Fora de escopo

LSP completo, debugger, preview web embutido.

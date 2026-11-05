# SPEC — 126 · LSP persistent sessions (servidor residente por conversa)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `lsp-persistent-sessions` |
| Type | `Feature` |
| Status | `In implementation` |
| Ticket | #331 (umbrella #329, Epic #317) — débito da SPEC-122 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/Tools/BuiltinLsp.cs`, `ISandboxExecutor.OpenBashSessionAsync`, bridge Python |

## 1. User Story

**As a** agente editando código, **I want** que o servidor LSP fique
residente durante a conversa, **so that** `lsp_*` responda com o índice
quente (diagnostics rápidos, references completas) em vez de reanalisar
o workspace a cada chamada.

## 2. Contexto / escopo

- SPEC-122 entregou os 6 tools `lsp_*` via bridge Python **one-shot**:
  cada chamada escreve/execa `lsp_bridge.py` via `ExecAsync` — correto
  porém sem cache (init + indexação por chamada).
- `ISandboxExecutor.OpenBashSessionAsync` já oferece canal bidirecional
  persistente no sandbox — caminho natural para um bridge residente.
- OWUIC mantém cliente JSON-RPC/LSP com lifecycle por sessão.

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | `LspBridgeSession` — bridge Python residente por `(conversationId, language)`, comunicação stdio JSON-RPC pela `OpenBashSessionAsync`. |
| RF-002 | Lazy start: primeira chamada `lsp_*` inicia a sessão; chamadas subsequentes reutilizam (keep-alive com TTL — ex. 10 min idle). |
| RF-003 | Fallback transparente: se a sessão morrer, recria; se indisponível, degrada para modo one-shot (comportamento atual). |
| RF-004 | Shutdown limpo no fim do run/conversa (`finally` no `AgentRunner` ou dispose do estado por conversa). |
| RF-005 | Manter jail de workspace e result capados da SPEC-122 (sem regressão de segurança). |
| RF-006 | Métricas/log: tempo de resposta da chamada LSP em stderr/diag (não polui stdout do protocolo). |

## 4. Acceptance

- [ ] Duas chamadas `lsp_diagnostics` seguidas → segunda sem init/index (log/métrica evidencia reuse).
- [ ] Sessão morta → fallback one-shot sem erro para o modelo.
- [ ] Fim do run → processo bridge encerrado (sem órfãos no sandbox).
- [ ] Testes unitários do lifecycle + teste de integração (docker) reutilizando sessão.

## 5. API Contract

Sem mudança de contrato externo. Interno: `ILspBridgePool` (Application)
com `GetOrStartAsync(conversationId, language)` / `StopAllAsync`.

## 6. Task Plan

1. `ILspBridgePool` + implementação com `ConcurrentDictionary` por conversa.
2. Bridge Python v2 — modo `serve` (loop lendo JSON-RPC de stdin) além do one-shot.
3. Refatorar `BuiltinLsp` para usar o pool com fallback.
4. Lifecycle: cleanup no fim do run (hook no `AgentRunner`).
5. Testes (unit lifecycle + integration docker).

## 7. Riscos / notas

- Sessões órfãs → TTL idle + dispose obrigatório no fim do run.
- Concorrência: mesma linguagem/idioma simultâneo → serializar por sessão (lock).
- Complexidade: manter modo one-shot como caminho de degradação reduz blast-radius.

# SPEC — 22 · Context Engine, Compaction & Resumable Snapshots

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `context-engine-compaction` |
| Type | `Feature / Agent Core` |
| Stack | `.NET 10 · EF Core · Minimal API` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Done` — PR #57 merged |
| Ticket | `TBD` |
| Depends on | `09_agent_engine_spec` (loop/eventos), `02_event_stream_spec` (V1 kinds) |
| Feeds | `23_omniroute_model_catalog_routing_spec` (janela por modelo) |
| Touches | `ICondenser`, `NoOpCondenser`, `AgentRunner.BuildHistoryAsync`, `Condensation*` events, `ConversationStateUpdateEvent`, `TokenEvent` |

## 1. User Story

**As a** usuário com runs longas (e operador OmniRoute com vários modelos de
janelas diferentes)
**I want** que o contexto da conversa seja medido, podado e compactado
automaticamente, persistido de forma resumível
**So that** runs nunca morrem por overflow de contexto, a conversa sobrevive a
restart de processo/container/troca de modelo, e eu vejo no stream quando e
por que o contexto foi compactado.

**Problem context:**

- `ICondenser` é `NoOpCondenser` — o histórico inteiro (todas as mensagens +
  todas as observations) vai para o LLM em **toda** iteração. Uma run de ~50
  iterações com saídas de `bash`/`file_read` estoura qualquer janela.
- O modelo V1 já prevê compaction: `Condensation` (com `ForgottenEventIds`,
  `Summary`, `LlmResponseId`), `CondensationSummaryEvent` e
  `CondensationRequest` existem em `Domain/Events` — e `BuildHistoryAsync`
  já honra `ForgottenEventIds` no resume. **Falta quem emita esses eventos.**
- Não há contagem/estimativa de tokens por modelo; `ModelInfo.ContextWindow`
  existe mas nada o popula.
- `LlmModel` pode mudar via `switch_acp_model` — o histórico não é adaptado à
  nova janela.

## 2. Scope (RF)

### RF-001 — `IContextMeter` (estimativa de tokens)

`Application/Agent/IContextMeter.cs`:

```csharp
public interface IContextMeter
{
    /// <summary>Tokens estimados do histórico para o modelo (chars/4 por
    /// padrão; calibrado com o último prompt_tokens observado).</summary>
    int EstimateTokens(IReadOnlyList<LlmMessage> messages, ModelInfo? model);

    /// <summary>Context window efetiva do modelo; null = ilimitado/unknown.</summary>
    int? ContextWindow(ModelInfo? model);
}
```

Implementação `HeuristicContextMeter` (Infrastructure/Agent/Context):
- Estimativa = `Σ(4 + chars(role)/4 + chars(content)/4 + args)` (constante de
  overhead por mensagem, padrão OpenAI).
- **Calibração online**: a cada `CompletionEnd`, guarda
  `prompt_tokens/estimated` da completion anterior e aplica como fator de
  correção (clamp 0.7–1.5) — melhora a precisão sem tokenizer nativo.
- `ContextWindow` vem de `IModelCatalog` (SPEC-23) com override por config
  `AgentLlm:ContextWindowOverrides:{"model":tokens}` e default
  `AgentLlm:DefaultContextWindow` (128000).

### RF-002 — `LlmSummarizingCondenser`

Substitui `NoOpCondenser` quando `AgentLlm:Compaction:Enabled=true` (default
`true` quando provider != mock). Implementação
`Infrastructure/Agent/Context/LlmSummarizingCondenser.cs`:

Entrada: histórico + `IContextMeter` + modelo atual.
Saída: histórico possivelmente reduzido + **efeitos colaterais de eventos**
(o condenser não emite — retorna `CondenseResult{Messages,
CompactionPlan?}` para o runner emitir).

Pipeline:
1. `tokens = meter.EstimateTokens(history)`;
   `threshold = window * Compaction:ThresholdPercent` (default `0.75`) ou
   `Compaction:ThresholdTokens` se setado.
2. Abaixo do threshold → passthrough.
3. Acima → **tool-result pruning primeiro** (barato, sem LLM):
   observations (mensagens `role=tool`) mais velhas que as últimas
   `KeepRecentToolResults` (default 8) têm conteúdo > `PruneMaxChars`
   (default 4000) substituído por `[Output truncated — N chars]`.
   Re-estima; se ainda acima → summarization.
4. **Summarization**: pega mensagens mais antigas que o boundary —
   boundary = depois do system prompt, mantendo as últimas
   `KeepLastTurns` (default 6) mensagens "vivas" + tool calls pendentes
   (nunca corta um par call/result). Prompt fixo de sumário
   (`Context/compaction_prompt.md` embedded resource) pede: objetivo do
   usuário, decisões, arquivos tocados, erros pendentes, estado atual.
   Usa o **modelo de summarizer** do router (SPEC-23 `RoleModels:summary`,
   fallback = modelo atual).
5. Retorna `[system, summary_msg, …mensagens mantidas]` +
   `CompactionPlan{ForgottenEventIds, Summary, TokensBefore, TokensAfter}`.

### RF-003 — Emissão de eventos pelo runner

`AgentRunner.LoopAsync`: antes de cada completion,
`condenser.CondenseAsync` retorna `CondenseResult`; se `CompactionPlan` →
emite nesta ordem (todos com `ParentId` correto):

1. `CondensationRequest` — `kind:"condensation_request"`, `tokens_before`,
   `trigger` (`threshold|mid_turn|overflow|stop_length|manual|idle`).
2. Após summarizer: `Condensation` — `ForgottenEventIds` (ids reais dos
   eventos cujas mensagens saíram), `Summary`, `SummaryOffset`,
   `LlmResponseId` do summarizer.
3. `CondensationSummaryEvent` — `Summary` (texto humano, renderizável no
   chat como "contexto compactado").

`BuildHistoryAsync` já dropa `ForgottenEventIds` — **resume + replay ficam
consistentes de graça**. Adicionar: ao achar `Condensation` com `Summary`,
insere a summary como mensagem `system` na posição `SummaryOffset` (hoje o
resume apenas esquece; a summary deve voltar ao contexto).

### RF-004 — Triggers além do threshold

- **Overflow recovery**: `AgentLlmProvider` lança `ContextOverflowException`
  (detecta 400/`context_length_exceeded`/`maximum context length` no body).
  Runner catch → `CondensationRequest(trigger:"overflow")` → compacta →
  **retenta a mesma iteração uma vez**; falha de novo → erro como hoje.
- **`stop_reason:"length"`**: completion truncada → mesma via com
  `trigger:"stop_length"` + hint no system ("responda de forma incremental").
- **Mid-turn**: `Compaction:MidTurnEnabled` (default true) — check antes de
  cada iteration dentro da run, não só no início.
- **Manual**: `POST /app-conversations/{id}/context/compact` (auth:
  conversation write) — enfileira `CondensationRequest(trigger:"manual",
  instructions?)`; runner consome na próxima iteração (ou numa run sintética
  se idle). Response `202`.
- **Model switch**: `switch_acp_model` bem-sucedido → re-check de contexto
  (nova janela pode ser menor) → compacta se necessário antes do próximo run.

### RF-005 — Context ledger (snapshot resumível)

A cada compaction e a cada `LedgerEveryIterations` (default 10) o runner
emite `ConversationStateUpdateEvent(key="context_state")` com:

```json
{"tokens_estimate":42000,"window":128000,"compactions":2,
 "boundary_event_id":"01J…","model":"x","updated_at":"…"}
```

UI mostra "contexto: 33% (42k/128k)" no header da conversa (leitura do
state update — sem endpoint novo). Serve também de telemetria para a SPEC-23
decidir promoção de modelo.

### RF-006 — Context promotion (oh-my-pi parity)

Antes de compactar por overflow/threshold, o runner consulta
`AgentLlm:PromotionTarget` (ou `ModelCatalog` janela-maior-disponível, SPEC-23):
se alvo existe com janela **estritamente maior** e o modelo está disponível →
`conv.LlmModel = target` + `ConversationStateUpdateEvent(key="model_change",
value={from,to,reason:"context_promotion"})` e **skip compaction** nessa
iteração. Sem alvo → compacta normalmente.

## 3. API Contract

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/v1/app-conversations/{id}/context/compact` | write | `202`; body `{instructions?}` |
| GET | `/api/v1/app-conversations/{id}/context` | read | `{tokens_estimate, window, compactions, boundary_event_id, model}` (lê último `context_state`) |

Eventos novos/changed: nenhum kind novo — usa `condensation_request`,
`condensation`, `condensation_summary` (já existem) + `state_update` keys
`context_state`, `model_change`. **Golden fixtures**: adicionar fixtures dos
3 kinds existentes (atualmente não exercitados) + payload `context_state`.

## 4. Data model

Sem migration — tudo vive no event stream. `Compaction:` options novas em
`AgentLlmOptions`/`AgentLlm` section (`Enabled, ThresholdPercent=0.75,
ThresholdTokens?, KeepLastTurns=6, KeepRecentToolResults=8,
PruneMaxChars=4000, MidTurnEnabled=true, LedgerEveryIterations=10,
PromotionTarget?`).

## 5. NFR / Security

- Summarizer recebe histórico **redigido** — passar por `SecretRedactor`
  antes do prompt de sumário (observations podem conter secrets vazados de
  `bash`).
- Compaction nunca remove `SystemPromptEvent` nem a última mensagem do
  usuário; nunca parte par call/result.
- Fail-open: summarizer falha → log + passa histórico intacto (exceto em
  overflow-recovery, onde falhar = erro como hoje).
- `context_state` não contém conteúdo — só contadores e ids (seguro p/ UI).

## 6. Task Plan

1. `IContextMeter` + `HeuristicContextMeter` + options (`AgentLlm` section)
   + unit tests de estimativa/calibração.
2. `ICondenser` → `CondenseResult` (breaking interno; ajusta `NoOpCondenser`
   p/ testes) + `LlmSummarizingCondenser` + pruning + unit tests.
3. Runner: emissão dos 3 eventos + triggers (threshold, mid-turn,
   stop_length, overflow-retry, model-switch) + ledger state update.
4. `BuildHistoryAsync`: reinjeta summary na posição do boundary.
5. Endpoint `POST …/context/compact` + `GET …/context`.
6. Promotion hook (`PromotionTarget` config; integração real do catálogo =
   SPEC-23).
7. Golden fixtures dos kinds + doc `docs/agent/context.md`.
8. Testes: compaction e2e com `MockLlmProvider` scriptado; overflow-retry;
   resume após compaction (history = summary + tail).

## 7. Deliverables

- `src/OpenHandsNET.Application/Agent/IContextMeter.cs`,
  `Infrastructure/Agent/Context/{HeuristicContextMeter,LlmSummarizingCondenser,CompactionOptions}.cs`
- Runner wiring + endpoints + docs + fixtures + testes.

## 8. Referências

- oh-my-pi compaction: threshold/mid-turn/idle/overflow/stop_length,
  `CompactionEntry{firstKeptEntryId,tokensBefore}`, pruning `[Output
  truncated]`, context promotion p/ janela maior —
  https://github.com/can1357/oh-my-pi (conceitos apenas).
- OpenHands upstream `Condenser` pipeline (conceitos apenas).
- Domain events existentes: `src/OpenHandsNET.Domain/Events/Condensation*.cs`.

## 9. Guardrails

- **V1 only** — kinds novos permitidos, formato V0 proibido.
- Determinismo no resume: replay do stream deve produzir o mesmo histórico.
- Sem secrets em summaries/`context_state`.

## 10. DoD

- Run sintética >threshold compacta e continua sem overflow.
- Resume após compaction reconstrói `[system, summary, tail]`.
- `POST /context/compact` → `condensation*` events no stream.
- Overflow simulado (mock 400 context_length) → retry pós-compact.
- `dotnet test` verde; fixtures novas passam.

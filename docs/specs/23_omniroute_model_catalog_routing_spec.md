# SPEC — 23 · OmniRoute Model Catalog, Multi-Model Routing & Turn Recovery

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `omniroute-model-catalog` |
| Type | `Feature / Agent Core` |
| Stack | `.NET 10 · OpenAI-compat HTTP · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` — PR #58 merged |
| Ticket | [#52](https://github.com/afonsoft/OpenHandsNET/issues/52) |
| Depends on | `09_agent_engine_spec`, `22_context_engine_compaction_spec` (usa `IModelCatalog` para janelas) |
| Touches | `IAgentLlmProvider`, `OpenAiCompatLlmProvider`, `AgentLlmOptions`, `AgentRunner`, `LlmCompletionLogEvent`, `TokenEvent` |

## 1. User Story

**As a** operador que roda OmniRoute (vários providers/modelos atrás de um
endpoint OpenAI-compat)
**I want** que o OpenHandsNET descubra os modelos disponíveis, conheça a
janela de contexto de cada um, escolha o modelo certo por papel
(worker/summarizer/planner) e faça retry/fallback automático
**So that** uma falha de provider/modelo não mata a run, modelos baratos
fazem o trabalho barato (summaries, embeddings), e a troca de modelo no meio
da conversa respeita o contexto (SPEC-22).

**Problem context:**

- `AgentLlmOptions` tem **um** `Model` + `BaseUrl` + `MaxRetries` — sem
  fallback chain, sem descoberta, sem noção de janela.
- `switch_acp_model` aceita qualquer string — não valida existência.
- Erro de LLM (`AgentRunner.cs:258`) → `AgentErrorEvent` + status `Error`:
  sem retry, sem failover. OmniRoute devolve 429/5xx transitórios e erros
  de contexto — hoje tudo é fatal.
- `ModelInfo.ContextWindow` existe mas nunca é preenchido.

## 2. Scope (RF)

### RF-001 — `IModelCatalog`

`Application/Agent/IModelCatalog.cs`:

```csharp
public interface IModelCatalog
{
    /// <summary>Modelos anunciados pelo gateway (GET {BaseUrl}/models),
    /// cacheados com TTL; overrides de config ganham.</summary>
    Task<IReadOnlyList<ModelInfo>> ListAsync(CancellationToken ct);

    /// <summary>Resolve id → ModelInfo (null = desconhecido).</summary>
    Task<ModelInfo?> GetAsync(string modelId, CancellationToken ct);
}
```

`Infrastructure/Agent/OmniRouteModelCatalog`:
- `GET {AgentLlm:BaseUrl}/models` (OpenAI-compat `{data:[{id,…}]}`) com
  `ApiKey` bearer; parse também de campos OmniRoute estendidos
  (`context_window`, `provider`, `pricing`) quando presentes.
- Cache `IMemoryCache` TTL `AgentLlm:CatalogCacheSeconds` (default 300).
- Overrides: `AgentLlm:ModelOverrides` map `{"id":{context_window,provider,
  max_output}}` — sempre ganham sobre a descoberta (janela errada em catálogo
  real é comum).
- Descoberta falha → devolve último cache; sem cache → lista com apenas o
  `Model` configurado (degradação fail-open, nunca bloqueia run).

### RF-002 — `IModelRouter` + política de seleção

`Application/Agent/IModelRouter.cs`:

```csharp
public interface IModelRouter
{
    /// <summary>Modelo efetivo para o papel nesta conversa/iteração.</summary>
    Task<ModelSelection> SelectAsync(
        Guid conversationId, ModelRole role, CancellationToken ct);

    /// <summary>Próximo candidato após falha (fallback chain, sem repetir).</summary>
    ModelSelection? NextFallback(ModelSelection current, LlmErrorKind kind);
}

public enum ModelRole { Worker, Summarizer, Planner }
public sealed record ModelSelection(
    string Model, string? Provider, int? ContextWindow, int Attempt,
    string? FallbackFrom);
```

Config `AgentLlm`:
- `Model` — worker default (existente).
- `RoleModels` — `{summarizer:"gpt-5-mini", planner:"claude-opus"}`;
  ausente → `Model`.
- `FallbackModels` — `["b","c"]` aplicados a qualquer role quando o primário
  falha.
- `MaxRetries` (existente) — tentativas **por modelo** antes de ir ao
  fallback; backoff exponencial 1s·2ⁿ + jitter, cap 30s.

Runner passa a chamar `llm.ChatStreamAsync` com o `ModelSelection` do role
`Worker`; o condenser (SPEC-22) usa role `Summarizer`; futuro planner usa
`Planner`.

### RF-003 — Turn recovery (classificação de erro → ação)

`OpenAiCompatLlmProvider` mapeia resposta/exception para `LlmErrorKind`:

| Kind | Detecção | Ação do runner |
|---|---|---|
| `ContextOverflow` | 400 + `context_length|maximum context|too many tokens` | SPEC-22: promote/compact + retry 1× |
| `RateLimited` | 429, `rate_limit` | retry mesmo modelo (backoff), depois fallback |
| `Transient` | 5xx, timeout, `IOException` | retry mesmo modelo ×`MaxRetries` → fallback |
| `ModelUnavailable` | 404/`model_not_found`, `model` em decommission | fallback imediato |
| `Auth` | 401/403 | erro fatal com mensagem clara (sem retry) |
| `Unknown` | resto | retry 1× → erro |

Cada tentativa emite `ConversationStateUpdateEvent(key="llm_route")`:
`{model, role, attempt, kind?, fallback_from?, latency_ms?}` — a UI mostra
"tentando claude-sonnet (fallback de gpt-5)" em tempo real. Falha final →
`AgentErrorEvent` como hoje (inalterado).

### RF-004 — Telemetria de chamada enriquecida

`LlmCompletionLogEvent.LogData` passa a serializar o envelope completo:
`{model, provider?, role, attempt, fallback_from, latency_ms,
prompt_tokens, completion_tokens, finish_reason, cost_usd?}`. `TokenEvent`
ganha campos opcionais `Model`, `Role` (nullable → compat retroativo).
Métricas: `ohn_llm_calls{model,role,outcome}`, `ohn_llm_latency_ms{model}`.

### RF-005 — Validação de modelo + integração switch/contexto

- `POST …/switch_acp_model`: valida contra `IModelCatalog` quando a
  descoberta está saudável → `422 {error:"unknown_model", available:[…]}`
  (catálogo indisponível → aceita como hoje, fail-open).
- `GET /api/v1/models` (auth read): lista do catálogo + `role_models`
  efetivos + `default`. Alimenta o `ModelSelector` da UI (SPEC-26).
- Promotion da SPEC-22 RF-006 usa `catalog.GetAsync` para escolher alvo de
  janela maior quando `PromotionTarget` não está fixo.

## 3. API Contract

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/v1/models` | read | catálogo + roles + default |
| GET | `/api/v1/app-conversations/{id}/model` | read | `{model, role_models, window?}` |
| POST | `…/switch_acp_model` | write | agora valida catálogo (422) |

Evento: `state_update` key `llm_route` (novo conteúdo — sem kind novo);
`TokenEvent` +2 campos opcionais. Fixtures: `llm_route` + TokenEvent
estendido.

## 4. Data model

Sem migration. `ConversationRecord.LlmModel` continua a fonte do modelo do
usuário; seleção por role é efêmera por run (não persiste `summarizer`
escolhido — determinístico pela config).

## 5. NFR / Security

- `ApiKey` nunca em eventos/logs; erros de auth redigidos via
  `SecretRedactor`.
- Catálogo falho **não** bloqueia runs (fail-open com último cache).
- Backoff total máximo por iteração: `AgentLlm:MaxRouteWaitSeconds` (300) —
  evita run pendurada em fallback infinito.
- OmniRoute é dependência externa opcional: `BaseUrl` vazio → catalog =
  apenas `Model` (modo single-provider direto continua funcionando).

## 6. Task Plan

1. `LlmErrorKind` + classificação no `OpenAiCompatLlmProvider` (unit tests
   por kind).
2. `IModelCatalog`/`OmniRouteModelCatalog` + overrides + cache (unit tests
   com `HttpMessageHandler` stub).
3. `IModelRouter` + seleção por role + fallback chain (unit tests).
4. Runner: loop de retry/fallback por iteração + eventos `llm_route` +
   métricas + TokenEvent/LogData enriquecidos.
5. `GET /models`, `GET …/model`, validação no switch + testes de contrato.
6. SPEC-22 hook: condenser usa role `Summarizer`; promotion usa catálogo.
7. Doc `docs/agent/models.md` (config OmniRoute, fallbacks, roles).

## 7. Deliverables

- `Application/Agent/{IModelCatalog,IModelRouter,LlmErrorKind}.cs` +
  `ModelSelection`/`ModelRole`.
- `Infrastructure/Agent/{OmniRouteModelCatalog,ModelRouter}.cs`, provider
  com classificação de erro.
- Endpoints + fixtures + `docs/agent/models.md`.

## 8. Referências

- oh-my-pi `model-registry`/catalog: descoberta por provider, regras de
  compatibilidade, context promotion — https://github.com/can1357/oh-my-pi
  (conceitos apenas).
- OmniRoute OpenAI-compat `/models` — gateway do usuário (contrato inferido;
  overrides cobrem divergências).
- OpenAI models API shape: https://platform.openai.com/docs/api-reference/models

## 9. Guardrails

- Nunca retry em `Auth`; nunca expor `ApiKey`.
- Fail-open quando catálogo indisponível; fail-closed em `Auth`.
- Retry total bounded; sem busy-loop.

## 10. DoD

- 429 simulado → backoff → sucesso; 404 de modelo → fallback chain; 401 →
  erro imediato.
- `GET /models` retorna catálogo com janelas; switch de modelo inválido → 422.
- Summarizer usa `RoleModels.summarizer` quando configurado.
- `llm_route` visível no stream durante fallback; `dotnet test` verde.

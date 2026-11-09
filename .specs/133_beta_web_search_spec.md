# SPEC — 133 · `web_search` agent tool (provider-backed)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `web-search-tool` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #367 · Epic #365 |
| Scope | `src/OpenHandsNET.Application/` + `Infrastructure/` + `Api/` + `tests/` + `docs/` |

## 1. User Story

**As a** usuário conversando com o agente, **I want** que ele consiga
buscar na internet (não apenas fetch de uma URL que eu dei), **so that**
ele responda perguntas factuais/atuais e descubra recursos por conta
própria — paridade com OpenHands Cloud, Devin e OpenCode, que já
empacotam search.

## 2. Contexto / escopo

Hoje o agente tem `web_fetch` (`BuiltinTools.cs:1075` — http(s) only,
SSRF guard, via `Application.Web.IWebFetchService`) e `search` = busca
em arquivos do workspace (ripgrep). **Não existe busca web por query** —
o agente não consegue descobrir URLs, só consumi-las.

Decisões de design:

- `IWebSearchService` em `Application.Web` (mesma pasta do
  `IWebFetchService`), implementação em `Infrastructure/Web/`.
- **Provider plugável por config** `WebSearch:Provider`:
  - `duckduckgo` (**default, zero config**) — endpoint HTML lite
    (`html.duckduckgo.com/html/`) com parse de resultados; funciona
    out-of-the-box para self-hosted beta sem nenhuma key.
  - `tavily` — `POST https://api.tavily.com/search` com `WebSearch:ApiKey`.
  - `brave` — `GET https://api.search.brave.com/res/v1/web/search` com
    `X-Subscription-Token` de `WebSearch:ApiKey`.
  - `none` — desliga o tool (não é registrado).
- Tool `web_search` em `BuiltinTools` (eager, ao lado do `web_fetch`):
  args `{query: string, max_results?: int (default 5, max 10)}` →
  JSON `[{title, url, snippet}]` ou `ToolResult.Error("web_search
  provider …")` quando falha — **nunca** resultado enlatado.
- Segurança: mesma borda do `web_fetch` — outbound só para o endpoint do
  provider configurado (não é fetch arbitrário; SSRF surface mínima);
  resultados são dados não-confiáveis passados como texto (prompt
  injection mitigado por truncamento de snippet ~300 chars e contagem
  máxima). Honra `Egress` allowlist quando habilitada (SPEC-19): host do
  provider precisa estar na lista — documentado.
- Rate-limit/cache: cache in-memory por `(query, max_results)` TTL 5 min
  por processo (evita martelo do mesmo query em retries); respeita
  429/backoff do provider com erro limpo.
- Permissões: entra no `PermissionedToolRegistry` como tool de rede —
  `allow/deny/ask` por regra já funciona sem código extra.
- Telemetria: span OTel `tool.web_search` (mesmo padrão dos outros
  tools) + contador de chamadas por provider.

Fora de escopo: search-as-MCP-default (o tool builtin cobre o caso),
busca de imagens/notícias verticalizadas, scraping de SERP do Google
(ToS), cache persistente cross-run, `web_fetch` auto-follow dos
resultados (o agente decide — composição natural).

## 3. Requisitos

- RF-001: `IWebSearchService` → `Task<IReadOnlyList<WebSearchResult>>
  SearchAsync(string query, int maxResults, CancellationToken ct)`;
  `WebSearchResult {Title, Url, Snippet}`.
- RF-002: `DuckDuckGoSearchService` (default) — parse resiliente
  (resultados ausentes → lista vazia, não exceção); UA honesto;
  timeout 10 s.
- RF-003: `TavilySearchService` e `BraveSearchService` ativados por
  `WebSearch:Provider` + `ApiKey` — sem key → `WebSearch:Provider`
  cai para `duckduckgo` com warning no boot.
- RF-004: Tool `web_search` registrado em `BuiltinTools` quando o
  serviço está ativo; schema JSON com `query` obrigatório e
  `max_results` opcional; saída JSON array de resultados.
- RF-005: Cache TTL 5 min por processo + dedup de chamadas concorrentes
  idênticas (single-flight).
- RF-006: Erros do provider (rede, 4xx/5xx, sem key) →
  `ToolResult.Error` legível; nunca retorna resultado fake.
- RF-007: testes — unit (parser DDG com HTML fixture, schema do tool,
  cache/single-flight, fallback sem key), integration (endpoint
  `tools/list` inclui `web_search` com provider default; execução com
  HttpMessageHandler stub — sem rede real).
- RF-008: docs — `environment-variables.md` (en+pt) com
  `WebSearch__Provider`, `WebSearch__ApiKey`, interação com egress
  allowlist; `docs/comparison.md` marca o item.

## 4. API Contract

Sem endpoint novo — tool do agente. Config:

| Env | Default | Notas |
|---|---|---|
| `WebSearch__Provider` | `duckduckgo` | `duckduckgo` \| `tavily` \| `brave` \| `none` |
| `WebSearch__ApiKey` | — | exigida para `tavily`/`brave`; secreta |

Tool schema (resumo): `web_search {query: string!, max_results?: int}`
→ `[{title, url, snippet}]`.

## 5. Critérios de aceite

- [ ] `web_search` aparece em `tools/list` com provider `duckduckgo`
      default (zero config) e some com `WebSearch__Provider=none`.
- [ ] Query retorna resultados reais com DDG em ambiente com rede;
      sem rede/key inválida → erro limpo, run não quebra.
- [ ] Tavily/Brave funcionam com key configurada.
- [ ] Egress allowlist bloqueia provider não listado (erro claro).
- [ ] `dotnet format` clean, build 0 warnings, testes verdes.

## 6. Task Plan

1. `IWebSearchService` + `WebSearchResult` + options `WebSearchOptions`.
2. `DuckDuckGoSearchService` (+ fixture HTML) — default.
3. Tavily/Brave services + DI por provider.
4. Tool `web_search` em `BuiltinTools` + permissão + span.
5. Cache/single-flight + testes + docs env.

## 7. Deliverables

- `Application/Web/IWebSearchService.cs`,
  `Infrastructure/Web/{DuckDuckGo,Tavily,Brave}SearchService.cs`,
  tool em `BuiltinTools.cs`, `WebSearchOptions`, testes +
  fixture DDG, `docs/environment-variables*.md`.

## 8. Referências

- `src/OpenHandsNET.Infrastructure/Agent/Tools/BuiltinTools.cs:1071-1100`
  — padrão `web_fetch` (SSRF guard, erro limpo).
- `src/OpenHandsNET.Application/Web/IWebFetchService.cs` — contrato irmão.
- `~/repos/OpenWebUiCode` — referência do upstream Open WebUI para
  web search RAG (conceito; implementação própria).
- SPEC-19 (egress allowlist), SPEC-123 (tools infra/permissions).

## 9. Guardrails

- Zero dependência nova de runtime para o provider default (parse HTML
  com `System.Text.RegularExpressions`/`HtmlAgilityPack` **só** se já
  for dependência do projeto — senão regex mínimo).
- `ApiKey` via `SecretNamesJson`/config mascarada — nunca serializada
  em logs nem respostas; `SecretRedactor` cobre o valor.
- Resultados = conteúdo não-confiável: snippet truncado (~300 chars),
  máx 10 resultados; não executar nada contido neles.
- Sem chamada de rede real em testes — sempre handler stub/fixture.

## 10. DoD

- Tool `web_search` funcional com provider zero-config + 2 providers
  por key; permissões/telemetria no padrão; testes sem rede; docs env
  atualizados; acceptance verificado em run real (agente usa a tool num
  prompt de pergunta factual).

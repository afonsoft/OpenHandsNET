# SPEC — 93 · Perf/loadtest harness — mock LLM + cenarios de carga

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `perf-loadtest-harness` |
| Type | `Infra / Tooling` |
| Stack | `.NET 10 · ASP.NET Core · SignalR · Python 3 (aiohttp)` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | análise comparativa `afonsoft/open-webui` (2026-10-22) |
| Depends on | — |
| Reference | `open-webui` `tools/perf/` (`mock_ollama.py`, `loadtest.py`, README) |
| Touches | `tools/perf/` (novo), `docs/` (runbook), opcional: `Agent:MockLlm*` config |

## 1. User Story

**As a** mantenedor da API + agent runtime
**I want** um harness de carga reproduzível — provider LLM mockado
determinístico + cliente async medindo throughput, latências
(p50/p90/p95/p99), TTFT de stream e erros
**So that** regressões de performance (SignalR streaming, fan-out de
subagents, criação de conversa/run, EF queries quentes) sejam medidas
antes do merge em vez de descobertas em produção.

**Contexto:** `evals/` mede **qualidade** do agente (suítes/tasks/
recordings), não **carga** — não existe ferramenta que encha a API com
requests concorrentes ou runs simultâneos com streaming. O open-webui
tem `tools/perf/` maduro: mock Ollama aiohttp com gatilhos no conteúdo
(`__delegate__N__` → tool_calls de delegate, `__words__N` → N chunks de
stream, `__sleep__MS` → latência) + `loadtest.py` com cenários `rps`,
`sessions`, `subagents`, `real`. O equivalente OpenHandsNET precisa
mockar o gateway **OpenAI-compatible** (nosso `OpenAiCompatLlmProvider`
+ `ModelRouter`, não Ollama) e medir o streaming **SignalR** dos hubs
(não SSE).

## 2. Escopo

In-scope:

- `tools/perf/mock_llm.py`: servidor aiohttp OpenAI-compatible —
  `GET /v1/models`, `POST /v1/chat/completions` (SSE stream ou JSON
  único, conforme `stream`), `POST /v1/embeddings`. Gatilhos na última
  mensagem do usuário:
  - `__task__N` → tool_call do `task` builtin (fan-out de subagents,
    `wait=false` análogo);
  - `__words__N` → N pedaços de `delta.content` no stream;
  - `__sleep__MS` → latência artificial antes do 1º token;
  - `__finish__` → resposta com `finish` tool_call (encerra run).
  Latência configurável por resposta; log de request resumido.
- `tools/perf/loadtest.py`: cliente async (aiohttp + signalr client
  leve ou WebSocket handshake do hub negociado) com cenários:
  - `rps` — mix ponderado de endpoints quentes (list/search
    conversations, get events paginado, healthz, start-run enqueue)
    por `--duration` com `--workers`;
  - `conversations` — `--users` × `--convs-per-user` × `--rounds`,
    cada conversa com run ativo (POST → attach SignalR eventos → fim);
  - `subagents` — `--fanout` conversas disparam `task` (análogo a
    `delegate_task`), contando filhos spawnados/completados;
  - relatório: rps efetivo, p50/p90/p95/p99 por endpoint, TTFT do
    stream, tokens/s agregado, taxa de erro.
- Config: `Agent:LlmProvider=Mock`/`LLM_BASE_URL` apontando pro mock
  (documentar combinação mínima em `.env.exemplo` comentado ou
  `tools/perf/README.md`); DB efêmero (`ConnectionStrings` para
  `/tmp/ohn-loadtest.db`).
- `tools/perf/README.md`: como rodar local (3 comandos), baseline de
  referência no hardware do autor, como interpretar o relatório.

Out-of-scope: gate de CI com threshold (primeiro passo é medir — gate
quantitativo vem depois que existir baseline estável); k6/Gatling;
carga em k8s/kind; profiling de alocação (BenchmarkDotNet é outra
camada).

## 3. Requisitos

1. **RF-001 — Mock OpenAI-compatível**: endpoints mínimos usados por
   `OpenAiCompatLlmProvider` funcionando; stream SSE com
   `data: {json}\n\n` e `data: [DONE]`; tool_calls no formato
   `choices[].delta.tool_calls`.
2. **RF-002 — Determinismo**: mesma entrada → mesma sequência de
   chunks/tool_calls (seed fixa por request-id ou gatilho no conteúdo).
3. **RF-003 — Cenários**: `rps`, `conversations`, `subagents`
   executáveis com flags do README; subagents conta spawnados vs
   completados e detecta orphan runs.
4. **RF-004 — Métricas**: relatório final com p50/p90/p95/p99 por
   rota, TTFT (1º evento SignalR/SSE), tokens/s, erros por classe
   (timeout/5xx/4xx); JSON opcional `--out result.json` para
   comparação entre runs.
5. **RF-005 — Sem provider real**: cenário padrão funciona sem
   `OPENAI_API_KEY`/rede externa; dependência apenas `aiohttp`.
6. **RF-006 — Segurança**: mock nunca faz bind fora de `127.0.0.1`
   por default; harness exige auth token de teste quando a API exige
   (documentar seed/flag).

## 4. API Contract

Sem mudança de API. Mock implementa o subset OpenAI que o provider já
consome (contrato existente, não novo).

## 5. Critérios de aceite

- `mock_llm.py` + app local → `loadtest.py rps --duration 30`
  completa com relatório sem erro de protocolo.
- Cenário `subagents --fanout 5` spawna e conta filhos corretamente.
- App sob mock atende ≥30 req/s de mix leve em máquina de dev sem
  erro (baseline orientativa — número real registrado no README após
  1ª medição).
- `dotnet build`/`test` intactos (nada em `src/` muda, exceto config
  opcional documentada).

## 6. Task Plan

1. `mock_llm.py` (endpoints + gatilhos + stream) + smoke manual.
2. `loadtest.py` cenário `rps` + relatório.
3. Cenários `conversations`/`subagents` com attach SignalR.
4. README + config documentada + baseline 1ª medição no PR.

## 7. Deliverables

- `tools/perf/{mock_llm.py,loadtest.py,README.md}` + baseline
  documentada.

## 8. Referências

- `src/OpenHandsNET.Infrastructure/Agent/OpenAiCompatLlmProvider.cs`,
  `Routing/ModelRouter.cs`, hubs SignalR
  (`Api/` — `ConversationHubClient` no Client mostra o contrato),
  `evals/` (suítes de qualidade — complementar).
- `open-webui/tools/perf/README.md` (cenários e gatilhos),
  `mock_ollama.py`, `loadtest.py`.

## 9. Guardrails

- Harness fora do caminho de produção — `tools/`, não `src/`; nenhuma
  dependência nova no runtime.
- Mock com bind default `127.0.0.1`; sem coleta de dados reais.
- Não usar para benchmarking de providers reais contra quota paga sem
  flag explícita.

## 10. DoD

- 3 cenários rodando localmente com relatório de latências; baseline
  registrada; README reproduzível; build/test verdes.

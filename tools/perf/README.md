# tools/perf — harness de carga (SPEC-93)

Mede **performance** da API + agent runtime (complementar ao `evals/`,
que mede *qualidade*). Dois componentes, dependência única: `aiohttp`.

| Arquivo | Papel |
|---|---|
| `mock_llm.py` | servidor OpenAI-compatible (`/v1/models`, `/v1/chat/completions` SSE ou JSON, `/v1/embeddings`) com gatilhos determinísticos |
| `loadtest.py` | cliente async — cenários `rps`, `conversations`, `subagents` + relatório p50/p90/p95/p99, TTFT SignalR, tokens/s, erros |

## Quickstart (3 comandos)

```bash
python3 tools/perf/mock_llm.py &                      # 1. mock em 127.0.0.1:11439
OPENHANDSNET_AgentLlm__BaseUrl=http://127.0.0.1:11439/v1 \
OPENHANDSNET_AgentLlm__ApiKey=sk-mock \
OPENHANDSNET_AgentLlm__Model=mock-fast \
dotnet run --project src/OpenHandsNET.Api &           # 2. app contra o mock
python3 tools/perf/loadtest.py rps --duration 30      # 3. carga
```

Nenhum provider real, chave ou rede externa é necessário (RF-005).
Para cenários com auth, gere um token de teste e passe `--token <jwt>`.

## Gatilhos do mock (na última mensagem `user`)

| Gatilho | Efeito |
|---|---|
| `__task__N` | N `tool_calls` do builtin `task` (`background:true` — fan-out de subagents) |
| `__words__N` | N chunks `delta.content` no stream |
| `__sleep__MS` | latência artificial antes do 1º token |
| `__finish__` | `tool_call` do builtin `finish` (encerra o run) |
| msg `role=tool` | resposta texto curto (loop termina) |

Porta default `11439`; mude com `python3 mock_llm.py <porta>`.

## Cenários

```bash
# mix ponderado de endpoints quentes por N segundos
python3 tools/perf/loadtest.py rps --duration 30 --workers 8

# conversas com run ativo: POST start → attach SignalR → 1º evento (TTFT)
python3 tools/perf/loadtest.py conversations \
    --users 2 --convs-per-user 2 --rounds 1 --token <jwt>

# fan-out de subagents: conta spawned vs completed, detecta orphans
python3 tools/perf/loadtest.py subagents --fanout 5 --fanout-conv 4 \
    --token <jwt>
```

## Relatório

```json
{ "rps": 512.3, "routes": {"GET /api/v1/models": {"p50": 2.2, "p99": 4.7}},
  "ttft_ms": {"p50": 120.0}, "tokens_per_s": 840.0,
  "errors": {"timeout": 0, "5xx": 0, "4xx": 0} }
```

`--out result.json` salva o mesmo payload para comparar runs.

## Baseline de referência

Registro da 1ª medição (mock local, `rps --duration 2 --workers 3`,
4xx esperados por endpoints inexistentes no mock):

| métrica | valor |
|---|---|
| throughput | ~1400 req / 2s (~700 rps) |
| p50 por rota | ~2.2 ms |
| p99 por rota | ~4.7 ms |

Números de baseline **real** (app + mock) devem ser atualizados aqui após
a 1ª medição local com o app rodando — SPEC-93 é "medir primeiro", gate
quantitativo de CI fica para um SPEC futuro.

## Segurança

- Mock faz bind só em `127.0.0.1` (RF-006) — nunca exponha.
- Harness vive em `tools/` — zero dependências novas no runtime.

# Troubleshooting — runbook operacional

Guia de diagnóstico para deployments self-hosted (compose ou Helm/k8s).
Comece pelo **support bundle**, mantenha a investigação **read-only** e
aplique correções por configuração — nunca `kubectl edit`/`docker rm` ad hoc.

## 1. Support bundle primeiro

`GET /api/v1/support-bundle` (ou o link na página `/admin`) baixa um ZIP
com: readiness detalhado, versões, entity counts, tail dos logs
(`openhandsnet-*.log`) e a configuração efetiva **com todo valor
sensível mascarado** (redação coberta por teste).

```bash
curl -sf -o bundle.zip http://localhost:8080/api/v1/support-bundle
```

O bundle pode conter configuração sensível — **revise antes** de anexar a
um ticket. Um summary saudável do analyzer não prova que a UI abre uma
conversa: faça os checks da seção 4.

## 2. Estado do sistema

### Compose

```bash
docker compose ps                              # status por serviço
docker compose logs --tail=200 app             # app (API + UI)
docker compose logs --tail=100 runtime-api     # só no modo split (provider=remote)
docker ps --filter label=openhandsnet.managed  # sandboxes ativos
```

### Kubernetes

```bash
kubectl get pods -n openhandsnet -o wide
kubectl get pods -n openhandsnet-sandboxes -o wide
kubectl get events -n openhandsnet --sort-by=.metadata.creationTimestamp
kubectl describe pod -n openhandsnet <pod>
kubectl logs -n openhandsnet <pod> --all-containers=true --previous --timestamps
kubectl top pods -n openhandsnet               # se a Metrics API existir
```

Comece pelas colunas `STATUS`/`READY`/`RESTARTS`: `Pending` = scheduling/
storage; `CrashLoopBackOff` = dependência ou healthcheck; restarts =
dependência/limite de recurso. `kubectl logs --previous` depois de um
restart.

## 3. Componente → sintoma

| Componente | Investigue quando |
|---|---|
| `app` (Api/UI) | erros de UI/API, conversas, `dotnet` logs, migration no boot |
| `runtime-api` | create/pause/cleanup de sandbox (provider `remote`) |
| pods `ohn-sandbox-*` / `ohn-pool-*` | conversa específica não sobe; warm pool não clama |
| `litellm` | modelo rejeitado, auth do proxy (BYOR), budget |
| `keycloak` | login/SSO, tokens, client redirect |
| `postgres` | readiness `database` falho, migrations |
| `valkey` | backplane multi-réplica (cache/rate-limit compartilhados) |

## 4. Checks de primeira conversa

| Sintoma | Check |
|---|---|
| Login OK, mas UI pede backend/API key | `DeploymentMode`/auth config; frontend servido pelo app em `:8080`; ingress do chart (valores de versão do chart) |
| "Failed to start sandbox" | logs do engine (`DockerSandboxEngine`/`KubernetesSandboxEngine`): daemon alcançável? imagem puxável? `runtime-api` up e `X-Runtime-Key` **igual** dos dois lados (`Sandbox:RemoteApiKey` = `RuntimeApi:ApiKey`) — compare sem imprimir valores |
| Primeiro request de modelo falha (proxy token/modelo/endpoint) | BYOR strict rejeita modelo fora do `GET /models` do proxy → `agent_diagnostic` `byor_model_rejected`; perfil LLM do usuário vs `LlmProxy:Mode` |
| Egress `deny` quebra clone/LLM | `Sandbox:EgressMode` + `host` clone mode ou mirror interno (ver `dind.md`) |
| Warm pool não é usado | pool só serve a **default spec**; spec custom → cold start (ver `kubernetes.md`/`dind.md`) |

Diagnóstico rápido: `openhandsnet doctor` (ambiente), `doctor --preflight`
(gate de instalação: DNS/egress/portas/socket docker), `doctor --k8s`
(cluster, `runtimeclass sysbox-runc`, StorageClass). Probes HTTP:
`/api/v1/health` (liveness) e `/api/v1/readiness` (dependências reais —
`database` faz `SELECT 1`; 503 = app não alcança/autentica no PG).

## 5. Debug logging temporário

1. `Logging__LogLevel__Default=Debug` (env) — ou `Serilog` override no
   compose/chart.
2. Reproduza o problema.
3. Colete os logs (ou um support bundle).
4. Volte para `Information` e faça rollout.

Não deixe DEBUG ligado em produção — volume alto e dados verbosos.

## 6. Sintomas específicos

- **Migration falha no boot**: banco em encoding não-UTF8 (LATIN1) ou
  versão < 16 — ver `external-postgres.md` (requisitos).
- **DinD não sobe no sandbox**: kernel < 6.3 com sysbox; ou
  `EnableDinD` sem runtime hardened em compose (sidecar privileged — ver
  `dind.md` matriz de mecanismos).
- **`byor_model_rejected`**: modelo fora da lista permitted do proxy —
  ajustar `deploy/litellm.yaml` ou o perfil do usuário.
- **Rate limit 429 inesperado**: `RateLimit:*` por ator/rota — subir o
  limite ou verificar `X-RateLimit-*` no response.
- **Input blocked 403**: `IInputGuard` (`pattern`) — revisar a mensagem;
  `security_policy.input_guard=llm` usa `InputGuard:LlmEndpoint`.

## Referências

- `cli.md` — `doctor`, exit codes.
- `ui/admin.md` — support bundle, páginas admin.
- `deployment/{dind,external-postgres,kubernetes,security,egress-allowlist}.md`
- Upstream: <https://docs.openhands.dev/enterprise/troubleshooting>
  (conceitos).
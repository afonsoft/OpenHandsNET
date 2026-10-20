# SPEC-81: k8s-e2e — nome do cluster kind divergente

| Campo | Valor |
|---|---|
| Status | Completed |
| Gap key | GAP-automation-k8s-e2e-kind-name |
| Type | Automation/CI |
| Fonte | runs `k8s-e2e.yaml` falhos 2026-10-18/19/20 — `ERROR: no nodes found for cluster "kind"` |

## User Story

Como mantenedor, quero o workflow diário `k8s-e2e` verde — ele falha
deterministicamente há 3 execuções por divergência de nome de cluster.

## Contexto / evidência

- `helm/kind-action@ef37e7f…` cria o cluster com nome default
  **`chart-testing`** (log: `Creating cluster "chart-testing" ...`).
- O step `build + load api image` roda `kind load docker-image
  openhandsnet:e2e` sem `--name` → procura cluster `kind` →
  `ERROR: no nodes found for cluster "kind"` (job `k8s-e2e`, run mais
  recente, linha ~1223).
- `.github/workflows/k8s-e2e.yaml:44-56` — mudança em workflow exige
  revisão humana (hard rule #6).

## Requisitos funcionais

1. **RF-001 — Um dos dois** (escolher na execução):
   - `with: cluster_name: kind` na `helm/kind-action`, **ou**
   - `kind load docker-image openhandsnet:e2e --name chart-testing`.
   Preferência: `cluster_name: kind` (alinha com o nome assumido no
   restante do workflow e em docs).
2. **RF-002 — Verificar** se outros comandos `kind`/`kubectl` no
   workflow assumem o nome (auditar o arquivo inteiro).

## Critérios de aceite

- [ ] Run diário do `k8s-e2e` verde (smoke passa).
- [ ] Sem referência a cluster inexistente no workflow.

# SPEC — 87 · Helm: image pre-pull DaemonSet (image loader)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `image-prepull-daemonset` |
| Type | `Infrastructure / Chart` |
| Stack | `Helm 3 · k8s DaemonSet` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #226, 4733e92)` |
| Ticket | `gap-analysis 2026-10-22` · `GAP-automation-image-prepull-daemonset` |
| Depends on | `37`/`49` (k8s runtime) — entregues |
| Reference | `docs.openhands.dev/enterprise/architecture` — "Image Loader" DaemonSet (conceitos apenas) |
| Touches | `charts/openhandsnet/templates/image-loader.yaml` (novo), `values.yaml`, `docs/deployment/kubernetes.md` (+en) |

## 1. User Story

**As a** operador do deployment k8s
**I want** um DaemonSet opt-in que pré-puxa as imagens de sandbox em
todo node
**So that** cold starts e imagens custom fora do warm pool não pagam o
pull completo no primeiro uso de cada node — upstream tem o "Image
Loader" DaemonSet exatamente para isso; hoje nosso chart só oferece
warm pods (`ohn-pool-*`), que cobrem a imagem default mas não warm-start
de imagens arbitrárias nem o primeiro pull por node.

**Contexto:** warm pod resolve latência de *runtime*, não de *pull* —
pod warm ainda precisa ter a imagem no node onde foi agendado. O
DaemonSet de upstream roda `sleep`/`exit 0` sobre cada imagem de
sandbox configurada, em todo node do pool de sandboxes, mantendo o
cache quente.

## 2. Escopo

In-scope:

- Template `charts/openhandsnet/templates/image-loader.yaml`:
  DaemonSet `openhandsnet-image-loader`, `enabled` default `false`.
- `values.yaml`: `sandbox.imageLoader.{enabled,images[],resources}`
  — default `images` = `[values.image]` do sandbox
  (`sandbox.image` se existir; senão lista explícita).
- Implementation note: container `kubernetes/pause` pattern — criar
  pods com `image: <sandbox-image>` + command que dorme (`["/pause"]`
  não existe na imagem ubuntu → usar `command: ["sleep","infinity"]` com
  resources mínimos `cpu: 1m, memory: 8Mi`). Alternativa leve: init
  pattern com `preStop`… escolher a mais simples que o scheduler
  aceite (sleep pod é o padrão upstream).
- Node placement: `nodeSelector`/`tolerations` reutilizando as chaves
  do sandbox pool se existirem em values (`sandbox.nodeSelector` /
  `sandbox.tolerations` — criar se ausentes) para puxar nos nodes certos.
- Doc `kubernetes.md` (+en): seção "Image loader" — quando ligar
  (clusters com cold-start sensível, imagens custom), custo (1 pod
  dormindo por node por imagem).

Out-of-scope: garbage-collection de imagens antigas, pull de secrets
(imagePullSecrets reusa o existente do chart), métricas.

## 3. Requisitos

1. `helm template` com `imageLoader.enabled=true` gera DaemonSet válido
   com uma entry por imagem listada.
2. Default `enabled=false` — comportamento atual intacto.
3. Tolerations/nodeSelector do sandbox pool propagados.
4. Pods do DaemonSet com requests mínimos declarados (não competir com
   sandbox pool).

## 4. Task Plan

1. Template + values + schema comment.
2. `helm template` smoke local (ambos enabled=true/false).
3. Docs + mirror en.

## 5. Deliverables

- DaemonSet opt-in no chart + documentação.

## 6. Referências

- Upstream: `docs.openhands.dev/enterprise/architecture` (Image Loader).
- Repo: `charts/openhandsnet/{values.yaml,templates/}`,
  `docs/deployment/kubernetes.md`, SPEC-49 (warm pods dormant).

## 7. Guardrails

- Opt-in; sem privileged, sem volumes, sem serviceAccount próprio além
  do necessário para pull secrets (reusa `imagePullSecrets` global).
- Não confundir com warm pool: doc explicita diferença (pull vs runtime).

## 8. DoD

- `helm template` válido on/off; doc atualizado (pt+en); dry-run no
  runbook kind+sysbox se trivial.

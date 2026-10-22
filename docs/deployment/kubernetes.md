# Kubernetes — deployment (SPEC-37 + SPEC-49)

Deploy via `charts/openhandsnet` (Helm 3). O `Sandbox:Provider=kubernetes`
faz o **Api** orquestrar pods de sandbox in-cluster (ServiceAccount com
RBAC mínimo no namespace de sandboxes); `provider=remote` habilita o
runtime-api para daemons Docker externos (modo split da SPEC-35).

## Instalação

```bash
helm install openhandsnet charts/openhandsnet \
  -n openhandsnet --create-namespace \
  --set ingress.baseDomain=meu-dominio.com \
  --set secrets.existingSecret=openhandsnet-secrets \
  --set postgres.enabled=true \
  --set postgres.externalSecretName=openhandsnet-postgres-dsn
```

`values.yaml` cobre: ingress wildcard (`app|auth|llm-proxy|runtime.<base>`,
className + cert-manager + cookie-affinity p/ SignalR), HPA do Api
(CPU 70%, min 1 — backplane via valkey com `replicas>1`), postgres
externalSecret (produção) ou pod dev, resources por Deployment, e
NetworkPolicy default-deny interna.

## Sandbox provider kubernetes

- **Warm pool** (SPEC-49 RF-001): `sandbox.warmPoolImages="img:2;…"` —
  pods `ohn-pool-*` dormant com `OHN_DEFERRED_INIT`; o claim deleta o pod
  warm e o recria como `ohn-sandbox-*` já com a PVC da conversa (imagem
  no node → claim ~segundos; deferred-init via `POST /api/init`). TTL de
  pod warm ocioso: `Sandbox:Kubernetes:WarmPodTtlMinutes` (default 30).
- **Sleep/wake** (RF-002): pods não são pausáveis — pause/stop faz
  snapshot do spec no ConfigMap `ohn-sleep-{pod}` e deleta o pod; a PVC
  `ohn-ws-{conv}` é retida; resume recria o pod sobre o mesmo PVC →
  workspace preservado.
- **Runtime class**: `sandbox.runtimeClassName` (default `sysbox-runc`) —
  requisito para DinD dentro do sandbox. Sem `privileged:true`; a
  isolação vem do user-namespace do sysbox.

## Runbook — validação em kind + sysbox

```bash
# 1. Cluster + sysbox (instalador oficial):
kind create cluster --name ohn
kubectl apply -f https://raw.githubusercontent.com/nestybox/sysbox/master/sysbox-k8s-manifests/sysbox-install.yaml
kubectl -n kube-system rollout status ds/sysbox-deploy-k8s

# 2. Imagens (build local + kind load):
docker build -t openhandsnet:dev .
kind load docker-image openhandsnet:dev --name ohn

# 3. Install + smoke:
helm install ohn charts/openhandsnet -n ohn --create-namespace \
  --set image.tag=dev --set ingress.enabled=false \
  --set sandbox.provider=kubernetes
kubectl -n ohn port-forward svc/openhandsnet-api 8080:8080
curl -sf http://localhost:8080/api/v1/ready
# POST /app-conversations → pod sobe no ns openhandsnet-sandboxes;
# exec roundtrip, sleep/wake (ConfigMap ohn-sleep-*), warm claim.
```

`openhandsnet doctor --preflight --k8s` cobre cluster alcançável,
`runtimeclass sysbox-runc` presente e StorageClass default para PVC RWO.

## CI e2e

`.github/workflows/k8s-e2e.yaml` (workflow_dispatch manual): kind +
sysbox + helm lint/template + smoke — não roda no CI de PR (custo).

## Image loader (SPEC-87)

Opt-in (`sandbox.imageLoader.enabled=true`): DaemonSet
`openhandsnet-image-loader` pré-puxa as imagens de sandbox em **todo
node** (um pod `sleep infinity` por imagem — o pull acontece no
scheduling). Diferente do warm pool (que cobre a *latência de runtime*
da imagem default), o loader cobre **cold starts e imagens custom**:
sem ele, o primeiro sandbox em um node fresco paga o pull completo.

```bash
helm install ohn charts/openhandsnet -n ohn \
  --set sandbox.imageLoader.enabled=true \
  --set sandbox.imageLoader.images[0]=ghcr.io/openhandsnet/runtime:0.1.0 \
  --set sandbox.imageLoader.nodeSelector.pool=sandbox
```

Sem `images:` → usa `image.repository:image.tag` do chart. Props
`nodeSelector`/`tolerations` apontam o loader para o pool de sandboxes
(taint). Custo: 1 pod dormindo por node por imagem, requests mínimos
(1m CPU / 8Mi).

## Mapping compose → K8s (referência)

| Compose | Kubernetes |
|---|---|
| `app` | Deployment + Service + Ingress |
| `openhandsnet-data` | PVC (SQLite) — enterprise: PostgreSQL externo |
| `postgres` | managed (RDS/CloudSQL) via externalSecret ou pod dev |
| `valkey/redis` | Deployment valkey (provider=redis) |
| sandboxes `docker.sock` | pods `ohn-sandbox-*` via engine k8s (sysbox) |

Valores mapeados 1:1 de `.env.exemplo` (`OPENHANDSNET_*` → `env:`);
secrets via `existingSecret`/`envFrom` — nunca em `values.yaml`.

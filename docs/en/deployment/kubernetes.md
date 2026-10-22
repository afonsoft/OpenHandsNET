# Kubernetes — deployment (SPEC-37 + SPEC-49)

Deploy via `charts/openhandsnet` (Helm 3). With
`Sandbox:Provider=kubernetes` the **Api** orchestrates sandbox pods
in-cluster (ServiceAccount with minimal RBAC in the sandbox namespace);
`provider=remote` enables the runtime-api for external Docker daemons
(SPEC-35 split mode).

## Install

```bash
helm install openhandsnet charts/openhandsnet \
  -n openhandsnet --create-namespace \
  --set ingress.baseDomain=my-domain.com \
  --set secrets.existingSecret=openhandsnet-secrets \
  --set postgres.enabled=true \
  --set postgres.externalSecretName=openhandsnet-postgres-dsn
```

`values.yaml` covers: wildcard ingress
(`app|auth|llm-proxy|runtime.<base>`, className + cert-manager +
cookie-affinity for SignalR), Api HPA (CPU 70%, min 1 — valkey
backplane when `replicas>1`), postgres externalSecret (prod) or dev
pod, per-Deployment resources, and internal default-deny NetworkPolicy.

## Kubernetes sandbox provider

- **Warm pool** (SPEC-49 RF-001): `sandbox.warmPoolImages="img:2;…"` —
  dormant `ohn-pool-*` pods with `OHN_DEFERRED_INIT`; claiming deletes
  the warm pod and recreates it as `ohn-sandbox-*` bound to the
  conversation PVC (image already on node → ~seconds claim;
  deferred-init via `POST /api/init`). Idle warm pod TTL:
  `Sandbox:Kubernetes:WarmPodTtlMinutes` (default 30).
- **Sleep/wake** (RF-002): pods cannot be paused — pause/stop snapshots
  the pod spec into ConfigMap `ohn-sleep-{pod}` and deletes the pod;
  the `ohn-ws-{conv}` PVC is retained; resume recreates the pod over
  the same PVC → workspace preserved.
- **Runtime class**: `sandbox.runtimeClassName` (default `sysbox-runc`)
  — required for DinD inside the sandbox. No `privileged:true`;
  isolation comes from the sysbox user-namespace.

## Runbook — kind + sysbox validation

```bash
# 1. Cluster + sysbox:
kind create cluster --name ohn
kubectl apply -f https://raw.githubusercontent.com/nestybox/sysbox/master/sysbox-k8s-manifests/sysbox-install.yaml
kubectl -n kube-system rollout status ds/sysbox-deploy-k8s

# 2. Images:
docker build -t openhandsnet:dev .
kind load docker-image openhandsnet:dev --name ohn

# 3. Install + smoke:
helm install ohn charts/openhandsnet -n ohn --create-namespace \
  --set image.tag=dev --set ingress.enabled=false \
  --set sandbox.provider=kubernetes
kubectl -n ohn port-forward svc/openhandsnet-api 8080:8080
curl -sf http://localhost:8080/api/v1/ready
# POST /app-conversations → pod up in openhandsnet-sandboxes ns;
# exec roundtrip, sleep/wake (ohn-sleep-* ConfigMap), warm claim.
```

`openhandsnet doctor --preflight --k8s` covers cluster reachability,
`sysbox-runc` runtime class, and a default StorageClass for RWO PVCs.

## CI e2e

`.github/workflows/k8s-e2e.yaml` (manual workflow_dispatch): kind +
sysbox + helm lint/template + smoke — does not run on PR CI (cost).

## Image loader (SPEC-87)

Opt-in (`sandbox.imageLoader.enabled=true`): a DaemonSet
`openhandsnet-image-loader` pre-pulls sandbox images on **every node**
(one `sleep infinity` pod per image — the pull happens at scheduling).
Unlike the warm pool (which covers *runtime latency* for the default
image), the loader covers **cold starts and custom images**: without it,
the first sandbox on a fresh node pays the full pull.

```bash
helm install ohn charts/openhandsnet -n ohn \
  --set sandbox.imageLoader.enabled=true \
  --set sandbox.imageLoader.images[0]=ghcr.io/openhandsnet/runtime:0.1.0 \
  --set sandbox.imageLoader.nodeSelector.pool=sandbox
```

Without `images:` it defaults to `image.repository:image.tag`. The
`nodeSelector`/`tolerations` props point the loader at the (tainted)
sandbox pool. Cost: 1 sleeping pod per node per image, minimal requests
(1m CPU / 8Mi).

## Compose → K8s mapping (reference)

| Compose | Kubernetes |
|---|---|
| `app` | Deployment + Service + Ingress |
| `openhandsnet-data` | PVC (SQLite) — enterprise: external Postgres |
| `postgres` | managed (RDS/CloudSQL) via externalSecret or dev pod |
| `valkey/redis` | valkey Deployment (provider=redis) |
| sandboxes `docker.sock` | `ohn-sandbox-*` pods via k8s engine (sysbox) |

Values map 1:1 from `.env.exemplo` (`OPENHANDSNET_*` → `env:`);
secrets via `existingSecret`/`envFrom` — never in `values.yaml`.

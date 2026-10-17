# Kubernetes — direction (not yet delivered)

This document maps the compose deployment to K8s/Helm concepts, inspired
by the OpenHands-Cloud architecture (Polyform — concepts, not code).
SPEC-49 delivers the production chart; a skeleton exists today at
`charts/openhandsnet/` (`api`, `runtime-api`, `valkey`, `rbac`,
`namespace`).

## Compose → K8s mapping

| Compose | Kubernetes |
|---|---|
| `app` | Deployment + Service + Ingress; probes at `/api/v1/health` (liveness) and `/api/v1/ready` (readiness, gated by the migrator) |
| `openhandsnet-data` | PVC (SQLite) or StatefulSet — enterprise should prefer external PostgreSQL |
| `postgres` | StatefulSet or managed (RDS/CloudSQL) |
| `otel-collector` | DaemonSet or `otel-collector` Deployment with OTLP |
| `prometheus`/`grafana` | kube-prometheus-stack |
| `keycloak` | codecentric/keycloakx Helm chart |
| `litellm` | Deployment + ConfigMap (`deploy/litellm.yaml`) |
| sandbox `docker.sock` | **Doesn't work on K8s** — replace with remote Runtime API (below) |

## Runtime API / warm pool (upstream direction)

The upstream cloud isolates agents via a Runtime API + warm pools of
pre-warmed runtimes. The OpenHandsNET equivalent on K8s:

1. Swap `DockerClient` for a remote `SandboxProvider` (API contract
   already isolated via `ISandboxService`/`ISandboxExecutor` —
   `KubernetesSandboxEngine` exists since SPEC-37).
2. Worker pool StatefulSet keeping N hot sandboxes per spec —
   warm pool delivered in SPEC-49 (`ClaimWarmAsync`).
3. `Sandbox:DefaultImage` → versioned runtime image in the registry.

## Helm (SPEC-49)

Values mapped 1:1 from `.env.exemplo` (`OPENHANDSNET_*` → `env:`
entries); secrets via `existingSecret`/`envFrom`; `docker.sock` absent
by design — K8s requires the remote provider. The complete chart adds
Ingress (wildcard `app|auth|llm-proxy|runtime.<dom>` + TLS), external
Postgres, resource requests/limits, NetworkPolicy, HPA and the
kind+sysbox validation runbook.

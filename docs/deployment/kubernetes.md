# Kubernetes — direção (não entrega)

Este documento mapeia o deployment compose para conceitos K8s/Helm, inspirado
na arquitetura do OpenHands-Cloud (Polyform — conceitos, não código). Não
inclui chart na v1.

## Mapping compose → K8s

| Compose | Kubernetes |
|---|---|
| `app` | Deployment + Service + Ingress; probes em `/api/v1/health` (liveness) e `/api/v1/ready` (readiness, gated pelo migrator) |
| `openhandsnet-data` | PVC (SQLite) ou StatefulSet — enterprise deve preferir PostgreSQL externo |
| `postgres` | StatefulSet ou managed (RDS/CloudSQL) |
| `otel-collector` | DaemonSet ou `otel-collector` Deployment com OTLP |
| `prometheus`/`grafana` | kube-prometheus-stack |
| `keycloak` | codecentric/keycloakx Helm chart |
| `litellm` | Deployment + ConfigMap (`deploy/litellm.yaml`) |
| sandboxes `docker.sock` | **Não funciona em K8s** — substituir por Runtime API remota (abaixo) |

## Runtime API / warm pool (direção upstream)

O cloud upstream isola agentes via Runtime API + warm pools de runtimes
pré-aquecidos. O equivalente OpenHandsNET em K8s:

1. Trocar `DockerClient` por um `SandboxProvider` remoto (API contract já
   isolado por `ISandboxService`/`ISandboxExecutor`).
2. Worker Pool StatefulSet mantendo N sandboxes quentes por spec.
3. `Sandbox:DefaultImage` → imagem de runtime versionada no registry.

## Helm (quando existir)

Valores mapeados 1:1 de `.env.exemplo` (`OPENHANDSNET_*` → `env:` entries);
secrets via `existingSecret`/`envFrom`; `docker.sock` ausente por design —
K8s exige o provider remoto.

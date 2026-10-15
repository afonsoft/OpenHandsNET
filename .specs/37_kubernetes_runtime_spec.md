# SPEC — 37 · Kubernetes Runtime Provider (pods + Sysbox + PVC)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `kubernetes-runtime` |
| Type | `Infra / Enterprise` |
| Stack | `.NET 10 · KubernetesClient · Helm · Sysbox` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` (enterprise wave; origem SPEC-32) |
| Ticket | #76 |
| Depends on | `03` (sandbox), `14` (ops/sizing), `16` (lifecycle), `17` (image), `35` (runtime-api split) |
| Touches | `ISandboxService` provider, deploy/ (Helm), docs |

## 1. User Story

**As a** operador enterprise
**I want** sandboxes como pods Kubernetes (com Sysbox p/ DinD seguro e
PVC p/ workspace persistente) atrás do runtime-api
**So that** o produto atinge paridade real com o runtime cloud do
upstream — autoscaling, node affinity, resource limits por sandbox —
sem depender do Docker socket do host do Api.

**Contexto:** `DockerSandboxService` é single-host. O upstream
enterprise usa k0s embedded (Replicated KOTS) + Sysbox p/
docker-in-docker seguro. Aqui entregamos o provider k8s **genérico**
(qualquer cluster), não o installer k0s — esse fica como follow-up de
packaging (conceitos apenas — Polyform).

## 2. Scope (RF)

### RF-001 — `KubernetesSandboxService`

Impl de `ISandboxService` (selecionada por `Sandbox:Provider=k8s` ou
via runtime-api da SPEC-35 — preferido):

- Sandbox = `Pod` (`runtimeClassName: sysbox-runc`) com container
  `sandbox-agent` (imagem SPEC-17) + sidecars conforme spec
  (egress proxy Squid da SPEC-19 → `NetworkPolicy` equivalente).
- Workspace = `PersistentVolumeClaim` por sandbox (`ReadWriteOnce`);
  sleep/wake = pod delete/recreate no mesmo PVC (spec do pod é
  deterministicamente derivável do `SandboxSpec` + labels).
- Pause: `kubectl exec freeze` **não existe** — pause = scale-to-zero
  lógico (pod stopped, PVC retained) — mapeia o `Stop` da SPEC-16;
  `Pause` real (memory-preserving) fica `NotSupported` no provider k8s
  (documentado).
- Labels: `ohn.dev/conversation-id`, `ohn.dev/sandbox-id`,
  `ohn.dev/spec-hash`, `ohn.dev/managed-by=openhandsnet`.
- Recursos: `resources.requests/limits` por spec (cpu/mem/ephemeral);
  `LimitRange`/`ResourceQuota` opcional por namespace.

### RF-002 — DinD seguro

Sysbox (`sysbox-runc`) habilita `docker`/`docker compose` **dentro**
do pod sem privileged. Requisito documentado: cluster com Sysbox
installado (daemonset upstream sysbox). Fallback documentado:
`privileged: true` apenas em clusters dedicados (warning no doctor).

### RF-003 — Rede e acesso

- sandbox-agent exposto via `ClusterIP` interno + port-forward do
  runtime-api (ou `Ingress` quando SPEC-38 host routing ativo).
- `NetworkPolicy` default-deny egress + allowlist (mapeia SPEC-19:
  domínios → FQDN rules via Cilium/Calico; Squid sidecar continua
  opção portável).
- Namespace dedicado `openhandsnet-sandboxes` (isolado do app).

### RF-004 — Helm chart

`deploy/charts/openhandsnet/` (entregável próprio, versão própria):
Api + runtime-api + valkey (SPEC-36) + postgres opcional + rbac
mínimo (`pods`, `pvc`, `networkpolicies` no ns de sandboxes) +
values: `runtime.provider`, `sandboxes.namespace`, `sysbox.enabled`,
`sizing` presets da SPEC-14.

### RF-005 — RBAC e multi-tenancy de cluster

ServiceAccount `openhandsnet-runtime` com Role escopada ao namespace
de sandboxes — nunca ClusterRole ampla. Suporte a múltiplos namespaces
(um por org) fica [A DEFINIR] na implementação.

## 3. API Contract

Nenhum endpoint público novo — configuração `Sandbox:Provider=k8s`
+ seção `K8s:` (`KubeConfig`, `Namespace`, `SysboxEnabled`,
`StorageClass`, `DefaultResources`). `doctor --preflight` ganha checks:
cluster reachable, `sysbox-runc` existe, StorageClass provisiona,
quota do namespace.

## 4. Data model

Sem migration EF. Estado do sandbox reconciliado via labels/annotations
do pod (mesmo princípio stateless da SPEC-35).

## 5. NFR / Security

- Pods nunca `privileged` com Sysbox; sem `hostPath`, sem `hostNetwork`.
- `seccomp`/`apparmor` defaults; `runAsNonRoot` no agent quando a
  imagem suportar.
- Secrets do sandbox via `Secret` k8s ou env injetado pelo runtime-api
  (by-ref da SPEC-33 preservado — valor nunca em evento).
- PVC com `Retain` até delete da conversa (GC da SPEC-16 adaptado:
  PVC lifecycle = sandbox lifecycle).

## 6. Task Plan

1. `KubernetesSandboxService` (KubernetesClient official) — create/
   delete/status/exec via pod exec → sandbox-agent.
2. PVC workspace + sleep/wake mapping.
3. NetworkPolicy/egress mapping + Sysbox runtime class.
4. Helm chart + RBAC + doctor checks.
5. Testes: unit (fake IKubernetes), integração kind/k3s opt-in
   `[Trait("Category","K8s")]`.

## 7. Deliverables

`Infrastructure/Sandbox/KubernetesSandboxService.cs`, manifests RBAC,
`deploy/charts/openhandsnet/`, docs de sizing, testes.

## 8. Referências

- Enterprise docs upstream: k0s embedded + Replicated + Sysbox +
  sizing 16vCPU/64GB ~15 sandboxes (SPEC-32 §2 — conceitos apenas).
- OpenHands-Cloud: resource requests/limits por sandbox, autoscaling,
  node affinity (conceitos).
- kubernetes-client/csharp.

## 9. Guardrails

- Docker provider continua default; k8s é opt-in enterprise.
- Nenhuma porta de sandbox exposta sem auth/network policy.
- Helm chart não instala CRDs de terceiros automaticamente (Sysbox é
  pré-requisito do cluster, documentado).
- **Licença**: nada de portar manifests do `OpenHands-Cloud/` —
  charts próprios.

## 10. DoD

- Cluster kind+sysbox: conversa full-cycle (start → exec → workspace
  persiste em PVC → sleep/wake → delete limpa recursos).
- DinD dentro do pod funciona sem privileged.
- `doctor --preflight` detecta cluster sem sysbox.
- Helm install em cluster limpo sobe stack funcional.
- `dotnet test` verde.

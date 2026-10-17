# SPEC — 49 · Kubernetes Runtime Completion (Warm Pool + Sleep/Wake + Helm)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `kubernetes-runtime-completion` |
| Type | `Feature / Infrastructure + Deploy` |
| Stack | `.NET 10 · KubernetesClient · Helm · Sysbox` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#115](https://github.com/afonsoft/OpenHandsNET/issues/115) |
| Depends on | `37` (KubernetesSandboxEngine + chart esqueleto), `16` (sleep/wake lifecycle), `17` (warm pool + deferred-init), `35` (engine split), `36` (distributed cache) |
| Reference | `OpenHands/OpenHands-Cloud` (Polyform — conceitos apenas): warm pools, idle cleanup, runtime URL pattern; docs enterprise: k0s+Replicated (conceitos) |
| Touches | `src/OpenHandsNET.Sandboxing`, `src/OpenHandsNET.RuntimeApi`, `charts/openhandsnet/`, `docs/deployment/kubernetes.md`, CI (workflow_dispatch) |

## 1. User Story

**As a** operador enterprise do OpenHandsNET
**I want** o runtime Kubernetes com warm pool, sleep/wake e um chart
instalável de verdade
**So that** o caminho cloud/enterprise sobe num cluster real — hoje o
engine existe mas `ClaimWarmAsync` retorna null (sem warm-pool),
`PauseAsync` é not-supported (sem sleep/wake → custo), o chart só tem
5 manifests sem Ingress/postgres/limits, e nada disso foi validado num
cluster com Sysbox.

**Contexto:** SPEC-37 entregou `KubernetesSandboxEngine` (pods + PVC +
RBAC), `doctor --preflight --k8s`, e o chart esqueleto
(`api|runtime-api|valkey|rbac|namespace`). Esta SPEC fecha o gap
runtime + empacotamento: paridade de lifecycle com o engine Docker
(warm-pool, sleep/wake, timeout watcher) e chart production-grade.
Instalador KOTS/k0s embedded do upstream **não** é escopo (Polyform +
decisão de distribuição separada — ver `docs/deployment/enterprise.md`).

## 2. Scope (RF)

### RF-001 — Warm pool em k8s (`ClaimWarmAsync`)

- `WarmPoolService` ganha provider k8s: pods dormant por imagem
  (`label ohn.io/warm=true, ohn.io/image=<hash>`), `POST /api/init`
  deferred no claim (mesmo contrato do provider Docker — SPEC-17
  RF-007).
- Config `Sandbox:Kubernetes:WarmPool:Size` por imagem (default 0 =
  desligado); reconciler mantém N dormant (BackgroundService com lock
  distribuído — réplica única por vez).
- Claim: rename/retag do pod (label `ohn.io/warm` removido, `conv=`
  setado) + deferred-init + health check — ≤5s como no Docker.
- Orphan sweep k8s cobre dormant excedentes (TTL de warm pod
  configurável, default 30min).

### RF-002 — Sleep/wake (pause/resume) em k8s

- `PauseAsync`: snapshot de estado **não é possível** em pod —
  estratégia documentada: `workspace` vive no PVC (`ohn-workspace` —
  já provisionado na SPEC-37); pause = `kubectl delete pod` +
  record `Status=Sleeping` + PVC retido.
- `ResumeAsync`: recria o pod a partir da mesma spec montada sobre o
  PVC existente → wake ≤ tempo de scheduling + image pull (imagem já
  no node ~segundos).
- `SandboxTimeoutWatcher`/`SandboxSleepService` viram engine-aware
  (interface `ISandboxEngine.SupportsPause`) — k8s passa a reportar
  suporte real após RF-002.
- Lease/idle rules idênticas ao engine Docker (SPEC-16).

### RF-003 — Helm chart production-grade

Completar `charts/openhandsnet/`:

- **Ingress**: `ingress.yaml` com wildcard TLS
  (`app|auth|llm-proxy|runtime.<baseDomain>`), annotations por
  controller (nginx/traefik via values), `cert-manager` opcional.
- **API hosta o WASM** (já é o caso no container) + HPA por CPU para
  `api` (min 1 — backplane cuida do fan-out; sessão SignalR é sticky
  via `sessionAffinity` opcional).
- **Postgres**: `values.postgres.externalDsn` (produção) OU subchart
  condicional `bitnami/postgresql` para dev.
- **Runtime-api** Deployment + RBAC completo (já tem pods/exec/pvc —
  revisar vs engine atual) + `PodSecurityStandards` restricted onde
  couber + `runtimeClassName: sysbox-runc` configurável.
- **Resources**: requests/limits explícitos em todos os Deployments
  (api, runtime-api, valkey); `values.resources.*` overrides.
- **NetworkPolicy**: default-deny ingress nos pods internos
  (valkey/runtime-api) + allowlist api→deps + sandbox egress via
  policy separada (compatível com SPEC-19 quando o provider de rede
  suportar — documentar fallback Squid fora do cluster).
- **Secrets**: `envFrom secretRef` para `ConnectionString`/LLM keys —
  nada de secrets em `values.yaml` commitado.
- `helm template` + `helm lint` no CI; `docs/deployment/kubernetes.md`
  atualizado com `values.yaml` comentado.

### RF-004 — Validação em cluster real

- Runbook `docs/deployment/kubernetes.md`: kind + sysbox-runc
  (instalação documentada) → `helm install` → smoke
  (`POST /app-conversations` → pod sobe, exec roundtrip, sleep/wake,
  warm claim).
- Workflow `workflow_dispatch` manual `k8s-e2e.yaml` (novo arquivo —
  **regra: workflows novos são proposta em PR, não edição silenciosa
  de existentes**): kind + sysbox + helm + smoke. Não roda no CI de
  PR (custo) — só manual/nightly.
- `doctor --preflight --k8s` já existe — estender com check de
  `runtimeClassName sysbox-runc` presente e PVC storageClass com
  ReadWriteOnce disponível.

## 3. API Contract

Nenhum endpoint novo — comportamento dos endpoints existentes
(`/sandboxes/*`, lifecycle) passa a valer também para
`Sandbox:Engine=kubernetes`. `GET /api/v1/ready` inclui probe do
engine k8s quando ativo.

## 4. Data model

Nenhuma tabela nova — reuse `SandboxRecord` (campos `Pool`/`Status`
já existentes).

## 5. NFR / Security

- Pod de sandbox sempre `runAsNonRoot` no container de workload;
  DinD interno só via sysbox (documentado — sem `privileged:true`).
- RBAC runtime-api mínimo revisado (sem cluster-admin).
- Secrets nunca em values commitados — `secretKeyRef`/`envFrom`.
- PVC workspace retido em pause; destruído junto com a conversa
  (retention da SPEC-16).

## 6. Task Plan

1. `WarmPoolService` provider k8s (dormant pods + claim + reconcile)
   — unit com fake KubernetesClient.
2. `PauseAsync`/`ResumeAsync` via PVC — unit + interface
   `SupportsPause` nos engines.
3. Chart: ingress/values/resources/networkpolicy/postgres/HPA —
   `helm lint` + `helm template` golden no CI.
4. `doctor --preflight --k8s` + runbook kind+sysbox + workflow
   dispatch `k8s-e2e` (novo, em PR próprio).
5. Docs: `kubernetes.md` completo + `enterprise.md` — decisão
   KOTS/k0s documentada.

## 7. Deliverables

Warm pool + sleep/wake k8s, chart completo, doctor probes, runbook +
smoke validado, workflow dispatch, docs, testes.

## 8. Referências

- `.specs/37` (engine base + status das pendências — fonte primária).
- `.specs/16` (sleep/wake lifecycle), `.specs/17` (warm pool +
  deferred-init), `.specs/35` (engine split).
- `docs/reference/openhands-upstream/cloud-api.md` — runtime URL
  pattern; docs enterprise — sizing 16vCPU/64GB ~15 sandboxes.

## 9. Guardrails

- `OpenHands-Cloud` é Polyform — conceitos apenas.
- Engine k8s nunca é default — `Sandbox:Engine=docker` continua o
  caminho OSS.
- Sem `privileged:true` em workload; sysbox documentado como
  requisito DinD.
- Novo workflow = PR separado com revisão (regra hard do repo).

## 10. DoD

- `ClaimWarmAsync` retorna claim real em cluster fake/testado.
- Pause→resume mantém workspace (teste com PVC fake).
- `helm template` renderiza stack completa sem secrets em texto;
  `helm lint` verde.
- Runbook validado manualmente em kind+sysbox (evidência na PR).
- Suite verde; SPEC `Done`; docs atualizadas.

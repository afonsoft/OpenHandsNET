# SPEC — 84 · Deployment Sizing Guide (capacidade por peak sandboxes)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `deployment-sizing` |
| Type | `Documentation` |
| Stack | `docs/` (pt-BR + mirror `docs/en/`) |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #226, 4733e92)` |
| Ticket | `gap-analysis 2026-10-22` · `GAP-documentation-deployment-sizing` |
| Depends on | `14` (perf limits), `37`/`49` (k8s runtime), `35` (runtime split) |
| Reference | `docs.openhands.dev/enterprise/sizing-guide` (conceitos apenas) |
| Touches | `docs/deployment/sandbox-sizing.md` (estender) ou nova seção `deployment-sizing`; mirrors en |

## 1. User Story

**As a** operador planejando um deployment self-hosted
**I want** tabelas de sizing em nível de deployment — peak concurrent
sandboxes → VM/nodes, pools separados, PostgreSQL dedicado
**So that** eu provisiono a infra correta na primeira vez — hoje
`sandbox-sizing.md` cobre só os cgroup profiles por sandbox
(`small|medium|large`), sem nada sobre quantas máquinas/nodes o
deployment precisa.

**Contexto:** upstream sizing-guide dimensiona por *peak sandboxes*:
unidade base 0,5 vCPU + 4 GiB RAM + 10 GiB volume por sandbox; VM única
8→96 vCPU conforme 5→100 sandboxes; Helm com dois node pools (tainted
sandbox pool + platform pool); PostgreSQL em node dedicado ≥500
sandboxes; disco de dados em volume separado expansível.

## 2. Escopo

In-scope:

- Estender `docs/deployment/sandbox-sizing.md` (ou seção nova) com:
  - unidade por sandbox (mapear nossos profiles `medium` = 2 vCPU/4 GiB
    vs upstream 0,5/4) e fórmula `total = peak × por-sandbox`;
  - tabela VM única (docker compose): peak 5/15/30/50 → vCPU/RAM/disco,
    com nota "disco de workspace em volume separado expansível"
    (`/workspace` e data dir);
  - tabela k8s (Helm): sandbox node pool (taint/toleration) vs platform
    pool, escala por peak; `kubectl` requests/limits já cobertos por
    `sandbox profiles` + `values.yaml`;
  - nota PostgreSQL: dedicado/externo acima de N sandboxes, apontando
    `external-postgres.md`;
  - regra "size pelo peak observado + headroom" e métricas a observar
    (`openhandsnet_sandbox_*`, `docker stats`, `kubectl top`).
- Mirror `docs/en/deployment/sandbox-sizing.md`.

Out-of-scope: autoscaling automático, Karpenter/HPA de sandbox (não
implementado), alterações de chart.

## 3. Requisitos

1. Tabelas derivadas de premissas declaradas (números reproduzíveis a
   partir de `peak × profile`).
2. Marcar estimativa de "usuários" como aproximação — sizing é por
   sandboxes, não headcount (paridade upstream).
3. Cross-link: `kubernetes.md`, `external-postgres.md`, `dind.md`,
   `custom-images.md`.

## 4. Task Plan

1. Estender doc pt-BR. 2. Mirror en. 3. Revisar índice se nova página.

## 5. Deliverables

- Sizing guide de deployment publicado nos docs.

## 6. Referências

- Upstream: `docs.openhands.dev/enterprise/sizing-guide` (conceitos).
- Repo: `docs/deployment/sandbox-sizing.md`, `charts/openhandsnet/values.yaml`,
  `DockerSandboxEngine.cs` (profiles), `KubernetesSandboxEngine.cs`.

## 7. Guardrails

- Números declarados como *recomendações*, não requisitos; sem inventar
  benchmarks não medidos — usar fórmula e premissas.
- pt-BR fonte; mirror en fiel.

## 8. DoD

- Tabelas por peak-sandbox presentes p/ VM única e Helm; fórmula
  declarada; links internos ok; mirror en presente.

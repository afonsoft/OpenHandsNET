# SPEC — 83 · Troubleshooting Runbook (ops doc consolidada)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `troubleshooting-runbook` |
| Type | `Documentation` |
| Stack | `docs/` (pt-BR + mirror `docs/en/`) |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #226, 4733e92)` |
| Ticket | `gap-analysis 2026-10-22` · `GAP-documentation-troubleshooting-runbook` |
| Depends on | `14` (support-bundle + doctor), `35`/`37` (runtime split/k8s) — já entregues |
| Reference | `docs.openhands.dev/enterprise/troubleshooting` (conceitos apenas) |
| Touches | `docs/deployment/troubleshooting.md` (novo), `docs/en/deployment/troubleshooting.md`, `docs/README.md`, `docs/en/README.md` |

## 1. User Story

**As a** operador do OpenHandsNET self-hosted
**I want** um runbook consolidado de troubleshooting — componente→sintoma,
workflow de inspeção (compose e k8s), support bundle, checks de primeira
conversa
**So that** eu diagnostico falhas de deployment sem conhecer a
arquitetura interna — hoje as peças existem (`SupportBundleService`,
`doctor --preflight`, readiness real) mas estão espalhadas entre
`cli.md`, `ui/admin.md` e `security.md`, sem doc operacional.

**Contexto:** upstream `enterprise/troubleshooting` tem página dedicada:
support bundle primeiro, workflow read-only de `kubectl`, tabela
component→pod→sintoma, log level temporário e checks de primeira
conversa (runtime-api 401 keys, modelo LiteLLM errado). Equivalente no
nosso mundo: compose (`docker logs`/`docker compose ps`) e k8s
(`kubectl -n openhandsnet`, `-n openhandsnet-sandboxes`).

## 2. Escopo

In-scope:

- `docs/deployment/troubleshooting.md` (pt-BR) + mirror en.
- Entradas no índice `docs/README.md` + `docs/en/README.md`.

Out-of-scope: novos endpoints, novos checks no `doctor`, log-level
dinâmico (já configurável via `Serilog`/`Logging` config), alertas.

## 3. Conteúdo obrigatório

| Seção | Conteúdo |
|---|---|
| Support bundle primeiro | `GET /api/v1/support-bundle` (admin UI `/admin`) — ZIP com readiness, versões, entity counts, config mascarada (redaction testada em `SupportBundleRedactionTests`), tail de `logs/openhandsnet-*.log`. Aviso: revisar antes de anexar a ticket. |
| Workflow read-only | compose: `docker compose ps`, `docker compose logs app`, `docker ps --filter label=openhandsnet.*`; k8s: `kubectl get pods -n openhandsnet -o wide`, `-n openhandsnet-sandboxes`, `events --sort-by`, `logs --previous`, `describe pod`. Regra: inspeção read-only, correções via config/values — nunca `kubectl edit` ad hoc. |
| Tabela componente→sintoma | `app` (UI/API/conversas), `runtime-api` (sandbox create/pause/cleanup — provider `remote`), pods `ohn-sandbox-*`/`ohn-pool-*` (conversa específica), `litellm` (modelo/auth BYOR), `keycloak` (login/SSO), `postgres`, `valkey` (backplane multi-réplica). |
| Probes | `/api/v1/health` (liveness), `/api/v1/readiness` (deps reais: db SELECT 1, docker, sandbox provider), `openhandsnet doctor` e `doctor --preflight`/`--k8s`. |
| Checks de primeira conversa | "Failed to start sandbox" → logs do provider (docker daemon acessível? `runtime-api` up? `X-Runtime-Key` igual dos dois lados — comparar sem imprimir); modelo inválido → BYOR strict rejeita fora do `GET /models` do proxy (`byor_model_rejected`); warm pool não clama → spec default vs custom. |
| Debug logging temporário | `Logging:LogLevel:Default=Debug` ou `Serilog` override via env; reproduzir; voltar a `Information`. |
| Egress/DinD | sintomas de `EgressMode=deny` (clone/LLM quebram), DinD sidecar privileged vs sysbox (`runtimeClassName`). |

## 4. Task Plan

1. Escrever `docs/deployment/troubleshooting.md`.
2. Mirror `docs/en/deployment/troubleshooting.md`.
3. Indexar em `docs/README.md` (seção Deployment) e `docs/en/README.md`.

## 5. Deliverables

- 1 doc pt-BR + 1 mirror en + 2 entradas de índice.

## 6. Referências

- Upstream: `docs.openhands.dev/enterprise/troubleshooting` (conceitos).
- Código: `SupportBundleService.cs`, `McpClientService.cs`, endpoints
  `/api/v1/health|readiness|support-bundle`, `docs/cli.md`,
  `docs/deployment/{dind,security,kubernetes}.md`.

## 7. Guardrails

- Doc-only; nada de secrets, IPs ou dados reais — exemplos com
  placeholders; comandos read-only.
- pt-BR como fonte; mirror en fiel.
- Não prometer features inexistentes (ex.: `Send bundle to vendor`).

## 8. DoD

- Doc cobre todas as seções da tabela; índice atualizado; links internos
  válidos; mirror en presente.

# SPEC — 153 · Hybrid Runner / Outpost (execução na rede do cliente)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `hybrid-runner` |
| Type | `Feature / Infra + Security (enterprise)` |
| Stack | `.NET 10 · agent runner binary · outbound relay · sandbox remoto` |
| Repository | `afonsoft/sandcastle` |
| Status | `Draft` |
| Ticket | `GAP-HYBRID-RUNNER` (docs/research/2026-11-competitive-gap-analysis.md §P2/E) |
| Depends on | `85` (k8s/sysbox), `86` (runtime image), `57` (service users), `149` (router residency) |
| Reference | Devin Outposts (sandbox dentro da rede do cliente); OH Enterprise TLS/egress policies — conceitos apenas |
| Touches | `src/Sandcastle.{Api,Infrastructure}`, novo `src/Sandcastle.Outpost/`, `tests/` |

## 1. User Story

**As a** cliente enterprise com código que não pode sair da minha rede
**I want** instalar um `sandcastle-outpost` (runner agent) dentro do
meu perímetro que executa os sandboxes localmente — a control plane
orquestra, mas código e secrets nunca saem
**So that** paridade com os Outposts do Devin e o modelo split
cloud/self-hosted do OpenHands Enterprise — hoje todo sandbox roda
onde a API roda; não existe execução remota.

## 2. Scope (RF)

### RF-001 — Outpost agent

- Novo binário `Sandcastle.Outpost` (worker .NET, imagem docker e
  binário standalone): conecta **outbound-only** à control plane via
  WebSocket/mTLS (`POST /api/v1/outposts/connect` → upgrade) —
  nenhuma porta inbound; token `opk-*` (outpost key, hash SHA-256,
  escopo org, `perm:outposts.manage`).
- Heartbeat + capacidade reportada (`max_sandboxes`, labels:
  `gpu`, `windows`, `region`, `docker-version`); reconnect com
  backoff; drain mode (`outpost drain` graceful).
- Executa `sandbox_task` frames recebidos: provisiona sandbox local
  (Docker/Podman no host do outpost — reusa a runtime image
  `sandcastle-runtime`), executa execs, devolve streams; o agente
  LLM continua rodando na control plane (o outpost é executor de
  sandbox, não de LLM — alinhado com o modelo Devin).

### RF-002 — Control plane

- `outposts` entity `{id, org, name, labels_json, status
  (online|draining|offline), version, last_heartbeat, created_by}` +
  `conversations.outpost_id` (route pinning opcional por
  conversa/automation/schedule).
- `ISandboxService` — nova implementação `OutpostSandboxService`
  que delega o `SandboxContract` ao outpost via o canal (mesmo
  contrato do Docker local — exec/read/write/commit).
- Roteamento: `Outpost:DefaultPolicy` (`cloud|prefer-outpost|
  outpost-only`) por org/conexão; data-residency orgs `internal-only`
  exigem outpost (ver SPEC-149 router residency — alinhar).

### RF-003 — Segurança (o ponto central)

- mTLS: outpost recebe cert client no enrollment (`POST
  /outposts/enroll` com one-time token → emite par de chaves +
  cert fingerprint registrado); control plane valida fingerprint.
- **Nenhum secret cruza**: secrets são resolvidos no lado do
  outpost (env local do outpost + `SecretNamesJson` — a control
  plane nunca envia valores; o outpost pode ter seu próprio vault
  file/ENV `OUTPOST_SECRETS_FILE`).
- Código nunca deixa o perímetro: diffs/patches retornam ao usuário
  via PR no provider interno (GitHub Enterprise on-prem etc.) ou
  via `handoff-package` (SPEC-150); logs do exec: streaming cifrado
  no canal, persistência no lado da plane apenas metadata
  (exit codes/durations) — `Outpost:LogRedaction=aggressive`
  (secret-scan antes de qualquer byte sair).

### RF-004 — Admin + operação

- Settings > Outposts: enroll flow (gera comando `docker run
  sandcastle-outpost --enroll-token …`), lista (status/labels/último
  hb), deregistrar, drain, logs de auditoria (connects, tasks).
- `sandcastle-outpost doctor` CLI (checks: docker socket, egress à
  plane, cert válido, runtime image presente).
- Permissões `outposts.manage` (admin), `outposts.view` (member);
  audit `outpost.*`.

### RF-005 — Fora de escopo

- Execução de LLM no outpost (full air-gapped — exige modelo local
  roteável; o router SPEC-149 prepara o caminho, fase 2) ·
  outpost clusters com HA/scheduling avançado · GPU scheduling ·
  Windows outpost · k8s operator — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/outposts/enroll` | `{org, name, labels}` → enroll token |
| GET | `/api/v1/outposts` · `/{id}` | lista/detalle |
| POST | `/api/v1/outposts/{id}/drain` · `/deregister` | |
| GET/POST | `/api/v1/outposts/connect` | WS upgrade (mTLS) |
| PATCH | `/api/v1/conversations/{id}` | `{outpost_id}` pinning |

## 4. Data model

- `outposts` + `conversations.outpost_id` + `outpost_audit` —
  migrations duplas.

## 5. NFR / Security

- Outbound-only é hard rule: nenhum listener TCP no outpost.
- Frames do canal são assinados (`HMAC` com a chave da sessão de
  enroll); `SandboxContract` payloads seriam-limpos (command
  allowlist por operação do contrato).
- Compromise do outpost ≠ compromise do código de outras orgs:
  tokens escopo-org, revogáveis (`deregister` invalida cert).
- mTLS required quando `Outpost:RequireMtls=true` (default prod).

## 6. Task Plan

1. `outposts` entity + enroll flow + `opk-*` keys + audit.
2. Canal WS+mTLS + heartbeat + frame protocol `sandbox_task_v1`.
3. `OutpostSandboxService` (delegação do contrato) + routing
   policy + pinning.
4. `Sandcastle.Outpost` worker + doctor + drain + secrets-locais.
5. UI admin + i18n.
6. Testes: unit (enroll, frames, routing, residency), integração
   (outpost real docker local executando task), golden.

## 7. Deliverables

Outpost binário que executa sandboxes dentro do perímetro do
cliente com canal outbound-only — a peça que abre o mercado
"self-hosted execution" de verdade.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.3/§E.
- `docs/specs/85_k8s_sysbox_spec.md`, `86_runtime_image_spec.md`,
  `57_service_users_spec.md`, `149_model_router_spec.md` (residency).

## 9. Guardrails

- Nenhuma porta inbound; nenhum valor de secret cruza o canal.
- Outpost revogado → tasks encerram (`kill` frame) e não reconecta.
- Não quebrar `ISandboxService` atual — outpost é implementação
  adicional, não substituição.

## 10. DoD

- Enroll → outpost docker local online → conversa pinada roda
  exec no outpost (integração) → logs/metadata na plane.
- Offline → status reflete; drain → tasks finalizam antes de morrer.
- mTLS obrigatório validado; secrets locais usados.
- Suite verde; migrations duplas; golden; i18n ×8.

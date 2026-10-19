# SPEC — 32 · Upstream Gap Analysis (Enterprise / Cloud / Agent-SDK)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `upstream-gap-analysis` |
| Type | `Analysis / Backlog (research spec)` |
| Stack | `survey of docs.openhands.dev + deepwiki (OpenHands/*)` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` — research/analysis spec (deliverable = análise + roadmap) |
| Ticket | `TBD` |
| Depends on | `27_cli_opencode_comparative_spec` (mesma família de análise) |
| Touches | roadmap — nenhum código nesta SPEC |

## 1. User Story

**As a** maintainer do OpenHandsNET
**I want** um gap analysis atualizado contra os repos oficiais
(`OpenHands`, `open-operator`, `enterprise`, `OpenHands-Cloud`, `docs`,
`software-agent-sdk`) e a doc enterprise de instalação
**So that** o backlog de hardening/feature reflita o estado real do upstream
— implementando conceitos em C# (licenças: `enterprise/` e
`OpenHands-Cloud/` são Polyform — **conceitos apenas, nunca código**).

## 2. Fontes analisadas

| Fonte | Método | Achados-chave |
|---|---|---|
| `docs.openhands.dev/enterprise/quick-start` | webfetch | installer = **k0s embedded k8s** via Replicated; dirs do sistema (`/etc/k0s`, `/var/lib/embedded-cluster`, kubelet, containerd, calico, CNI); admin console :30000; wildcard DNS ou SANs (`admin|app|auth|analytics|llm-proxy|runtime-api|runtime.<dom>`); sandbox routing subdomain|path-based; Sysbox p/ DinD; sizing 16vCPU/64GB ~15 sandboxes |
| `OpenHands/OpenHands` | deepwiki | Agent Canvas (React) + Event Store Zustand; **streaming delta events** coalescidos — durable supersedes slot; SecurityAnalyzer (LLM/pattern/policy, DefenseClaw hook) gating tool calls; secrets via **LookupSecret** (URL de referência, resolvido no spawn); memory `.openhands/memory/` (`agent_context.load_memory`); condenser configurável; dual websocket (main + planning) |
| `OpenHands/OpenHands-Cloud` | deepwiki | `runtime-api` dedicado (provisioning, warm pools, idle cleanup); Keycloak IdP; LiteLLM proxy; **filestore S3/GCS/MinIO** p/ artefatos; PostgreSQL + Valkey/Redis; Helm + Replicated KOTS; runtime URL pattern configurável; resource requests/limits por sandbox; autoscaling + node affinity |
| `OpenHands/software-agent-sdk` | deepwiki | `AgentBase.step()` stateless; `AgentContext` pydantic (system+skills); `IWorkspace` abstraction (local/docker/k8s); **resume: agent type deve ser igual e tools só aditivos**; `condense()` forçável; security_analyzer default-on; agent-server REST multi-tenant |
| `OpenHands/enterprise` | não indexado (privado/Polyform) | coberto via docs + OpenHands-Cloud charts |
| `OpenHands/open-operator` | README | **não é produto** — coleção de recursos de computer-use: benchmarks WebArena/OSWorld/ClawBench, capabilities catalog (dev, data, automation, web, sysops) |
| `OpenHands/docs` | README | Mintlify doc-site agregando `openhands/`, `sdk/`, `enterprise/`, `openapi/` — fonte das nossas refs |

## 3. Gap analysis — onde estamos vs upstream

| Área | Upstream | OpenHandsNET hoje | Gap / ação |
|---|---|---|---|
| Orquestração do loop | `AgentBase.step()` stateless + AgentServer REST | `AgentRunner` + `IAgentRunnerHost` in-process | ✅ paridade funcional; worker-based já |
| Modelo de eventos | `kind`+ULID+`parent_id` + **delta events** | V1 completo; **sem delta/streaming coalescing** | SPEC-32 RF-01 (deltas) |
| Sandbox runtime | Docker (OSS) / **k8s+Sysbox** (cloud/ent) | Docker.DotNet single-host + warm pool + sleep/wake | ✅ OSS parity; RF-02 runtime-api split (deferred → SPEC-35) |
| Sandbox agent | agent-server REST in-container | sandbox-agent v2 `/exec/stream`+`/activity`+`/proc` | ✅ à frente (activity ring) |
| Roteamento LLM | LiteLLM proxy | OmniRoute catalog + roles (SPEC-23) | ✅ paridade |
| Contexto | `condense()` + `LLMSummarizingCondenser` + `agent_context` | SPEC-22 meter+condenser+ledger+triggers | ✅ paridade (+ledger extra) |
| Tools | FileEditor/Terminal/TaskTracker/Browser + MCP | Idem + search/web_fetch/edit-modes (SPEC-25) | ✅ à frente (hashline/anchor) |
| Security gate | `SecurityAnalyzer` (LLM/pattern/policy) — risk score por ação | `Risky` flag + confirmation mode + SecretRedactor | **RF-03 analyzer plugável** |
| Secrets | LookupSecret by-URL (valor nunca no event stream) | masked storage + SecretRedactor | **RF-04 by-reference secrets** |
| Memória | `.openhands/memory/` files, `load_memory` | — (SPEC-27 RF-C propõe org+project) | alinhado com SPEC-30 |
| Subagentes | task tool + presets (fork alltomatos) | — | alinhado com SPEC-28 |
| Permissões | `allow/deny/ask` por tool/agent | confirmation on/off | alinhado com SPEC-29 |
| Artefatos | filestore S3/GCS/MinIO | workspace `/tmp/ohn-artifacts` (morre c/ sandbox) | **RF-05 artifact store externo** |
| Cache/pubsub distribuído | Valkey/Redis | IMemoryCache in-process | RF-06 (multi-replica, deferred → SPEC-36) |
| Deploy enterprise | k0s+Replicated KOTS+Helm | docker compose + doctor/support-bundle | RF-07 k8s path (deferred → SPEC-37) |
| Sandbox URL routing | subdomain \| path-based por config | path interno fixo | RF-08 routing mode config (deferred → SPEC-38) |
| Preflight | script DNS+egress+ports | `doctor` parcial | **RF-09 doctor extendido** |
| Resume | agent type igual + tools só aditivos | replay livre | **RF-10 resume validation** |
| Bench/eval | WebArena/OSWorld (open-operator) | — | RF-11 eval harness (deferred → SPEC-39) |

## 4. RFs propostos (novos — os já mapeados p/ 28–31 ficam de fora)

### RF-01 — Streaming delta events (prioridade 2)

Upstream separa **deltas** (token chunks, coalesced) de **durable events**
(mesmo `item_id` → durable substitui o slot). Hoje nosso `TokenEvent`
persiste cada chunk — custo de storage e replay.

- Novo kind `streaming_delta` **efêmero** (SignalR-only, nunca persistido):
  `{item_id, seq, text}`; o evento durável (`ActionEvent`/`MessageEvent`)
  carrega `item_id` correspondente.
- Hub SignalR: batcher server-side (16ms/64B) → client agrega no slot;
  replay ignora deltas (só duráveis).
- Ganho: event store para de guardar milhares de tokens/turno; UI ganha
  streaming suave já no SPEC-26.

### RF-02 — `runtime-api` split (deferred — enterprise) → **SPEC-35**

Extrair `DockerSandboxService` p/ serviço `openhandsnet-runtime-api`
(REST interno) quando multi-host for requisito; manter in-process como
default. Pré-req: contrato `ISandboxOrchestrator` já limpo hoje.

### RF-03 — `ISecurityAnalyzer` plugável (prioridade 1)

Pipeline antes de executar tool `Risky=true`:

```csharp
interface ISecurityAnalyzer {
    Task<SecurityRisk> AnalyzeAsync(ToolCallContext ctx,
        string toolName, JsonElement args, CancellationToken ct);
} // enum SecurityRisk { Low, Medium, High }
```

- `PatternSecurityAnalyzer` (default): regex destrutivos (`rm -rf /`,
  `> /dev/sda`, fork bombs, `curl | sh` fora de allowlist, write fora do
  workspace, `web_fetch` para host privado já cobre SSRF) → `High`.
- `LlmSecurityAnalyzer` (opt-in): role `summarizer` da SPEC-23 julga o
  args → `High`/`Low`, cache por hash do args.
- Integração: `High` → `ConfirmationRequired` forçado (independe de
  `permissions`); evento `agent_diagnostic` `code="security_flag"`.
- Tabela `security_policies` por org (SPEC-08) com presets
  `permissive|balanced|strict` — strict também exige `ask` em
  `mcp__*`.

### RF-04 — Secrets by-reference (prioridade 2)

Hoje secrets chegam como env vars resolvidas no start. Upstream injeta
`LookupSecret` (URL `/api/settings/secrets/{name}`) resolvida no spawn —
o **valor nunca trafega no request do start-task**.

- `ConversationStartWorker` passa a resolver nomes→valores na borda do
  sandbox (nunca no payload do start-task, nem no evento persistido).
- `GET /settings/secrets` já existe? — expor `secrets_mask` list +
  resolução interna apenas (nunca GET do valor).
- Redator já cobre output; adicionar redação também no `command_output`
  stream (SPEC-24 já redige — verificar cobertura de env-injection).

### RF-05 — Artifact store externo (prioridade 3)

`artifact_ref` (SPEC-25) hoje grava no workspace — morre com o sandbox.

- `IArtifactStore` (`workspace` default; `s3` provider via
  `AWSSDK.S3`/MinIO compat): `PutAsync(stream) → artifact_id`,
  `GET /api/v1/artifacts/{id}` no Api (auth = dono da conversa).
- OutputPolicy passa a escrever no store; observation mantém
  `artifact_ref` opaco (workspace path OU artifact id).
- Config `Artifacts:Provider`, `Artifacts:Bucket`, TTL por lifecycle
  da conversa (delete junto).

### RF-06 — Distributed cache/pubsub (deferred) → **SPEC-36**

Valkey/Redis via `IDistributedCache` + `StackExchange.Redis` para:
warm-pool state, rate-limits multi-replica, event fan-out SignalR
backplane. Só quando `Deployment:Replicas > 1` — doc flag.

### RF-07 — Kubernetes runtime (deferred — enterprise) → **SPEC-37**

Paridade enterprise real exige k8s runtime provider
(`KubernetesSandboxService` com pods + Sysbox p/ DinD + PVC workspace).
Escopo grande; documentar como roadmap — Helm chart `charts/openhandsnet/`
seria entregável próprio.

### RF-08 — Sandbox URL routing mode (deferred) → **SPEC-38**

`Sandbox:RoutingMode = path|host` — hoje o agente é acessado internamente
(port mapping); path já basta. Só importa se sandbox-agent v2 virar
user-facing (terminal do browser via URL do sandbox).

### RF-09 — Preflight no `doctor` (prioridade 2)

Upstream roda script de DNS+egress+portas antes do install. Estender
`openhandsnet doctor` e `/readiness`:

- DNS do `BaseDomain` (quando `Deployment:PublicDomain` set) resolve;
- egress: reachability de `registry-1.docker.io`, `ghcr.io`,
  `api.anthropic.com` etc conforme providers ativos;
- Docker socket, cgroup v2, espaço em disco mínimo (já temos?),
  portas 8080/30000 livres;
- saída `doctor --preflight` non-zero em falha → CI/install gate.

### RF-10 — Resume validation (prioridade 1 — cheap)

Upstream exige no resume: **mesmo agent type + toolset só aditivo**.
Regra análoga: ao resumir conversa, persistimos `agent_contract_fingerprint`
(hash de: tool names + agent version) num `ConversationStateUpdateEvent`
no início do run; resume com fingerprint divergente que **removeu** tools
→ warning event `agent_diagnostic code="resume_drift"` + modo
fail-open (log) por ora, fail-closed depois.

### RF-11 — Eval harness (deferred) → **SPEC-39**

`openhandsnet eval` — runner headless de tasks com fixtures
(estilo WebArena) para regression de prompts/tools. Alinha com CI
nightly; detalhar na SPEC própria quando priorizado.

## 5. Cruzamento com SPEC-27

| SPEC-27 RF | Reforço upstream |
|---|---|
| RF-A subagents | upstream sdk tem `IWorkspace`/`AgentBase` — nosso `SubagentRunner` espelha `AgentRunner` ✓ |
| RF-B permissions | upstream `SecurityAnalyzer` complementa — RF-03 aqui cobre risk-score, RF-B cobre allow/deny |
| RF-C memory | upstream `.openhands/memory/` confirma o formato de arquivos markdown no workspace — SPEC-30 adota o mesmo path |
| RF-G cli parity | `openhandsnet eval`/`doctor --preflight` entram na lista (RF-09/11) |

Ordem sugerida atualizada: `28 (subagents) → 29 (permissions) →
30 (memory) → 31 (cli parity) → 33 (security analyzer) → 34 (artifacts+
preflight) → …` — 32 é análise, não implementa.

## 6. Guardrails

- **Licença**: `enterprise/` e `OpenHands-Cloud/` = Polyform — conceitos
  apenas; todo código C# próprio.
- Streaming delta nunca persiste — só durable events no store (V1 rule).
- Security analyzer nunca bloqueia `ask` legítimo: `High` → confirmation,
  não abort silencioso.
- By-reference secrets: valor nunca no payload do start-task nem em
  eventos.

## 7. DoD

- Doc com tabela de gaps fonte↔OpenHandsNET + RF numerados.
- README indexado; cross-refs com SPECs 27–31 consistentes.
- Nenhuma implementação — backlog apenas.

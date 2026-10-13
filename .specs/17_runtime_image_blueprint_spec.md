# SPEC — 17 · Runtime Image (Ubuntu) + Blueprint Provisioning + Sandbox Agent

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `runtime-image-blueprint` |
| Type | `Feature / Infra` |
| Stack | `.NET 10 · Docker.DotNet · Dockerfile (ubuntu:24.04) · Minimal API in-container` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#37](https://github.com/afonsoft/OpenHandsNET/issues/37) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox_spec`, `12_git_scm_integration_spec`, `14_enterprise_operations_spec`, `16_persistent_sandbox_lifecycle` |
| Touches | `runtime/` (novo), `src/OpenHandsNET.SandboxAgent` (novo), `DockerSandboxService`, `DockerSandboxExecutor`, `ConversationStartWorker`, `WorkspaceProvisioner`, `WarmPoolService` |

## 1. User Story

**As a** operador do OpenHandsNET
**I want** que cada conversa suba um container da nossa imagem Ubuntu — com o
agente de sandbox instalado como PID 1, o repo clonado, as dependências e os
SDKs certos provisionados pelo blueprint do projeto
**So that** o ambiente seja reproduzível, fiel à arquitetura upstream
(host → docker → agent-server in-container), e o agente comece a trabalhar
num workspace já pronto em vez de gastar turnos instalando ferramenta.

**Problem context:**

- Hoje o sandbox é `nikolaik/python-nodejs` com `Cmd=["sleep","infinity"]` —
  nenhum processo nosso roda dentro do container; todo comando sai por
  `docker exec` do host. Não há paridade com o **agent-server in-container**
  do OpenHands (upstream serve `/api/*` na :8000 e o backend fala HTTP com ele).
- Não existe etapa de **provisionamento por repo**: clone acontece, mas
  `dotnet restore`/`npm install`/SDK correto ficam por conta do agente —
  desperdiça turnos e falha sem o SDK certo instalado.
- Não há imagem própria: ferramentas que o agente precisa (`git`, `gh`,
  SDKs) dependem da imagem de terceiro.
- Warm pool só pré-aquece a imagem default — upstream mantém **um pool por
  imagem** (`custom-sandbox-images`).

## 2. Scope

**In scope:**

- **RF-001 — Imagem runtime `openhandsnet/runtime`**: `runtime/Dockerfile`
  sobre `ubuntu:24.04` com usuário não-root `openhands` (UID 10001),
  `git`, `gh`, `curl`, `jq`, `python3`, `node` LTS, `tini`, e o binário
  `openhandsnet-sandbox-agent` em `/usr/local/bin`. Publicada como
  `ghcr.io/openhandsnet/runtime:{version}` (+`latest`) — build manual via
  `docker buildx` documentado; workflow de CI para publicar = mudança em
  `.github/workflows/` **exige revisão humana** (hard rule do repo).
  `Sandbox:DefaultImage` passa a apontar para ela.
- **RF-002 — `openhandsnet-sandbox-agent` (PID 1)**: projeto novo
  `src/OpenHandsNET.SandboxAgent` — ASP.NET Core **Native AOT** self-contained
  single-file (ou framework-dependent + runtime no layer da imagem). Serve HTTP
  na porta **8000** e mantém o container vivo (substitui `sleep infinity`):
  - `GET /alive` → `200 {status:"ok"}` (health check do host);
  - `POST /exec` `{command, timeout_seconds}` → `{exit_code, stdout, stderr, timed_out}`;
  - `GET|PUT /files/{**path}` — leitura/escrita dentro de `/workspace` apenas;
  - `GET /files` listagem;
  - `GET|POST /api/init` — **deferred-init** upstream: container sobe
    `dormant` (só `/alive`+`/api/init` respondem; `/exec`/`/files` → 503) até o
    host ativar com `session_api_key` + workspace path + env (SPEC-17 §4.2).
  - Auth: `X-Session-Key` header — chave gerada por sandbox no host, injetada
    por env `OHN_SESSION_KEY`, nunca logada.
- **RF-003 — Transporte do executor**: `ISandboxExecutor` ganha impl HTTP
  `SandboxAgentExecutor` (alcança o agente via IP do container na bridge, ou
  porta publicada `127.0.0.1:{port}` quando `Sandbox:PublishAgentPort=true` —
  Docker Desktop/remote daemon). Probe `/alive` decide: agente OK → HTTP;
  imagem antiga sem agente → fallback `docker exec` (compat total).
- **RF-004 — Blueprint resolution** (`WorkspaceProvisioner` +
  `ConversationStartWorker`): após clone e antes do READY:
  1. `openhandsnet.yaml` na raiz do repo (campos §4.3) — opcional;
  2. `.openhands/setup.sh` executável — convenção upstream, roda como
     `openhands` em `/workspace/repo` com `DEBIAN_FRONTEND=noninteractive`;
  3. sem arquivo → **auto-detect SDK** por manifestos e instala conforme §4.4;
  4. `env` do blueprint → `spec.Env` efetivo do sandbox **no próximo start**;
     para o sandbox atual, exporta em `/etc/profile.d/ohn-blueprint.sh` +
     injeta nas chamadas do executor.
  Precedência de imagem: `openhandsnet.yaml > org blueprint > Sandbox:DefaultImage`.
  Falha de blueprint ≠ falha de conversa — registra `BlueprintFailed` no
  record, emite `SystemPromptEvent`/observation visível, segue.
- **RF-005 — SDK installers**: `runtime/sdks/{dotnet,node,python,java,go}.sh`
  version-pinados (dotnet-install script, NodeSource, apt, sdkman, tarball
  oficial). `sdk: [dotnet:10, node:22]` no YAML → executa o installer
  correspondente no boot.
- **RF-006 — Warm pool por imagem**: `WarmPoolService` passa a manter
  `Sandbox:WarmPoolSize` por **pool_key = image** (`SandboxRecord.PoolKey`);
  claim só para specs cuja imagem tem pool configurado
  (`Sandbox:WarmPoolImages` map `image→count`; default `{default: N}`).
- **RF-007 — Dormant warm pool**: containers warm nascem **dormant**
  (`OHN_DEFERRED_INIT=true`); claim → `POST /api/init` com session key +
  workspace → `ready`. Paridade com o `deferred-init` upstream.

**Out of scope:** agente/LLM loop dentro do container (o loop ReAct fica no
host — divergência intencional documentada em §5; upstream roda o agent-server
com o agente inteiro dentro), VS Code server in-container, worker ports,
migração de streaming de eventos para o container, Windows/macOS da imagem
agente.

## 3. Technical Context

**Estado atual (evidência):**

- `DockerSandboxService.cs:117` — `Cmd = ["sleep","infinity"]`; zero processo
  nosso in-container.
- `DockerSandboxExecutor` — `docker exec` para tudo (bash one-shot, sessão
  persistente via marker, read/write/list files, git diff).
- `ISandboxExecutor` já é a fronteira certa — trocar transporte sem tocar tools.
- `WorkspaceProvisioner` — `PrepareAsync`/`PreparePath` + `CloneRepositoryAsync`
  (host mode com `GIT_CONFIG_*` http.extraHeader, SPEC-15).
- `ConversationStartWorker` — ordem atual: start sandbox → provision gh auth →
  PREPARING_REPOSITORY (clone) → SETTING_UP_SKILLS → READY. Blueprint entra
  entre clone e skills (novo estado visual `SETTING_UP_ENVIRONMENT`).
- `WarmPoolService` — pool único da imagem default; claim faz rename do
  workspace dir pré-montado.
- `spec.Env` + `ResolveCredentialEnvAsync` — pipeline de env já existe; GH_TOKEN
  já injetado (SPEC-15 RF-004).

**Upstream (referências §9):** `ghcr.io/openhands/agent-server` =
`python:3.13`+`node:24`+`git`+`vscode`+browser+`docker-cli`, entrypoint
`tini -- openhands-agent-server`, portas 8000/8001/8011/8012; custom images
nunca sobrescrevem o entrypoint; warm pool por imagem; deferred-init com
`POST /api/init` + `X-Init-API-Key` (dormant→initializing→ready); URL reach via
`container_url_pattern` `{port}` ou subdomínio.

## 4. Design

### 4.1 Topologia

```
host Api ──docker create──► container [tini → openhandsnet-sandbox-agent :8000]
   │                              ▲ dormant: /alive + /api/init only
   ├── docker exec (fallback)     │
   └── HTTP http://{ctr-ip}:8000 ─┘  X-Session-Key
        /api/init → ready → /exec /files
```

### 4.2 Deferred init (paridade upstream)

```
dormant ─POST /api/init (X-Session-Key bootstrap)─► initializing ─► ready
   ▲                                                   │ error
   └───────────────────────────────────────────────────┘
```
`InitRequest`: `session_key`, `workspace`, `env`, `repos` — campos opcionais
só sobrescrevem o default. Warm pool nasce dormant; cold start pode passar
`OHN_DEFERRED_INIT=false` e já subir ready com a session key do env.

### 4.3 `openhandsnet.yaml` (raiz do repo, tudo opcional)

```yaml
image: ghcr.io/openhandsnet/runtime:latest   # override da imagem
sdk: [ "dotnet:10", "node:22", "python:3.13" ]
env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
setup: .openhands/setup.sh                    # default; path relativo ao repo
resources: { profile: large }                 # hint de sizing (SPEC-14)
```

### 4.4 SDK auto-detect (sem YAML)

| Manifesto no repo | SDK |
|---|---|
| `*.csproj`/`*.slnx`/`global.json` | `dotnet` (versão do `global.json` ou LTS) |
| `package.json`/`package-lock.json` | `node` (`.nvmrc`/engines ou LTS) |
| `requirements*.txt`/`pyproject.toml` | `python` (`pyproject.requires-python` ou 3.13) |
| `pom.xml`/`build.gradle*` | `java` (21) |
| `go.mod` | `go` (`go` directive ou latest) |

### 4.5 Boot sequence (worker)

```
STARTING → container up (dormant) → /api/init → ready
→ PREPARING_REPOSITORY (clone) → SETTING_UP_ENVIRONMENT
  (read openhandsnet.yaml → sdk installers → .openhands/setup.sh)
→ gh auth (existente) → SETTING_UP_SKILLS → READY
```

## 5. API Contract

- Sandbox record: `+ pool_key`, `+ blueprint_status` (`ok|failed|none`),
  `+ blueprint_log_tail` (últimos 4 KB) — expostos no `SandboxDto`.
- `GET /api/v1/sandboxes/{id}/blueprint-log` → log completo do provisionamento.
- Divergência documentada: agent-server upstream roda o **loop do agente**
  in-container; o nosso `sandbox-agent` é um fino executor HTTP — LLM/estado
  ficam no host (melhor p/ chaves e observabilidade centralizadas).

## 6. NFR

- `openhandsnet-sandbox-agent` < 15 MB (AOT single-file, linux-x64+arm64).
- `setup.sh` timeout `Sandbox:BlueprintTimeoutMinutes` (default 15); stdout/stderr
  capturados no blueprint log (secrets redacted via `SecretRedactor`).
- Blueprint roda como `openhands` (não-root); installers que precisam de root
  sobem via `sudo` passwordless restrito a apt/installers conhecidos.
- Session key: ULID por sandbox; rotacionada em resume de restart do host.
- Imagem: SBOM + tag pinada; nunca `latest` em produção.

## 7. Task Plan

1. `src/OpenHandsNET.SandboxAgent` — Minimal API AOT: /alive /exec /files /api/init.
2. `runtime/Dockerfile` + `runtime/sdks/*.sh` + script `build-runtime.sh`
   (publicação manual documentada; workflow = follow-up com revisão humana).
3. `SandboxAgentExecutor` (HTTP) + capability probe + fallback exec.
4. Blueprint: parser `openhandsnet.yaml` + auto-detect + runner no worker.
5. Warm pool por imagem + dormant init no claim.
6. Session key plumbing + `SandboxRecord` migration (`pool_key`,
   `blueprint_status`, `blueprint_log_tail`, `session_key_hash`).
7. Testes: unit (parser/auto-detect/init state machine), integration docker
   (agent alive, exec via HTTP, setup.sh roda, warm-claim dormant→ready).
8. Docs: `docs/deployment/custom-images.md` + `.env.exemplo` novos campos.

## 8. Deliverables

- Imagem `ghcr.io/openhandsnet/runtime` reproduzível + novo default.
- Container com agente HTTP como PID 1; executor dual-mode.
- Blueprint repo→org→global com SDK auto-detect.
- Warm pool por imagem com init adiado.
- Migrações dos dois providers + testes.

## 9. Referências

- https://docs.openhands.dev/enterprise/architecture — app → Runtime API →
  sandboxes; Image Loader; componentes.
- https://docs.openhands.dev/enterprise/custom-sandbox-images/building-custom-images —
  `FROM agent-server`, nunca sobrescrever entrypoint, `prepare-repo` pattern.
- https://docs.openhands.dev/enterprise/custom-sandbox-images/index — warm pool
  por imagem, reconciler 1/min.
- https://docs.openhands.dev/sdk/guides/agent-server/deferred-init — dormant→
  ready, `X-Init-API-Key`, campos do `InitRequest`.
- https://docs.openhands.dev/openhands/usage/sandboxes/docker — portas
  8000/8001/8011/8012, `container_url_pattern`, host-network caveat.
- https://docs.openhands.dev/openhands/usage/customization/repository —
  convenção `.openhands/` no repo.
- https://github.com/OpenHands/software-agent-sdk — `openhands-agent-server/…/docker/Dockerfile`.

## 10. Guardrails

- `.github/workflows/` **não** é tocado nesta spec (publicação da imagem é
  manual/script — workflow dedicado pede revisão humana separada).
- Sem secrets na imagem; `setup.sh` nunca recebe credencial em argv.
- Fallback exec garante que imagens antigas continuam funcionando.
- Blueprint falho nunca derruba a conversa — degrada com log visível.

## 11. DoD

- [ ] Container da imagem nova sobe com agente PID 1 e `/alive` 200.
- [ ] `POST /api/init` dormant→ready; `/exec` 503 antes, funciona depois.
- [ ] Repo com `.openhands/setup.sh` tem o script rodado no boot (log gravado).
- [ ] Repo `*.csproj` sem YAML instala dotnet SDK; `package.json` instala node.
- [ ] Warm pool honra `pool_key` por imagem; claim ativa dormant→ready.
- [ ] Imagem legada sem agente → exec fallback sem regressão.
- [ ] `dotnet build` + `dotnet test` verdes; `.env.exemplo` cobre novas chaves.

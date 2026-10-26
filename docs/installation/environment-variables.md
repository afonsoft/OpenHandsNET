# Referência de variáveis de ambiente

Toda configuração usa o prefixo `OPENHANDSNET_` com `__` separando seção e
chave (`OPENHANDSNET_Database__Provider` → `Database:Provider`). O arquivo
[`.env.exemplo`](../../.env.exemplo) é a lista viva mantida em sync pelo
`scripts/check-env-drift.sh`; esta página documenta semântica e defaults.
English: [../en/installation/environment-variables.md](../en/installation/environment-variables.md).

## Hosting

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_PORT` | `3032` | Porta externa do compose (app escuta 8080 interno) |
| `ASPNETCORE_URLS` | `http://+:8080` | Bind do Kestrel (host/systemd) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` habilita swagger/debug |
| `OPENHANDSNET_DeploymentMode` | `oss` | `oss` \| `self_hosted` \| `cloud` |
| `OPENHANDSNET_Deployment__Replicas` | `1` | >1 exige backplane (`Cache__Provider=redis`) |
| `OPENHANDSNET_MultiTenancy__Enabled` | `false` | Isolamento por org (`IOrgScoped` query filters) |

## Banco de dados

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Database__Provider` | `Sqlite` | `Sqlite` \| `PostgreSql` |
| `OPENHANDSNET_Database__ConnectionString` | `Data Source=data/openhandsnet.db` | ADO.NET/Npgsql conn string |
| `POSTGRES_PASSWORD` | — | Senha do container Postgres (compose) |

## LLM do agente

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_AgentLlm__Provider` | — | `openai` (OpenAI-compat) \| `mock` |
| `OPENHANDSNET_AgentLlm__Model` | `openhandsnet-agent` | Modelo default |
| `OPENHANDSNET_AgentLlm__BaseUrl` | — | Endpoint OpenAI-compat (ex.: OmniRoute) |
| `OPENHANDSNET_AgentLlm__ApiKey` | — | `sk-*`; nunca commitar |
| `OPENHANDSNET_AgentLlm__TimeoutSeconds` | `120` | Timeout por chamada LLM |
| `OPENHANDSNET_AgentLlm__MaxRetries` | `1` | Retries por chamada |
| `OPENHANDSNET_AgentLlm__MaxIterations` | `250` | Passos máx. do loop do agente |
| `OPENHANDSNET_AgentLlm__ToolTimeoutSeconds` | `120` | Timeout por tool |
| `OPENHANDSNET_AgentLlm__BashToolTimeoutSeconds` | `600` | Timeout do tool bash |
| `OPENHANDSNET_AgentLlm__ConfirmationTimeoutMinutes` | `30` | Espera por aprovação `ask` |
| `OPENHANDSNET_AgentLlm__AllowMock` | `false` | Permite provider `mock` fora de oss |
| `OPENHANDSNET_AgentLlm__DefaultContextWindow` | `128000` | Janela p/ meter + condensação |
| `OPENHANDSNET_AgentLlm__RoleModels__<role>` | — | Modelo por papel (ex.: `__condenser`) |
| `OPENHANDSNET_AgentLlm__FallbackModels__<n>` | — | Lista ordenada de fallback |
| `OPENHANDSNET_AgentLlm__SecurityAnalyzer` | `pattern` | `pattern` \| `llm` \| `policy` |
| `OPENHANDSNET_AgentLlm__StrictResume` | `false` | Falha resume com drift de contrato |
| `LLM__MODEL` / `LLM__BASE_URL` / `LLM__API_KEY` | — | Aliases flat legados (estilo upstream) |
| `OPENHANDSNET_Agent__Sdd` | `auto` | Probe `.specs/`/`docs/specs` no prompt |

## Sandbox

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Sandbox__Provider` | `local` | `local` \| `remote` \| `kubernetes` |
| `OPENHANDSNET_Sandbox__DefaultImage` | `ghcr.io/openhandsnet/runtime:latest` | Imagem do sandbox |
| `OPENHANDSNET_Sandbox__Profile` | `medium` | Profile global: `small`/`medium`/`large` (CPU/mem/pids); CPU é clampado ao NCPU do host |
| `OPENHANDSNET_Sandbox__WorkspaceRoot` | `data/workspaces` | Raiz dos workspaces no host |
| `OPENHANDSNET_Sandbox__InstanceOwner` | `default` | Label `owner` dos containers |
| `OPENHANDSNET_Sandbox__EnableDinD` | `false` | Sidecar DinD privileged (docker-in-docker) |
| `OPENHANDSNET_Sandbox__DockerRuntime` | — | OCI runtime alternativo (ex.: `sysbox-runc`) |
| `OPENHANDSNET_Sandbox__DockerSocketMount` | `false` | Monta docker.sock no sandbox (mais fraco que DinD) |
| `OPENHANDSNET_Sandbox__EgressMode` | `unrestricted` | `unrestricted` \| `allowlist` \| `deny` |
| `OPENHANDSNET_Sandbox__EgressAllow` | — | Domínios `;`-separados no allowlist |
| `OPENHANDSNET_Sandbox__EgressProxyImage` | `ubuntu/squid:6.6-24.04_beta` | Imagem do proxy Squid |
| `OPENHANDSNET_Sandbox__WarmPoolSize` | `0` | Sandboxes pré-aquecidos |
| `OPENHANDSNET_Sandbox__WarmPoolImages` | — | Pools extras por imagem (`img:2;img2:1`) |
| `OPENHANDSNET_Sandbox__IdleSleepSeconds` | `30` | Graça pós-run → `docker pause` (`-1` off) |
| `OPENHANDSNET_Sandbox__SleepStopAfterHours` | `0` | Paused >Nh → `docker stop` (`0` off) |
| `OPENHANDSNET_Sandbox__AgentImagePrefixes` | `openhandsnet/runtime` | Prefixos `;` de imagens com agente HTTP :8000 |
| `OPENHANDSNET_Sandbox__PublishAgentPort` | `false` | Publica :8000 em 127.0.0.1 (Docker Desktop) |
| `OPENHANDSNET_Sandbox__BlueprintTimeoutMinutes` | `15` | Timeout de sdk installers + setup.sh |
| `OPENHANDSNET_Sandbox__ControlNetwork` | — | Rede docker compartilhada Api↔agente (runtime-split) |
| `OPENHANDSNET_Sandbox__RoutingMode` | `internal` | `internal` \| `path` \| `host` (preview de surfaces) |
| `SANDBOX_DESKTOP` | `1` (imagens agente) | Env do container: surface de desktop noVNC :6080 (`0` desliga; instalação lazy) |
| `SANDBOX_VSCODE` | `1` (imagens agente) | Env do container: surface code-server :8080 (`0` desliga) |
| `SANDBOX_TERMINAL` | — | Env do container: surface ttyd :7681 (opt-in) |
| `OPENHANDSNET_Sandbox__PublicBaseDomain` | — | Base pública no modo `host` (ex.: `sandboxes.example.com`) |
| `OPENHANDSNET_Sandbox__RemoteBaseUrl` | — | Endpoint do `openhandsnet-runtime-api` |
| `OPENHANDSNET_Sandbox__RemoteApiKey` | — | `X-Runtime-Key` aceito pelo runtime-api |
| `OPENHANDSNET_Sandbox__Kubernetes__Namespace` | `openhandsnet-sandboxes` | Namespace dos pods sandbox |
| `OPENHANDSNET_Sandbox__Kubernetes__KubeconfigPath` | — | Vazio = in-cluster |
| `OPENHANDSNET_Sandbox__Kubernetes__RuntimeClassName` | — | RuntimeClass (ex.: `sysbox-runc`) |
| `OPENHANDSNET_Sandbox__Kubernetes__WorkspacePvcSize` | `4Gi` | PVC por workspace |
| `DOCKER_HOST` | `unix:///var/run/docker.sock` | Endpoint do daemon (rootless/remoto) |
| `OPENHANDSNET_RUNTIME_KEY` | — | Chave servida pelo runtime-api (vazio = fail-closed) |

## Auth

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Auth__Provider` | `none` | `none` (single-user OSS) \| `oidc` |
| `OPENHANDSNET_Auth__Authority` | — | Issuer OIDC (Keycloak realm etc.) |
| `OPENHANDSNET_Auth__ClientId` / `__ClientSecret` | — | Cliente OIDC |
| `OPENHANDSNET_Auth__EnableJwt` | `false` | Bearer JWT opcional |
| `OPENHANDSNET_Auth__DeviceKeyExpirationDays` | `7` | TTL das chaves `sk-*` device-flow |
| `OPENHANDSNET_Auth__UserProvisioningEnabled` | `false` | `POST /organizations/provision-user` |
| `OPENHANDSNET_Auth__EnableSuperAdmin` | `false` | API super-admin em modo `oss` |

## Cache / backplane

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Cache__Provider` | `memory` | `memory` \| `redis` |
| `OPENHANDSNET_Cache__Redis__ConnectionString` | — | ex.: `valkey:6379` |

## Billing / planos

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Billing__EnforcementEnabled` | `false` | `false` = OSS nunca bloqueia; limites via `/api/v1/admin/plans` |

## Git

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Git__CloneMode` | `container` | `container` \| `host` |
| `OPENHANDSNET_Git__WorkBranch` | `openhands/{id}` | Branch de trabalho do agente |
| `OPENHANDSNET_Git__RepoPath` | `/workspace/repo` | Path do clone no sandbox |
| `OPENHANDSNET_Git__AuthorName` / `__AuthorEmail` | `openhandsnet-agent` | Identidade de commit |

## Browser tool

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Browser__Enabled` | `true` | Playwright no sandbox |
| `OPENHANDSNET_Browser__Headless` | `true` | — |
| `OPENHANDSNET_Browser__NavigationTimeoutMs` | `30000` | — |
| `OPENHANDSNET_Browser__AutoInstall` | `true` | Instala browsers no primeiro uso |
| `OPENHANDSNET_Browser__ProfilesEnabled` | `true` | Storage state criptografado + TOTP |

## Artifacts / filestore

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Artifacts__Provider` | `workspace` | `workspace` \| `s3` |
| `OPENHANDSNET_Artifacts__WorkspaceRoot` | `/var/lib/ohn-artifacts` | Destino no provider workspace |
| `OPENHANDSNET_Artifacts__S3__ServiceUrl` | — | Endpoint S3-compat |
| `OPENHANDSNET_Artifacts__S3__Bucket` | `openhandsnet-artifacts` | — |
| `OPENHANDSNET_Artifacts__S3__AccessKey` / `__SecretKey` | — | Credenciais S3 |

## MCP / webhooks / observabilidade

| Variável | Default | Descrição |
|---|---|---|
| `OPENHANDSNET_Mcp__AllowHostMcp` | `false` | MCP servers no host do Api |
| `OPENHANDSNET_Mcp__DefaultCallTimeoutSeconds` | `60` | — |
| `OPENHANDSNET_Webhooks__Endpoints__<n>` | — | URLs outbound |
| `OPENHANDSNET_Webhooks__ConversationIds__<n>` | — | Filtro por conversa (vazio = todas) |
| `OPENHANDSNET_Observability__OtlpEndpoint` | — | OTLP gRPC (ex.: `http://otel:4317`) |

## CLI / eval

| Variável | Descrição |
|---|---|
| `OPENHANDSNET_SERVER_URL` | URL do Api para `openhandsnet` CLI |
| `OPENHANDSNET_API_KEY` | `sk-*` do usuário para o CLI |
| `OPENHANDSNET_CLOUD_URL` | Cloud endpoint (parity com upstream CLI) |
| `OPENHANDSNET_EVAL_JUDGE_{URL,KEY,MODEL}` | Judge LLM do `openhandsnet eval` |
| `OPENHANDSNET_SANDBOX_PROVIDER`, `OPENHANDSNET_WORKSPACE_HOST`, `OPENHANDSNET_WARM_POOL_SIZE` | Aliases flat aceitos pelo CLI/runtime |

## Sidecars do compose

| Variável | Uso |
|---|---|
| `POSTGRES_PASSWORD` | Postgres (compose.postgres/enterprise) |
| `KEYCLOAK_ADMIN_PASSWORD` | Keycloak (enterprise) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana (observability) |
| `OPENAI_API_KEY` | LiteLLM gateway (enterprise) |
| `OPENHANDSNET_LlmProxy__{BaseUrl,MasterKey,Mode,DefaultBudgetUsd}` | BYOR LiteLLM (SPEC-50) |

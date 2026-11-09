# Referência de variáveis de ambiente

Toda configuração usa o prefixo `SANDCASTLE_` com `__` separando seção e
chave (`SANDCASTLE_Database__Provider` → `Database:Provider`). O arquivo
[`.env.exemplo`](../../.env.exemplo) é a lista viva mantida em sync pelo
`scripts/check-env-drift.sh`; esta página documenta semântica e defaults.
English: [../en/installation/environment-variables.md](../en/installation/environment-variables.md).

## Hosting

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_PORT` | `3032` | Porta externa do compose (app escuta 8080 interno) |
| `ASPNETCORE_URLS` | `http://+:8080` | Bind do Kestrel (host/systemd) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` habilita swagger/debug |
| `SANDCASTLE_DeploymentMode` | `oss` | `oss` \| `self_hosted` \| `cloud` |
| `SANDCASTLE_Deployment__Replicas` | `1` | >1 exige backplane (`Cache__Provider=redis`) |
| `SANDCASTLE_MultiTenancy__Enabled` | `false` | Isolamento por org (`IOrgScoped` query filters) |

## Banco de dados

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Database__Provider` | `Sqlite` | `Sqlite` \| `PostgreSql` |
| `SANDCASTLE_Database__ConnectionString` | `Data Source=data/sandcastle.db` | ADO.NET/Npgsql conn string |
| `POSTGRES_PASSWORD` | — | Senha do container Postgres (compose) |

## LLM do agente

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_AgentLlm__Provider` | — | `openai` (OpenAI-compat) \| `mock` |
| `SANDCASTLE_AgentLlm__Model` | `sandcastle-agent` | Modelo default |
| `SANDCASTLE_AgentLlm__BaseUrl` | — | Endpoint OpenAI-compat (ex.: OmniRoute) |
| `SANDCASTLE_AgentLlm__ApiKey` | — | `sk-*`; nunca commitar |
| `SANDCASTLE_AgentLlm__TimeoutSeconds` | `120` | Timeout por chamada LLM |
| `SANDCASTLE_AgentLlm__MaxRetries` | `1` | Retries por chamada |
| `SANDCASTLE_AgentLlm__MaxIterations` | `250` | Passos máx. do loop do agente |
| `SANDCASTLE_AgentLlm__ToolTimeoutSeconds` | `120` | Timeout por tool |
| `SANDCASTLE_AgentLlm__BashToolTimeoutSeconds` | `600` | Timeout do tool bash |
| `SANDCASTLE_AgentLlm__ConfirmationTimeoutMinutes` | `30` | Espera por aprovação `ask` |
| `SANDCASTLE_AgentLlm__AllowMock` | `false` | Permite provider `mock` fora de oss |
| `SANDCASTLE_AgentLlm__DefaultContextWindow` | `128000` | Janela p/ meter + condensação |
| `SANDCASTLE_AgentLlm__RoleModels__<role>` | — | Modelo por papel (ex.: `__condenser`) |
| `SANDCASTLE_AgentLlm__FallbackModels__<n>` | — | Lista ordenada de fallback |
| `SANDCASTLE_AgentLlm__SecurityAnalyzer` | `pattern` | `pattern` \| `llm` \| `policy` |
| `SANDCASTLE_AgentLlm__StrictResume` | `false` | Falha resume com drift de contrato |
| `LLM__MODEL` / `LLM__BASE_URL` / `LLM__API_KEY` | — | Aliases flat legados (estilo upstream) |
| `SANDCASTLE_Agent__Sdd` | `auto` | Probe `.specs/`/`docs/specs` no prompt |

## Sandbox

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Sandbox__Provider` | `local` | `local` \| `remote` \| `kubernetes` |
| `SANDCASTLE_Sandbox__DefaultImage` | `ghcr.io/sandcastle/runtime:latest` | Imagem do sandbox |
| `SANDCASTLE_Sandbox__Profile` | `medium` | Profile global: `small`/`medium`/`large` (CPU/mem/pids); CPU é clampado ao NCPU do host |
| `SANDCASTLE_Sandbox__WorkspaceRoot` | `data/workspaces` | Raiz dos workspaces no host |
| `SANDCASTLE_Sandbox__InstanceOwner` | `default` | Label `owner` dos containers |
| `SANDCASTLE_Sandbox__EnableDinD` | `false` | Sidecar DinD privileged (docker-in-docker) |
| `SANDCASTLE_Sandbox__DockerRuntime` | — | OCI runtime alternativo (ex.: `sysbox-runc`) |
| `SANDCASTLE_Sandbox__DockerSocketMount` | `false` | Monta docker.sock no sandbox (mais fraco que DinD) |
| `SANDCASTLE_Sandbox__EgressMode` | `unrestricted` | `unrestricted` \| `allowlist` \| `deny` |
| `SANDCASTLE_Sandbox__EgressAllow` | — | Domínios `;`-separados no allowlist |
| `SANDCASTLE_Sandbox__EgressProxyImage` | `ubuntu/squid:6.6-24.04_beta` | Imagem do proxy Squid |
| `SANDCASTLE_Sandbox__WarmPoolSize` | `0` | Sandboxes pré-aquecidos |
| `SANDCASTLE_Sandbox__WarmPoolImages` | — | Pools extras por imagem (`img:2;img2:1`) |
| `SANDCASTLE_Sandbox__IdleSleepSeconds` | `30` | Graça pós-run → `docker pause` (`-1` off) |
| `SANDCASTLE_Sandbox__SleepStopAfterHours` | `0` | Paused >Nh → `docker stop` (`0` off) |
| `SANDCASTLE_Sandbox__AgentImagePrefixes` | `sandcastle/runtime` | Prefixos `;` de imagens com agente HTTP :8000 |
| `SANDCASTLE_Sandbox__PublishAgentPort` | `false` | Publica :8000 em 127.0.0.1 (Docker Desktop) |
| `SANDCASTLE_Sandbox__BlueprintTimeoutMinutes` | `15` | Timeout de sdk installers + setup.sh |
| `SANDCASTLE_Sandbox__ControlNetwork` | — | Rede docker compartilhada Api↔agente (runtime-split) |
| `SANDCASTLE_Sandbox__RoutingMode` | `internal` | `internal` \| `path` \| `host` (preview de surfaces) |
| `SANDBOX_DESKTOP` | `1` (imagens agente) | Env do container: surface de desktop noVNC :6080 (`0` desliga; instalação lazy) |
| `SANDBOX_VSCODE` | `1` (imagens agente) | Env do container: surface code-server :8080 (`0` desliga) |
| `SANDBOX_TERMINAL` | — | Env do container: surface ttyd :7681 (opt-in) |
| `SANDCASTLE_Sandbox__PublicBaseDomain` | — | Base pública no modo `host` (ex.: `sandboxes.example.com`) |
| `SANDCASTLE_Sandbox__RemoteBaseUrl` | — | Endpoint do `sandcastle-runtime-api` |
| `SANDCASTLE_Sandbox__RemoteApiKey` | — | `X-Runtime-Key` aceito pelo runtime-api |
| `SANDCASTLE_Sandbox__Kubernetes__Namespace` | `sandcastle-sandboxes` | Namespace dos pods sandbox |
| `SANDCASTLE_Sandbox__Kubernetes__KubeconfigPath` | — | Vazio = in-cluster |
| `SANDCASTLE_Sandbox__Kubernetes__RuntimeClassName` | — | RuntimeClass (ex.: `sysbox-runc`) |
| `SANDCASTLE_Sandbox__Kubernetes__WorkspacePvcSize` | `4Gi` | PVC por workspace |
| `DOCKER_HOST` | `unix:///var/run/docker.sock` | Endpoint do daemon (rootless/remoto) |
| `SANDCASTLE_RUNTIME_KEY` | — | Chave servida pelo runtime-api (vazio = fail-closed) |

## Auth

> ⚠️ `SANDCASTLE_Auth__Provider=none` é **single-user local** — qualquer
> cliente que alcançar a porta vira o usuário. Não exponha à internet sem
> `oidc` configurado; se expuser, mantenha o rate limit e as IP rules
> (SPEC-59) ativos — ambos já vêm ligados no `docker-compose.beta.yaml`.

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Auth__Provider` | `none` | `none` (single-user OSS) \| `oidc` |
| `SANDCASTLE_Auth__Authority` | — | Issuer OIDC (Keycloak realm etc.) |
| `SANDCASTLE_Auth__ClientId` / `__ClientSecret` | — | Cliente OIDC |
| `SANDCASTLE_Auth__EnableJwt` | `false` | Bearer JWT opcional |
| `SANDCASTLE_Auth__DeviceKeyExpirationDays` | `7` | TTL das chaves `sk-*` device-flow |
| `SANDCASTLE_Auth__UserProvisioningEnabled` | `false` | `POST /organizations/provision-user` |
| `SANDCASTLE_Auth__EnableSuperAdmin` | `false` | API super-admin em modo `oss` |

## Cache / backplane

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Cache__Provider` | `memory` | `memory` \| `redis` |
| `SANDCASTLE_Cache__Redis__ConnectionString` | — | ex.: `valkey:6379` |

## Billing / planos

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Billing__EnforcementEnabled` | `false` | `false` = OSS nunca bloqueia; limites via `/api/v1/admin/plans` |

## Git

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Git__CloneMode` | `container` | `container` \| `host` |
| `SANDCASTLE_Git__WorkBranch` | `openhands/{id}` | Branch de trabalho do agente |
| `SANDCASTLE_Git__RepoPath` | `/workspace/repo` | Path do clone no sandbox |
| `SANDCASTLE_Git__AuthorName` / `__AuthorEmail` | `sandcastle-agent` | Identidade de commit |

## Browser tool

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Browser__Enabled` | `true` | Playwright no sandbox |
| `SANDCASTLE_Browser__Headless` | `true` | — |
| `SANDCASTLE_Browser__NavigationTimeoutMs` | `30000` | — |
| `SANDCASTLE_Browser__AutoInstall` | `true` | Instala browsers no primeiro uso |
| `SANDCASTLE_Browser__ProfilesEnabled` | `true` | Storage state criptografado + TOTP |

## Artifacts / filestore

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Artifacts__Provider` | `workspace` | `workspace` \| `s3` |
| `SANDCASTLE_Artifacts__WorkspaceRoot` | `/var/lib/sc-artifacts` | Destino no provider workspace |
| `SANDCASTLE_Artifacts__S3__ServiceUrl` | — | Endpoint S3-compat |
| `SANDCASTLE_Artifacts__S3__Bucket` | `sandcastle-artifacts` | — |
| `SANDCASTLE_Artifacts__S3__AccessKey` / `__SecretKey` | — | Credenciais S3 |

## MCP / webhooks / observabilidade

| Variável | Default | Descrição |
|---|---|---|
| `SANDCASTLE_Mcp__AllowHostMcp` | `false` | MCP servers no host do Api |
| `SANDCASTLE_Mcp__DefaultCallTimeoutSeconds` | `60` | — |
| `SANDCASTLE_Webhooks__Endpoints__<n>` | — | URLs outbound |
| `SANDCASTLE_Webhooks__ConversationIds__<n>` | — | Filtro por conversa (vazio = todas) |
| `SANDCASTLE_Observability__OtlpEndpoint` | — | OTLP gRPC (ex.: `http://otel:4317`) |

## CLI / eval

| Variável | Descrição |
|---|---|
| `SANDCASTLE_SERVER_URL` | URL do Api para `sandcastle` CLI |
| `SANDCASTLE_API_KEY` | `sk-*` do usuário para o CLI |
| `SANDCASTLE_CLOUD_URL` | Cloud endpoint (parity com upstream CLI) |
| `SANDCASTLE_EVAL_JUDGE_{URL,KEY,MODEL}` | Judge LLM do `sandcastle eval` |
| `SANDCASTLE_SANDBOX_PROVIDER`, `SANDCASTLE_WORKSPACE_HOST`, `SANDCASTLE_WARM_POOL_SIZE` | Aliases flat aceitos pelo CLI/runtime |

## Sidecars do compose

| Variável | Uso |
|---|---|
| `POSTGRES_PASSWORD` | Postgres (compose.postgres/enterprise) |
| `KEYCLOAK_ADMIN_PASSWORD` | Keycloak (enterprise) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana (observability) |
| `OPENAI_API_KEY` | LiteLLM gateway (enterprise) |
| `SANDCASTLE_LlmProxy__{BaseUrl,MasterKey,Mode,DefaultBudgetUsd}` | BYOR LiteLLM (SPEC-50) |

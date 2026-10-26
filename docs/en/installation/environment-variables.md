# Environment variable reference

Every setting uses the `OPENHANDSNET_` prefix with `__` between section and
key (`OPENHANDSNET_Database__Provider` → `Database:Provider`). The
[`.env.exemplo`](../../../.env.exemplo) file is the living list kept in sync
by `scripts/check-env-drift.sh`; this page documents semantics and defaults.
Português: [../../installation/environment-variables.md](../../installation/environment-variables.md).

## Hosting

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_PORT` | `3032` | External compose port (app listens on 8080 internally) |
| `ASPNETCORE_URLS` | `http://+:8080` | Kestrel bind (host/systemd) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables swagger/debug |
| `OPENHANDSNET_DeploymentMode` | `oss` | `oss` \| `self_hosted` \| `cloud` |
| `OPENHANDSNET_Deployment__Replicas` | `1` | >1 requires the backplane (`Cache__Provider=redis`) |
| `OPENHANDSNET_MultiTenancy__Enabled` | `false` | Per-org isolation (`IOrgScoped` query filters) |

## Database

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Database__Provider` | `Sqlite` | `Sqlite` \| `PostgreSql` |
| `OPENHANDSNET_Database__ConnectionString` | `Data Source=data/openhandsnet.db` | ADO.NET/Npgsql conn string |
| `POSTGRES_PASSWORD` | — | Postgres container password (compose) |

## Agent LLM

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_AgentLlm__Provider` | — | `openai` (OpenAI-compat) \| `mock` |
| `OPENHANDSNET_AgentLlm__Model` | `openhandsnet-agent` | Default model |
| `OPENHANDSNET_AgentLlm__BaseUrl` | — | OpenAI-compat endpoint (e.g. OmniRoute) |
| `OPENHANDSNET_AgentLlm__ApiKey` | — | `sk-*`; never commit |
| `OPENHANDSNET_AgentLlm__TimeoutSeconds` | `120` | Per-LLM-call timeout |
| `OPENHANDSNET_AgentLlm__MaxRetries` | `1` | Retries per call |
| `OPENHANDSNET_AgentLlm__MaxIterations` | `250` | Max agent loop steps |
| `OPENHANDSNET_AgentLlm__ToolTimeoutSeconds` | `120` | Per-tool timeout |
| `OPENHANDSNET_AgentLlm__BashToolTimeoutSeconds` | `600` | Bash tool timeout |
| `OPENHANDSNET_AgentLlm__ConfirmationTimeoutMinutes` | `30` | `ask` approval wait |
| `OPENHANDSNET_AgentLlm__AllowMock` | `false` | Allow `mock` provider outside oss |
| `OPENHANDSNET_AgentLlm__DefaultContextWindow` | `128000` | Window for meter + condensation |
| `OPENHANDSNET_AgentLlm__RoleModels__<role>` | — | Per-role model (e.g. `__condenser`) |
| `OPENHANDSNET_AgentLlm__FallbackModels__<n>` | — | Ordered fallback list |
| `OPENHANDSNET_AgentLlm__SecurityAnalyzer` | `pattern` | `pattern` \| `llm` \| `policy` |
| `OPENHANDSNET_AgentLlm__StrictResume` | `false` | Fail resume on contract drift |
| `LLM__MODEL` / `LLM__BASE_URL` / `LLM__API_KEY` | — | Legacy flat aliases (upstream style) |
| `OPENHANDSNET_Agent__Sdd` | `auto` | Probe `.specs/`/`docs/specs` into the prompt |

## Sandbox

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Sandbox__Provider` | `local` | `local` \| `remote` \| `kubernetes` |
| `OPENHANDSNET_Sandbox__DefaultImage` | `ghcr.io/openhandsnet/runtime:latest` | Sandbox image |
| `OPENHANDSNET_Sandbox__Profile` | `medium` | Global profile: `small`/`medium`/`large` (CPU/mem/pids); CPU is clamped to host NCPU |
| `OPENHANDSNET_Sandbox__WorkspaceRoot` | `data/workspaces` | Host-side workspace root |
| `OPENHANDSNET_Sandbox__InstanceOwner` | `default` | `owner` label on containers |
| `OPENHANDSNET_Sandbox__EnableDinD` | `false` | Privileged DinD sidecar (docker-in-docker) |
| `OPENHANDSNET_Sandbox__DockerRuntime` | — | Alternate OCI runtime (e.g. `sysbox-runc`) |
| `OPENHANDSNET_Sandbox__DockerSocketMount` | `false` | Mount docker.sock in the sandbox (weaker than DinD) |
| `OPENHANDSNET_Sandbox__EgressMode` | `unrestricted` | `unrestricted` \| `allowlist` \| `deny` |
| `OPENHANDSNET_Sandbox__EgressAllow` | — | `;`-separated allowlist domains |
| `OPENHANDSNET_Sandbox__EgressProxyImage` | `ubuntu/squid:6.6-24.04_beta` | Squid proxy image |
| `OPENHANDSNET_Sandbox__WarmPoolSize` | `0` | Pre-warmed sandboxes |
| `OPENHANDSNET_Sandbox__WarmPoolImages` | — | Extra pools per image (`img:2;img2:1`) |
| `OPENHANDSNET_Sandbox__IdleSleepSeconds` | `30` | Post-run grace → `docker pause` (`-1` off) |
| `OPENHANDSNET_Sandbox__SleepStopAfterHours` | `0` | Paused >Nh → `docker stop` (`0` off) |
| `OPENHANDSNET_Sandbox__AgentImagePrefixes` | `openhandsnet/runtime` | `;`-prefixes of images with HTTP agent :8000 |
| `OPENHANDSNET_Sandbox__PublishAgentPort` | `false` | Publish :8000 on 127.0.0.1 (Docker Desktop) |
| `OPENHANDSNET_Sandbox__BlueprintTimeoutMinutes` | `15` | sdk installers + setup.sh timeout |
| `OPENHANDSNET_Sandbox__ControlNetwork` | — | Shared Api↔agent docker network (runtime-split) |
| `OPENHANDSNET_Sandbox__RoutingMode` | `internal` | `internal` \| `path` \| `host` (surface previews) |
| `OPENHANDSNET_Sandbox__PublicBaseDomain` | — | Public base in `host` mode (e.g. `sandboxes.example.com`) |
| `OPENHANDSNET_Sandbox__RemoteBaseUrl` | — | `openhandsnet-runtime-api` endpoint |
| `OPENHANDSNET_Sandbox__RemoteApiKey` | — | `X-Runtime-Key` accepted by the runtime-api |
| `OPENHANDSNET_Sandbox__Kubernetes__Namespace` | `openhandsnet-sandboxes` | Sandbox pods namespace |
| `OPENHANDSNET_Sandbox__Kubernetes__KubeconfigPath` | — | Empty = in-cluster |
| `OPENHANDSNET_Sandbox__Kubernetes__RuntimeClassName` | — | RuntimeClass (e.g. `sysbox-runc`) |
| `OPENHANDSNET_Sandbox__Kubernetes__WorkspacePvcSize` | `4Gi` | Per-workspace PVC |
| `DOCKER_HOST` | `unix:///var/run/docker.sock` | Daemon endpoint (rootless/remote) |
| `OPENHANDSNET_RUNTIME_KEY` | — | Key served by the runtime-api (empty = fail-closed) |

## Auth

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Auth__Provider` | `none` | `none` (single-user OSS) \| `oidc` |
| `OPENHANDSNET_Auth__Authority` | — | OIDC issuer (Keycloak realm etc.) |
| `OPENHANDSNET_Auth__ClientId` / `__ClientSecret` | — | OIDC client |
| `OPENHANDSNET_Auth__EnableJwt` | `false` | Optional bearer JWT |
| `OPENHANDSNET_Auth__DeviceKeyExpirationDays` | `7` | `sk-*` device-flow key TTL |
| `OPENHANDSNET_Auth__UserProvisioningEnabled` | `false` | `POST /organizations/provision-user` |
| `OPENHANDSNET_Auth__EnableSuperAdmin` | `false` | Super-admin API in `oss` mode |

## Cache / backplane

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Cache__Provider` | `memory` | `memory` \| `redis` |
| `OPENHANDSNET_Cache__Redis__ConnectionString` | — | e.g. `valkey:6379` |

## Billing / plans

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Billing__EnforcementEnabled` | `false` | `false` = OSS never blocks; limits via `/api/v1/admin/plans` |

## Git

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Git__CloneMode` | `container` | `container` \| `host` |
| `OPENHANDSNET_Git__WorkBranch` | `openhands/{id}` | Agent work branch |
| `OPENHANDSNET_Git__RepoPath` | `/workspace/repo` | Clone path in the sandbox |
| `OPENHANDSNET_Git__AuthorName` / `__AuthorEmail` | `openhandsnet-agent` | Commit identity |

## Browser tool

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Browser__Enabled` | `true` | Playwright in the sandbox |
| `OPENHANDSNET_Browser__Headless` | `true` | — |
| `OPENHANDSNET_Browser__NavigationTimeoutMs` | `30000` | — |
| `OPENHANDSNET_Browser__AutoInstall` | `true` | Install browsers on first use |
| `OPENHANDSNET_Browser__ProfilesEnabled` | `true` | Encrypted storage state + TOTP |

## Artifacts / filestore

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Artifacts__Provider` | `workspace` | `workspace` \| `s3` |
| `OPENHANDSNET_Artifacts__WorkspaceRoot` | `/var/lib/ohn-artifacts` | Target in workspace provider |
| `OPENHANDSNET_Artifacts__S3__ServiceUrl` | — | S3-compat endpoint |
| `OPENHANDSNET_Artifacts__S3__Bucket` | `openhandsnet-artifacts` | — |
| `OPENHANDSNET_Artifacts__S3__AccessKey` / `__SecretKey` | — | S3 credentials |

## MCP / webhooks / observability

| Variable | Default | Description |
|---|---|---|
| `OPENHANDSNET_Mcp__AllowHostMcp` | `false` | MCP servers on the Api host |
| `OPENHANDSNET_Mcp__DefaultCallTimeoutSeconds` | `60` | — |
| `OPENHANDSNET_Webhooks__Endpoints__<n>` | — | Outbound URLs |
| `OPENHANDSNET_Webhooks__ConversationIds__<n>` | — | Per-conversation filter (empty = all) |
| `OPENHANDSNET_Observability__OtlpEndpoint` | — | OTLP gRPC (e.g. `http://otel:4317`) |

## CLI / eval

| Variable | Description |
|---|---|
| `OPENHANDSNET_SERVER_URL` | Api URL for the `openhandsnet` CLI |
| `OPENHANDSNET_API_KEY` | User `sk-*` for the CLI |
| `OPENHANDSNET_CLOUD_URL` | Cloud endpoint (upstream CLI parity) |
| `OPENHANDSNET_EVAL_JUDGE_{URL,KEY,MODEL}` | `openhandsnet eval` LLM judge |
| `OPENHANDSNET_SANDBOX_PROVIDER`, `OPENHANDSNET_WORKSPACE_HOST`, `OPENHANDSNET_WARM_POOL_SIZE` | Flat aliases accepted by CLI/runtime |

## Compose sidecars

| Variable | Use |
|---|---|
| `POSTGRES_PASSWORD` | Postgres (compose.postgres/enterprise) |
| `KEYCLOAK_ADMIN_PASSWORD` | Keycloak (enterprise) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana (observability) |
| `OPENAI_API_KEY` | LiteLLM gateway (enterprise) |
| `OPENHANDSNET_LlmProxy__{BaseUrl,MasterKey,Mode,DefaultBudgetUsd}` | BYOR LiteLLM (SPEC-50) |

# Environment variable reference

Every setting uses the `SANDCASTLE_` prefix with `__` between section and
key (`SANDCASTLE_Database__Provider` → `Database:Provider`). The
[`.env.exemplo`](../../../.env.exemplo) file is the living list kept in sync
by `scripts/check-env-drift.sh`; this page documents semantics and defaults.
Português: [../../installation/environment-variables.md](../../installation/environment-variables.md).

## Hosting

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_PORT` | `3032` | External compose port (app listens on 8080 internally) |
| `ASPNETCORE_URLS` | `http://+:8080` | Kestrel bind (host/systemd) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables swagger/debug |
| `SANDCASTLE_DeploymentMode` | `oss` | `oss` \| `self_hosted` \| `cloud` |
| `SANDCASTLE_Deployment__Replicas` | `1` | >1 requires the backplane (`Cache__Provider=redis`) |
| `SANDCASTLE_MultiTenancy__Enabled` | `false` | Per-org isolation (`IOrgScoped` query filters) |

## Database

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Database__Provider` | `Sqlite` | `Sqlite` \| `PostgreSql` |
| `SANDCASTLE_Database__ConnectionString` | `Data Source=data/sandcastle.db` | ADO.NET/Npgsql conn string |
| `POSTGRES_PASSWORD` | — | Postgres container password (compose) |

## Agent LLM

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_AgentLlm__Provider` | — | `openai` (OpenAI-compat) \| `mock` |
| `SANDCASTLE_AgentLlm__Model` | `sandcastle-agent` | Default model |
| `SANDCASTLE_AgentLlm__BaseUrl` | — | OpenAI-compat endpoint (e.g. OmniRoute) |
| `SANDCASTLE_AgentLlm__ApiKey` | — | `sk-*`; never commit |
| `SANDCASTLE_AgentLlm__TimeoutSeconds` | `120` | Per-LLM-call timeout |
| `SANDCASTLE_AgentLlm__MaxRetries` | `1` | Retries per call |
| `SANDCASTLE_AgentLlm__MaxIterations` | `250` | Max agent loop steps |
| `SANDCASTLE_AgentLlm__ToolTimeoutSeconds` | `120` | Per-tool timeout |
| `SANDCASTLE_AgentLlm__BashToolTimeoutSeconds` | `600` | Bash tool timeout |
| `SANDCASTLE_AgentLlm__ConfirmationTimeoutMinutes` | `30` | `ask` approval wait |
| `SANDCASTLE_AgentLlm__AllowMock` | `false` | Allow `mock` provider outside oss |
| `SANDCASTLE_AgentLlm__DefaultContextWindow` | `128000` | Window for meter + condensation |
| `SANDCASTLE_AgentLlm__RoleModels__<role>` | — | Per-role model (e.g. `__condenser`) |
| `SANDCASTLE_AgentLlm__FallbackModels__<n>` | — | Ordered fallback list |
| `SANDCASTLE_AgentLlm__SecurityAnalyzer` | `pattern` | `pattern` \| `llm` \| `policy` |
| `SANDCASTLE_AgentLlm__StrictResume` | `false` | Fail resume on contract drift |
| `LLM__MODEL` / `LLM__BASE_URL` / `LLM__API_KEY` | — | Legacy flat aliases (upstream style) |
| `SANDCASTLE_Agent__Sdd` | `auto` | Probe `.specs/`/`docs/specs` into the prompt |

## Sandbox

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Sandbox__Provider` | `local` | `local` \| `remote` \| `kubernetes` |
| `SANDCASTLE_Sandbox__DefaultImage` | `ghcr.io/sandcastle/runtime:latest` | Sandbox image |
| `SANDCASTLE_Sandbox__Profile` | `medium` | Global profile: `small`/`medium`/`large` (CPU/mem/pids); CPU is clamped to host NCPU |
| `SANDCASTLE_Sandbox__WorkspaceRoot` | `data/workspaces` | Host-side workspace root |
| `SANDCASTLE_Sandbox__InstanceOwner` | `default` | `owner` label on containers |
| `SANDCASTLE_Sandbox__EnableDinD` | `false` | Privileged DinD sidecar (docker-in-docker) |
| `SANDCASTLE_Sandbox__DockerRuntime` | — | Alternate OCI runtime (e.g. `sysbox-runc`) |
| `SANDCASTLE_Sandbox__DockerSocketMount` | `false` | Mount docker.sock in the sandbox (weaker than DinD) |
| `SANDCASTLE_Sandbox__EgressMode` | `unrestricted` | `unrestricted` \| `allowlist` \| `deny` |
| `SANDCASTLE_Sandbox__EgressAllow` | — | `;`-separated allowlist domains |
| `SANDCASTLE_Sandbox__EgressProxyImage` | `ubuntu/squid:6.6-24.04_beta` | Squid proxy image |
| `SANDCASTLE_Sandbox__WarmPoolSize` | `0` | Pre-warmed sandboxes |
| `SANDCASTLE_Sandbox__WarmPoolImages` | — | Extra pools per image (`img:2;img2:1`) |
| `SANDCASTLE_Sandbox__IdleSleepSeconds` | `30` | Post-run grace → `docker pause` (`-1` off) |
| `SANDCASTLE_Sandbox__SleepStopAfterHours` | `0` | Paused >Nh → `docker stop` (`0` off) |
| `SANDCASTLE_Sandbox__AgentImagePrefixes` | `sandcastle/runtime` | `;`-prefixes of images with HTTP agent :8000 |
| `SANDCASTLE_Sandbox__PublishAgentPort` | `false` | Publish :8000 on 127.0.0.1 (Docker Desktop) |
| `SANDCASTLE_Sandbox__BlueprintTimeoutMinutes` | `15` | sdk installers + setup.sh timeout |
| `SANDCASTLE_Sandbox__ControlNetwork` | — | Shared Api↔agent docker network (runtime-split) |
| `SANDCASTLE_Sandbox__RoutingMode` | `internal` | `internal` \| `path` \| `host` (surface previews) |
| `SANDBOX_DESKTOP` | `1` (agent images) | Container env: noVNC desktop surface :6080 (`0` disables; lazy install) |
| `SANDBOX_VSCODE` | `1` (agent images) | Container env: code-server surface :8080 (`0` disables) |
| `SANDBOX_TERMINAL` | — | Container env: ttyd surface :7681 (opt-in) |
| `SANDCASTLE_Sandbox__PublicBaseDomain` | — | Public base in `host` mode (e.g. `sandboxes.example.com`) |
| `SANDCASTLE_Sandbox__RemoteBaseUrl` | — | `sandcastle-runtime-api` endpoint |
| `SANDCASTLE_Sandbox__RemoteApiKey` | — | `X-Runtime-Key` accepted by the runtime-api |
| `SANDCASTLE_Sandbox__Kubernetes__Namespace` | `sandcastle-sandboxes` | Sandbox pods namespace |
| `SANDCASTLE_Sandbox__Kubernetes__KubeconfigPath` | — | Empty = in-cluster |
| `SANDCASTLE_Sandbox__Kubernetes__RuntimeClassName` | — | RuntimeClass (e.g. `sysbox-runc`) |
| `SANDCASTLE_Sandbox__Kubernetes__WorkspacePvcSize` | `4Gi` | Per-workspace PVC |
| `DOCKER_HOST` | `unix:///var/run/docker.sock` | Daemon endpoint (rootless/remote) |
| `SANDCASTLE_RUNTIME_KEY` | — | Key served by the runtime-api (empty = fail-closed) |

## Auth

> ⚠️ `SANDCASTLE_Auth__Provider=none` is **single-user local** — any
> client that can reach the port becomes the user. Do NOT expose it to
> the internet without `oidc`; if you do, keep rate limiting and the
> IP rules (SPEC-59) enabled — both are on in `docker-compose.beta.yaml`.

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Auth__Provider` | `none` | `none` (single-user OSS) \| `oidc` |
| `SANDCASTLE_Auth__Authority` | — | OIDC issuer (Keycloak realm etc.) |
| `SANDCASTLE_Auth__ClientId` / `__ClientSecret` | — | OIDC client |
| `SANDCASTLE_Auth__EnableJwt` | `false` | Optional bearer JWT |
| `SANDCASTLE_Auth__DeviceKeyExpirationDays` | `7` | `sk-*` device-flow key TTL |
| `SANDCASTLE_Auth__UserProvisioningEnabled` | `false` | `POST /organizations/provision-user` |
| `SANDCASTLE_Auth__EnableSuperAdmin` | `false` | Super-admin API in `oss` mode |

## Cache / backplane

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Cache__Provider` | `memory` | `memory` \| `redis` |
| `SANDCASTLE_Cache__Redis__ConnectionString` | — | e.g. `valkey:6379` |

## Billing / plans

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Billing__EnforcementEnabled` | `false` | `false` = OSS never blocks; limits via `/api/v1/admin/plans` |

## Git

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Git__CloneMode` | `container` | `container` \| `host` |
| `SANDCASTLE_Git__WorkBranch` | `openhands/{id}` | Agent work branch |
| `SANDCASTLE_Git__RepoPath` | `/workspace/repo` | Clone path in the sandbox |
| `SANDCASTLE_Git__AuthorName` / `__AuthorEmail` | `sandcastle-agent` | Commit identity |

## Browser tool

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Browser__Enabled` | `true` | Playwright in the sandbox |
| `SANDCASTLE_Browser__Headless` | `true` | — |
| `SANDCASTLE_Browser__NavigationTimeoutMs` | `30000` | — |
| `SANDCASTLE_Browser__AutoInstall` | `true` | Install browsers on first use |
| `SANDCASTLE_Browser__ProfilesEnabled` | `true` | Encrypted storage state + TOTP |

## Artifacts / filestore

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Artifacts__Provider` | `workspace` | `workspace` \| `s3` |
| `SANDCASTLE_Artifacts__WorkspaceRoot` | `/var/lib/sc-artifacts` | Target in workspace provider |
| `SANDCASTLE_Artifacts__S3__ServiceUrl` | — | S3-compat endpoint |
| `SANDCASTLE_Artifacts__S3__Bucket` | `sandcastle-artifacts` | — |
| `SANDCASTLE_Artifacts__S3__AccessKey` / `__SecretKey` | — | S3 credentials |

## MCP / webhooks / observability

| Variable | Default | Description |
|---|---|---|
| `SANDCASTLE_Mcp__AllowHostMcp` | `false` | MCP servers on the Api host |
| `SANDCASTLE_Mcp__DefaultCallTimeoutSeconds` | `60` | — |
| `SANDCASTLE_Webhooks__Endpoints__<n>` | — | Outbound URLs |
| `SANDCASTLE_Webhooks__ConversationIds__<n>` | — | Per-conversation filter (empty = all) |
| `SANDCASTLE_Observability__OtlpEndpoint` | — | OTLP gRPC (e.g. `http://otel:4317`) |

## CLI / eval

| Variable | Description |
|---|---|
| `SANDCASTLE_SERVER_URL` | Api URL for the `sandcastle` CLI |
| `SANDCASTLE_API_KEY` | User `sk-*` for the CLI |
| `SANDCASTLE_CLOUD_URL` | Cloud endpoint (upstream CLI parity) |
| `SANDCASTLE_EVAL_JUDGE_{URL,KEY,MODEL}` | `sandcastle eval` LLM judge |
| `SANDCASTLE_SANDBOX_PROVIDER`, `SANDCASTLE_WORKSPACE_HOST`, `SANDCASTLE_WARM_POOL_SIZE` | Flat aliases accepted by CLI/runtime |

## Compose sidecars

| Variable | Use |
|---|---|
| `POSTGRES_PASSWORD` | Postgres (compose.postgres/enterprise) |
| `KEYCLOAK_ADMIN_PASSWORD` | Keycloak (enterprise) |
| `GRAFANA_ADMIN_PASSWORD` | Grafana (observability) |
| `OPENAI_API_KEY` | LiteLLM gateway (enterprise) |
| `SANDCASTLE_LlmProxy__{BaseUrl,MasterKey,Mode,DefaultBudgetUsd}` | BYOR LiteLLM (SPEC-50) |

# Troubleshooting — operations runbook

Diagnostics for self-hosted deployments (compose or Helm/k8s). Start with
the **support bundle**, keep the investigation **read-only**, and apply
fixes through configuration — never ad hoc `kubectl edit`/`docker rm`.

## 1. Support bundle first

`GET /api/v1/support-bundle` (or the link on the `/admin` page) downloads
a ZIP with: detailed readiness, versions, entity counts, log tail
(`openhandsnet-*.log`) and the effective configuration with **every
sensitive value masked** (redaction is covered by tests).

```bash
curl -sf -o bundle.zip http://localhost:8080/api/v1/support-bundle
```

The bundle may contain sensitive configuration — **review it** before
attaching it to a ticket. A healthy analyzer summary does not prove the
UI can open a conversation: run the checks in section 4.

## 2. System state

### Compose

```bash
docker compose ps                              # per-service status
docker compose logs --tail=200 app             # app (API + UI)
docker compose logs --tail=100 runtime-api     # split mode only (provider=remote)
docker ps --filter label=openhandsnet.managed  # live sandboxes
```

### Kubernetes

```bash
kubectl get pods -n openhandsnet -o wide
kubectl get pods -n openhandsnet-sandboxes -o wide
kubectl get events -n openhandsnet --sort-by=.metadata.creationTimestamp
kubectl describe pod -n openhandsnet <pod>
kubectl logs -n openhandsnet <pod> --all-containers=true --previous --timestamps
kubectl top pods -n openhandsnet               # if the Metrics API exists
```

Start with the `STATUS`/`READY`/`RESTARTS` columns: `Pending` =
scheduling/storage; `CrashLoopBackOff` = dependency or healthcheck;
restarts = dependency/resource limit. Use `kubectl logs --previous` after
a restart.

## 3. Component → symptom

| Component | Investigate when |
|---|---|
| `app` (Api/UI) | UI/API errors, conversations, `dotnet` logs, boot migrations |
| `runtime-api` | sandbox create/pause/cleanup (provider `remote`) |
| pods `ohn-sandbox-*` / `ohn-pool-*` | a specific conversation does not start; warm pool does not claim |
| `litellm` | model rejected, proxy auth (BYOR), budget |
| `keycloak` | login/SSO, tokens, client redirect |
| `postgres` | readiness `database` failing, migrations |
| `valkey` | multi-replica backplane (shared cache/rate-limit) |

## 4. First-conversation checks

| Symptom | Check |
|---|---|
| Login OK but the UI asks for a backend/API key | `DeploymentMode`/auth config; frontend served by the app on `:8080`; chart ingress (chart-version values) |
| "Failed to start sandbox" | engine logs (`DockerSandboxEngine`/`KubernetesSandboxEngine`): daemon reachable? image pullable? `runtime-api` up and `X-Runtime-Key` **identical** on both sides (`Sandbox:RemoteApiKey` = `RuntimeApi:ApiKey`) — compare without printing values |
| First model request fails (proxy token/model/endpoint) | BYOR strict rejects models outside the proxy `GET /models` → `agent_diagnostic` `byor_model_rejected`; user LLM profile vs `LlmProxy:Mode` |
| Egress `deny` breaks clone/LLM | `Sandbox:EgressMode` + `host` clone mode or internal mirror (see `dind.md`) |
| Warm pool not used | the pool serves the **default spec** only; custom specs cold-start (see `kubernetes.md`/`dind.md`) |

Quick diagnostics: `openhandsnet doctor` (environment), `doctor --preflight`
(install gate: DNS/egress/ports/docker socket), `doctor --k8s` (cluster,
`runtimeclass sysbox-runc`, StorageClass). HTTP probes:
`/api/v1/health` (liveness) and `/api/v1/readiness` (real dependencies —
`database` runs `SELECT 1`; 503 = app cannot reach/authenticate to PG).

## 5. Temporary debug logging

1. `Logging__LogLevel__Default=Debug` (env) — or a `Serilog` override in
   compose/chart.
2. Reproduce the problem.
3. Collect the logs (or a support bundle).
4. Revert to `Information` and roll out.

Do not leave DEBUG on in production — high volume, verbose data.

## 6. Specific symptoms

- **Boot migration failure**: database in a non-UTF8 encoding (LATIN1) or
  version < 16 — see `external-postgres.md` (requirements).
- **DinD does not start in the sandbox**: kernel < 6.3 with sysbox; or
  `EnableDinD` without the hardened runtime in compose (privileged
  sidecar — see `dind.md` mechanism matrix).
- **`byor_model_rejected`**: model outside the proxy allowed list —
  adjust `deploy/litellm.yaml` or the user profile.
- **Unexpected 429 rate limit**: `RateLimit:*` per actor/route — raise the
  limit or inspect `X-RateLimit-*` on the response.
- **403 input blocked**: `IInputGuard` (`pattern`) — review the message;
  `security_policy.input_guard=llm` uses `InputGuard:LlmEndpoint`.

## References

- `cli.md` — `doctor`, exit codes.
- `ui/admin.md` — support bundle, admin pages.
- `deployment/{dind,external-postgres,kubernetes,security,egress-allowlist}.md`
- Upstream: <https://docs.openhands.dev/enterprise/troubleshooting>
  (concepts).
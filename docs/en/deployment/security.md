# Deployment security

## docker.sock

The app creates/manages sandbox containers via the Docker API. Mounting
`/var/run/docker.sock` gives the process full daemon control —
effectively root on the host.

**Mitigations, in preference order:**

1. **Rootless Docker** — per-user daemon; socket at
   `unix:///run/user/<uid>/docker.sock` (`DOCKER_HOST`).
2. **`DOCKER_HOST=tcp://`** with mutual TLS to a dedicated daemon/VM.
3. **Socket proxy** (e.g. `tecnativa/docker-socket-proxy`) exposing only
   the needed APIs (`containers`, `images`, `exec`) — limited POST.
4. Never mount the sock in examples labelled "production" without the
   mitigations above.

## Secrets

- No sensitive values in a committed `.env` — `.env.exemplo` only holds
  placeholders.
- App secrets live encrypted (DataProtection) in the `secrets` table.
- Git tokens enter the container via a `GIT_ASKPASS` shim — never in the
  URL, args or logs (`SecretRedactor` covers outputs).
- `sk-*` API keys are stored as SHA-256 hashes (SPEC-08).

## Image

- Final runtime `aspnet:10.0`, `openhandsnet` user (non-root), no SDK.
- `HEALTHCHECK` on `/api/v1/ready` (failure ≠ automatic restart — the
  compose also has a healthcheck for orchestration).

## Metrics and logs

- `/metrics` exposes counters only (never prompts, file contents or
  secrets — only metadata like token counts and relative paths).
- Logs carry `trace_id`, `conversation_id`, `service` — never
  credentials.
- If needed, protect `/metrics` behind a reverse proxy with auth.

## Per-org security policies (SPEC-48)

Each organization has a `security_policy` (`security_policies` table,
one row per org) governing the agent's confirmation gate:

- **`preset`**: `permissive` | `balanced` (default) | `strict`.
  - `balanced`: `pattern` analyzer, confirmation only on `high` risk,
    `mcp__*` keeps the guardrail floor (credential boundary).
  - `strict`: LLM analyzer (falls back to `pattern` + warning when no
    provider is configured), confirmation on `medium`+, `mcp__*` always
    asks.
  - `permissive`: analyzer off, confirmation only on `high` —
    **explicit opt-out** that also relaxes the MCP floor.
- **Per-field overrides** (beat the preset): `analyzer_override`
  (`pattern`/`llm`/`off`), `ask_mcp_tools`, `min_confirm_risk`
  (`low`/`medium`/`high`), `resume_strict`.

API: `GET|PUT /api/v1/organizations/{id}/security-policy` (members can
read; writes require `perm:edit_org_settings`; `PUT` with a null body
resets to `balanced`). Every write audits `security_policy_updated`.
The policy is resolved once per run and cached for 60s via
`IDistributedCache`.

`security_risk` diagnostics carry `policy=<preset>, min_confirm=<…>`
for decision observability.

### Fail-closed resume

- `AgentLlm:ResumeEnforcement` = `open` (default) | `strict`
  (`AgentLlm:StrictResume=true` is the legacy alias), and/or
  `resume_strict=true` in the org policy (org override beats global).
- In `strict`: resuming a run whose tool contract lost tools emits an
  `agent_diagnostic` `code=resume_drift`, `severity=error` and the run
  ends `Error` — fail-closed. **Added** tools do not block.
- In `open` (default): the same drift emits `resume_drift` with
  `severity=warning` and continues (pre-SPEC-48 behaviour).

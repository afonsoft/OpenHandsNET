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

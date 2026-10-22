# Custom Sandbox Images (SPEC-17)

Every sandbox runs from a Docker image chosen by the blueprint/spec.
To have the **in-container HTTP agent** (`/exec`, `/files`,
deferred-init), the image must derive from the runtime base — parity
with upstream custom images.

## Rules (compatible with `docs.openhands.dev/usage/custom-sandbox-image`)

1. **Never override the `ENTRYPOINT`** — the base image runs
   `tini -- openhandsnet-sandbox-agent`. Extend with `RUN`/`ENV`.
2. **Never bake secrets in** — credentials arrive via env on
   create/init.
3. Repo setup belongs to the blueprint (`openhandsnet.yaml` /
   `.openhands/setup.sh`), not the image.
4. The agent listens on `:8000` — the host reaches it via the bridge IP
   (default) or `Sandbox:PublishAgentPort=true` (Docker Desktop /
   remote daemon).

```dockerfile
FROM ghcr.io/openhandsnet/runtime:latest
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
```

An image **without** the agent works in legacy mode: the host drives the
container via `docker exec` (`sleep infinity` entrypoint). Detection is
by prefix — `Sandbox:AgentImagePrefixes` (default
`openhandsnet/runtime`).

## Repo blueprint

`openhandsnet.yaml` at the repo root (all fields optional):

```yaml
image: ghcr.io/openhandsnet/runtime:latest
sdk: [ "dotnet:10", "node:22", "python:3.13", "chrome:stable" ]
env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
setup: .openhands/setup.sh   # default
```

- `sdk` runs the pinned `/opt/ohn/sdks/{name}.sh` installers (dotnet,
  node, python, java, go, **chrome**) — passwordless `sudo` restricted
  to them.
- No `sdk:` → **auto-detect** from manifests (`*.csproj`,
  `package.json`, `requirements.txt`, `pyproject.toml`, `pom.xml`,
  `go.mod`, `global.json`, `.nvmrc`).
- `setup` runs as `openhands` in `/workspace/repo` with
  `DEBIAN_FRONTEND=noninteractive`.
- `env` → `/etc/profile.d/ohn-blueprint.sh` + `spec.Env` on the next
  start.
- `image` → applies on the conversation's next sandbox create.
- `.openhands/setup.sh` also works **without** `openhandsnet.yaml`
  (upstream convention).

Blueprint precedence: **repo `openhandsnet.yaml` → org
`settings.blueprint` (inline YAML) → auto-detect**. A blueprint failure
doesn't kill the conversation — the sandbox is left
`blueprint_status=failed` with the last 4 KB of the log at
`GET /api/v1/sandboxes/{id}/blueprint-log`.

## Building the base image

```bash
./runtime/build-runtime.sh ghcr.io/openhandsnet/runtime:0.1.0
docker push ghcr.io/openhandsnet/runtime:0.1.0
```

The image is multi-stage: publishes `openhandsnet-sandbox-agent`
(single-file self-contained) and installs over `ubuntu:24.04` — git,
gh, git-lfs, python3, node 22, build toolchain, `tini` as init.

## Chrome SDK — Chrome for Testing + chrome-devtools-mcp

`sdk: ["chrome:stable"]` (optional channel: `stable|beta|dev|canary`)
installs Chrome for Testing (arch-aware binary via `@puppeteer/browsers`),
the runtime deps and a **global `chrome-devtools-mcp`** (no npx/network
at conversation start). Requires Node ≥ 22.12 (the runtime image ships
22.x). The binary is exposed at `/usr/local/bin/chrome` (stable symlink).

MCP config for the conversation (stdio runs **inside** the sandbox via
`docker exec` — see `docs/specs/07_mcp_integration_spec.md`):

```json
{
  "name": "chrome-devtools",
  "transport": "stdio",
  "command": "chrome-devtools-mcp",
  "args": ["--headless", "--isolated",
           "--chrome-arg=--no-sandbox",
           "--executable-path=/usr/local/bin/chrome"],
  "env": { "CHROME_DEVTOOLS_MCP_NO_USAGE_STATISTICS": "1",
           "CHROME_DEVTOOLS_MCP_NO_UPDATE_CHECKS": "1" },
  "timeoutSeconds": 120
}
```

- `--chrome-arg=--no-sandbox`: required on plain Docker (no user
  namespaces); under sysbox (`Sandbox:DockerRuntime=sysbox-runc` /
  `runtimeClassName`) it can be omitted.
- `--isolated` uses a temporary profile per session (avoids `Target
  closed` and cookie leakage between conversations).
- Egress `deny`/`allowlist`: the MCP is preinstalled — no extra routes
  needed for the server itself; visited pages follow the sandbox's
  normal egress.

## Per-image warm pool

`Sandbox:WarmPoolSize` covers the default image;
`Sandbox:WarmPoolImages` (`imgA:2;imgB:1`) creates extra pools per
image. Agent-image containers are born **dormant**
(`OHN_DEFERRED_INIT=true` + `OHN_INIT_API_KEY` on the
`openhandsnet.init_key` label); the claim activates them via
`POST /api/init` with a fresh session key — parity with upstream
deferred-init.

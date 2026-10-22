# Docker-in-Sandbox & Egress (SPEC-14 RF-002)

Both features are **opt-in and off by default** — enabling either weakens
isolation. Do not enable them in shared/production environments without a
threat-model review.

## Docker-in-Docker — pick a mechanism

| Mechanism | Isolation | Requirement |
|---|---|---|
| `DockerRuntime=sysbox-runc` (SPEC-86) | **strong** — userns remapping, no privileged, daemon is a child of the sandbox | sysbox installed on the host; kernel ≥ 6.3 recommended |
| `EnableDinD` | medium — privileged `docker:27-dind` **sidecar** | none |
| `DockerSocketMount` | **weakest** — full control of the host daemon | none |

### Sysbox runtime (`Sandbox__DockerRuntime`, SPEC-86)

With [Sysbox](https://github.com/nestybox/sysbox) installed on the host,
sandboxes run under `sysbox-runc`: in-container "root" maps to a regular
host user and the agent can start `dockerd` **inside its own sandbox** —
no sidecar, no `privileged`. Same posture as the k8s provider
(`runtimeClassName`) and upstream enterprise.

```bash
OPENHANDSNET_Sandbox__DockerRuntime=sysbox-runc
# keep EnableDinD OFF — not needed with sysbox
```

Verify with `docker inspect -f '{{.HostConfig.Runtime}}' <sandbox>`.
Host kernels older than 6.3 may hit sysbox failures — prefer Ubuntu
24.04+ (upstream's hardened-runtime requirement).

### DinD sidecar (`Sandbox__EnableDinD`)

When on, each sandbox gets a privileged `docker:27-dind` **sidecar**
sharing the sandbox network namespace. The agent sees
`DOCKER_HOST=tcp://localhost:2375` — containers spawned inside the sandbox
stay siblings of the sandbox (never siblings of the host daemon).

```bash
OPENHANDSNET_Sandbox__EnableDinD=true
```

Costs: a second privileged container per sandbox, ~150 MB image, and the
sidecar itself is `privileged` — treat `EnableDinD` as "the agent is
root-equivalent inside its pod" (acceptable for dev; review for prod).

### Weaker alternative: socket mount

```bash
OPENHANDSNET_Sandbox__DockerSocketMount=true   # ignored when DinD is on
```

Bind-mounts the host `/var/run/docker.sock` — **weakest isolation**: the
sandbox gains full control of the host daemon (can start privileged
containers, read all workspaces). Only for single-user dev boxes. Never
set this together with untrusted repositories.

## Egress policy (`Sandbox__EgressMode`)

| Mode | Behaviour |
|---|---|
| `unrestricted` (default) | standard bridge — full outbound |
| `allowlist` | isolated internal network + Squid proxy sidecar (SPEC-19) |
| `deny` | isolated internal network — no outbound route at all |

See `egress-allowlist.md` for the allowlist proxy mechanics.

```bash
OPENHANDSNET_Sandbox__EgressMode=deny
# or:
OPENHANDSNET_Sandbox__EgressMode=allowlist
OPENHANDSNET_Sandbox__EgressAllow=api.github.com;*.internal.corp
```

Egress `deny` breaks `git clone` from public hosts and LLM calls routed
through the sandbox — combine with `host` clone mode or an internal
mirror.

## Warm pool (`Sandbox__WarmPoolSize`)

```bash
OPENHANDSNET_Sandbox__WarmPoolSize=2
```

Keeps N pre-created, running containers labelled `openhandsnet.pool=warm`.
A conversation start **claims** one (attach ~sub-second vs multi-second
cold start); its pre-mounted workspace dir is renamed to the conversation
path — bind target preserved. The pool:

- only serves the **default spec** (custom specs always cold-start)
- replenishes one slot per claim on a 15 s tick
- prunes free containers on shutdown/restart — claimed ones keep serving
- reports `pool: "warm"` on the sandbox DTO

Workspace binds make claimed warm containers identical to cold ones; the
only difference is start latency.

Upstream reference: <https://docs.openhands.dev/enterprise/docker-in-sandbox>

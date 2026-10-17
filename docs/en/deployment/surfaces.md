# Public sandbox surfaces — web terminal and VS Code (SPEC-40)

Surfaces are web processes running **inside** the sandbox, exposed to the
user through the Api host-router with a preview token. All opt-in — no
extra processes run by default.

## Image/runtime flags

| Flag | Where | Surface | Port | Protocol |
|---|---|---|---|---|
| `SANDBOX_TERMINAL=1` | container env **or** blueprint/`/api/init` env | `terminal` (ttyd) | 7681 | ws |
| `SANDBOX_VSCODE=1` | idem | `vscode` (code-server) | 8080 | http |

- **ttyd**: pinned static binary (`TTYD_VERSION` in
  `runtime/Dockerfile`) ships in the image.
- **code-server**: **outside** the image (+~200MB). When the flag is on
  and the binary is missing, the agent runs
  `/opt/ohn/sdks/code-server.sh` once (pinned `CODE_SERVER_VERSION`,
  default 4.141.0) installing to `/opt/ohn/code-server`.
- Supervisor: respawn with backoff up to 10 attempts; on/off is logged
  as a `sandbox_surface` event in the agent's `/activity` ring.

In the blueprint (`openhandsnet.yaml` / init env):

```yaml
env:
  SANDBOX_TERMINAL: "1"
  SANDBOX_VSCODE: "1"
```

## Publish flow

```
client → GET /api/v1/app-conversations/{id}/surfaces
       → [{name, protocol, port, url, expires_at}]
       → url = SandboxUrlBuilder + ?token=<HMAC preview token>
```

- `RoutingMode=host` → `https://{sandboxId}-{port}.{PublicBaseDomain}?token=…`
- `RoutingMode=path` → `/sandbox/{id}/port/{port}?token=…`
- `RoutingMode=internal` → `url: null` (surface unreachable).

## Wildcard DNS (host mode)

`host` mode requires wildcard `*.{PublicBaseDomain}` → Api, since the
router extracts `{id}-{port}` from the Host label. In dev, use
`RoutingMode=path` (no wildcard needed).

## Preview token (RF-004)

- `POST /api/v1/sandboxes/{id}/preview-token {port}` →
  `{token, expires_at}` — manual mint also exists.
- Stateless HMAC: `{sandbox_id, port, exp≤15min, nonce}`; middleware
  validates **before** proxying — surfaces without their own auth
  (ttyd/code-server run `--auth none`) stay protected at the entry.
- `?token=` only applies to the first request; the surface's internal
  WS/cookies continue on the authenticated proxy.
- Secret: `Sandbox:PreviewTokenSecret` (set it in production — without
  it the Api uses an ephemeral key and tokens don't survive restart).

## Security

- Surfaces bind `0.0.0.0` **inside** the container — reachable only on
  the sandbox's internal network; all public entry goes through the
  router.
- SPEC-38 SSRF guard maintained: `{guid:N}-{port}` labels only.
- Egress allowlist (SPEC-19) unchanged.
- Diagnostics: `GET /activity` on the agent lists `sandbox_surface`
  (`started|stopped|install`) per surface.

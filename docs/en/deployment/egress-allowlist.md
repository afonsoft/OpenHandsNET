# Egress Domain Allowlist (SPEC-19)

Mode `Sandbox:EgressMode=allowlist` releases **only** the domains in
`Sandbox:EgressAllow` — everything else is blocked by the proxy.

## How it works

```
bridge default (outbound ok)            sandcastle-egress-isolated (internal)
┌────────────────────────┐              ┌──────────────────────────────┐
│ sandcastle-egress-proxy│  connected  │  sandbox A  ──HTTP_PROXY──┐  │
│  (squid :3128)          │◄───────────►│  sandbox B  ──HTTPS_PROXY─┘  │
└────────────────────────┘              └──────────────────────────────┘
```

- Sandboxes in `allowlist` mode are born on the **internal** network
  (`sandcastle-egress-isolated`) — no outbound route.
- A **shared per-instance** Squid proxy (`sandcastle-egress-proxy`)
  is attached to both networks: controlled egress for the sandboxes.
- The sandbox gets `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` pointing to
  `http://sandcastle-egress-proxy:3128` (resolved via embedded DNS).
- `dstdomain` ACL: each entry becomes `.domain.tld` (covers subdomains).
  `CONNECT` is limited to port 443; everything else is `deny`
  (**fail-closed**).

## Configuration

```bash
SANDCASTLE_Sandbox__EgressMode=allowlist
SANDCASTLE_Sandbox__EgressAllow="github.com;nuget.org;pypi.org;registry.npmjs.org"
# optional:
# SANDCASTLE_Sandbox__EgressProxyImage=ubuntu/squid:6.6-24.04_beta
# SANDCASTLE_Sandbox__EgressConfigDir=/var/lib/sandcastle/egress
```

`squid.conf` + `allowlist.txt` are generated in `EgressConfigDir`
(default `{WorkspaceRoot}/../egress`) on every start — changing the
list recreates the proxy automatically (config hash on the container
label).

## Fail-closed

If the proxy cannot be created/started, the sandbox start **fails** —
`allowlist` mode never degrades to open egress. Readiness exposes
`checks.egress_proxy` (`sandcastle doctor` shows the probe).

## Limitations

- Allowlist is **instance-wide** (not per org/conversation).
- Only HTTP/HTTPS go through the proxy — UDP/ICMP stay blocked.
- TLS is tunnelled (CONNECT) — Squid filters by CONNECT host/SNI,
  no content inspection.
- Warm pool doesn't apply in `allowlist` (proxy env is immutable after
  create) — sandboxes cold-start.

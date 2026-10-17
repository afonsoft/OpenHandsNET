# Egress Domain Allowlist (SPEC-19)

Mode `Sandbox:EgressMode=allowlist` releases **only** the domains in
`Sandbox:EgressAllow` — everything else is blocked by the proxy.

## How it works

```
bridge default (outbound ok)            openhandsnet-egress-isolated (internal)
┌────────────────────────┐              ┌──────────────────────────────┐
│ openhandsnet-egress-proxy│  connected  │  sandbox A  ──HTTP_PROXY──┐  │
│  (squid :3128)          │◄───────────►│  sandbox B  ──HTTPS_PROXY─┘  │
└────────────────────────┘              └──────────────────────────────┘
```

- Sandboxes in `allowlist` mode are born on the **internal** network
  (`openhandsnet-egress-isolated`) — no outbound route.
- A **shared per-instance** Squid proxy (`openhandsnet-egress-proxy`)
  is attached to both networks: controlled egress for the sandboxes.
- The sandbox gets `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` pointing to
  `http://openhandsnet-egress-proxy:3128` (resolved via embedded DNS).
- `dstdomain` ACL: each entry becomes `.domain.tld` (covers subdomains).
  `CONNECT` is limited to port 443; everything else is `deny`
  (**fail-closed**).

## Configuration

```bash
OPENHANDSNET_Sandbox__EgressMode=allowlist
OPENHANDSNET_Sandbox__EgressAllow="github.com;nuget.org;pypi.org;registry.npmjs.org"
# optional:
# OPENHANDSNET_Sandbox__EgressProxyImage=ubuntu/squid:6.6-24.04_beta
# OPENHANDSNET_Sandbox__EgressConfigDir=/var/lib/openhandsnet/egress
```

`squid.conf` + `allowlist.txt` are generated in `EgressConfigDir`
(default `{WorkspaceRoot}/../egress`) on every start — changing the
list recreates the proxy automatically (config hash on the container
label).

## Fail-closed

If the proxy cannot be created/started, the sandbox start **fails** —
`allowlist` mode never degrades to open egress. Readiness exposes
`checks.egress_proxy` (`openhandsnet doctor` shows the probe).

## Limitations

- Allowlist is **instance-wide** (not per org/conversation).
- Only HTTP/HTTPS go through the proxy — UDP/ICMP stay blocked.
- TLS is tunnelled (CONNECT) — Squid filters by CONNECT host/SNI,
  no content inspection.
- Warm pool doesn't apply in `allowlist` (proxy env is immutable after
  create) — sandboxes cold-start.

# SPEC — 19 · Egress Domain Allowlist (proxy sidecar)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `egress-domain-allowlist` |
| Type | `Feature / Infra` |
| Stack | `.NET 10 · Docker.DotNet · Squid sidecar` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` (autopilot — gap list) |
| Ticket | [#43](https://github.com/afonsoft/OpenHandsNET/issues/43) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `14_enterprise_operations_spec` (RF-002 egress modes) |
| Touches | `DockerNetworkPolicy`, `DockerSandboxService`, `SandboxOptions`, `WarmPoolService` |

## 1. User Story

**As a** admin de uma instância OpenHandsNET
**I want** que `Sandbox:EgressMode=allowlist` libere **só os domínios listados**
em `Sandbox:EgressAllow`
**So that** agentes possam clarear deps (github.com, nuget.org) sem abrir
egress irrestrito — hoje o modo bloqueia **tudo** (coarse).

**Problem context:**

- `DockerNetworkPolicy` cria a rede `openhandsnet-egress-isolated`
  (`internal:true` — sem rota de saída) e só propaga `EGRESS_ALLOWLIST` como
  env **informativo** (`DockerNetworkPolicy.cs:75-89`). Nenhum domínio passa —
  o allowlist é um deny disfarçado (limitação documentada em SPEC-14).
- DNS numa rede `internal` continua funcionando (embedded DNS do Docker
  encaminha ao host) — a saída bloqueada é só IP/forwarding. Um proxy
  dual-homed resolve sem mexer em iptables.

## 2. Scope

**In scope:**

- **RF-001 — Proxy sidecar compartilhado**: container `squid` (imagem
  `ubuntu/squid`, pinada por tag/digest em `Sandbox:EgressProxyImage`,
  default `ubuntu/squid:6.13-24.10`) **um por instância**, conectado a duas
  redes: `openhandsnet-egress-isolated` (internal — alcance dos sandboxes) e
  a bridge default (saída real). Labels `openhandsnet.managed=true`,
  `openhandsnet.owner`, `openhandsnet.role=egress-proxy`. Restart policy
  `unless-stopped`; `EnsureProxyAsync` cria/inicia se ausente/parado.
- **RF-002 — Config gerada**: `squid.conf` + `allowlist.txt` materializados em
  `{Sandbox:EgressConfigDir}` (default `{WorkspaceRoot}/../egress`) a cada
  start/troca de config; `http_access allow` só para `dstdomain` da lista +
  `CONNECT` limitado a port 443 nos domínios permitidos; tudo o mais `deny`.
  Recarga via recriação do container (conf imutável de bind — barato, um por
  instância).
- **RF-003 — Injeção no sandbox**: modo `allowlist` + proxy vivo → env
  `HTTP_PROXY`/`HTTPS_PROXY`/`http_proxy`/`https_proxy` =
  `http://{proxy-ip-na-isolated}:3128` + `NO_PROXY=localhost,127.0.0.1`.
  Modo `deny` → sem proxy (segue tudo bloqueado). Modo `unrestricted` → nada.
- **RF-004 — Fail-closed**: proxy inalcançável/falha no start com
  `EgressMode=allowlist` → `StartAsync` falha com erro claro **em vez** de
  subir sandbox sem enforcement (deny implícito — nunca cai para open).
- **RF-005 — Warm pool**: containers warm nascem sem proxy env; no claim, se o
  modo for `allowlist`, o claim falha → fallback cold (documentado — env de
  processo é imutável pós-create).

**Out of scope:**

- Allowlist **por org/conversa** (v1 = allowlist instance-wide de
  `Sandbox:EgressAllow`; per-tenant exigiria ACL por src-IP — registrado como
  limitação).
- MITM/inspeção TLS — CONNECT é tunelado; squid filtra por SNI/host do CONNECT.
- Egress UDP/ICMP (sempre bloqueados no internal net; documentado).

## 3. Design

```text
┌──────────── bridge default (outbound ok) ──────────┐
│  egress-proxy (squid :3128)                        │
└──────┬─────────────────────────────────────────────┘
       │ docker network connect (dual-homed)
┌──────┴──── openhandsnet-egress-isolated (internal) ┐
│  sandbox A ──HTTP_PROXY──► proxy                   │
│  sandbox B ──HTTPS_PROXY─► proxy                   │
└────────────────────────────────────────────────────┘
```

- DNS externo resolve dentro do internal net (embedded DNS) → o sandbox
  conecta ao proxy por IP/nome do container (`openhandsnet-egress-proxy`).
- `DockerNetworkPolicy` ganha `EnsureProxyAsync` + `ProxyEnvAsync` —
  `DockerSandboxService.StartAsync` chama quando `EgressMode==allowlist`.
- squid.conf mínimo auditável: `http_port 3128`, `acl localnet`,
  `acl allowed dstdomain "/etc/squid/allowlist.txt"`,
  `acl CONNECT method CONNECT`, `acl SSL_ports port 443`,
  `http_access allow allowed`, `http_access allow CONNECT SSL_ports allowed`,
  `http_access deny all`, `dns_v4_first on`.

## 4. API Contract

Sem mudança de API. `Sandbox:EgressAllow` (vírgula-separado, sufixo `.dom.com`
cobre subdomínios — sintaxe squid) ganha efeito real em `allowlist`.
`openhandsnet doctor` ganha probe `egress_proxy` (ausente quando
modo!=allowlist).

## 5. Critérios de Aceite

- [ ] `allowlist` + `EGRESS_ALLOW=github.com` → `curl https://github.com` no
  sandbox passa pelo proxy (200); `https://example.org` → 403/timeout do
  proxy.
- [ ] `deny` continua sem qualquer saída; `unrestricted` sem proxy env.
- [ ] Proxy parado entre starts → `EnsureProxyAsync` religa; config nova →
  container recriado com conf atual.
- [ ] Falha de proxy → start falha fechado (nunca abre egress).
- [ ] `doctor` reporta `egress_proxy` healthy quando allowlist ativo.

## 6. NFR

- Um proxy por instância (não por sandbox) — overhead ~30 MB RAM.
- Conf de squid commitada em texto legível; nenhum secret em allowlist.
- Imagem pinada (tag+digest em docs); pull só no primeiro uso.

## 7. Task Plan

1. `SquidConfigWriter` (conf+allowlist em `{EgressConfigDir}`) + unit tests do
   template.
2. `DockerNetworkPolicy.EnsureProxyAsync`/`ProxyEnvAsync` (dual-network
   connect, restart, recreate-on-config-change) + unit tests c/ fake docker.
3. Wire `DockerSandboxService.StartAsync` (env injeção + fail-closed) e
   exclusão no warm claim.
4. `doctor` probe `egress_proxy`.
5. Integration test com squid real (opt-in `OHN_EGRESS_TESTS=1`): allow/deny.
6. Docs `docs/deployment/egress-allowlist.md` + `.env.exemplo` + index.

## 8. Deliverables

```text
src/OpenHandsNET.Infrastructure/Sandbox/EgressProxyService.cs
src/OpenHandsNET.Infrastructure/Sandbox/SquidConfigWriter.cs
src/OpenHandsNET.Infrastructure/Sandbox/DockerNetworkPolicy.cs   # proxy branches
src/OpenHandsNET.Infrastructure/Sandbox/SandboxOptions.cs        # EgressProxyImage/ConfigDir
cli/OpenHandsNET.Cli/Services/DoctorService.cs                   # probe
tests/OpenHandsNET.UnitTests/SandboxOps/EgressProxyTests.cs
tests/OpenHandsNET.IntegrationTests/EgressAllowlistTests.cs      # opt-in docker
docs/deployment/egress-allowlist.md
```

## 9. Referências

- SPEC-14 RF-002 (modos de egress + rede internal existente).
- http://www.squid-cache.org/Doc/config/acl/ — `dstdomain`, `CONNECT`, `SSL_ports`.
- Docker internal networks: embedded DNS resolve externo, forwarding bloqueado
  (docs.docker.com/network/drivers/bridge).

## 10. Guardrails

- Fail-closed: nunca degradar `allowlist` para `unrestricted` silenciosamente.
- Proxy é infra compartilhada — nunca carregar secrets de conversa nele.
- Sem alterar `.github/workflows/`.

## 11. DoD

- [ ] Allowlist real funciona E2E; deny/unrestricted inalterados.
- [ ] `dotnet build` + `dotnet test` verdes; CI do PR verde.
- [ ] Limitações (per-org, UDP) documentadas na SPEC + docs.

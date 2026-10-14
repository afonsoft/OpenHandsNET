# Egress Domain Allowlist (SPEC-19)

Modo `Sandbox:EgressMode=allowlist` libera **somente** os domínios de
`Sandbox:EgressAllow` — os demais saem bloqueados pelo proxy.

## Como funciona

```
bridge default (outbound ok)            openhandsnet-egress-isolated (internal)
┌────────────────────────┐              ┌──────────────────────────────┐
│ openhandsnet-egress-proxy│  conectado  │  sandbox A  ──HTTP_PROXY──┐  │
│  (squid :3128)          │◄────────────►│  sandbox B  ──HTTPS_PROXY─┘  │
└────────────────────────┘              └──────────────────────────────┘
```

- Sandboxes em modo `allowlist` nascem na rede **internal**
  (`openhandsnet-egress-isolated`) — sem rota de saída.
- Um proxy Squid **compartilhado por instância** (`openhandsnet-egress-proxy`)
  é conectado às duas redes: dá saída controlada aos sandboxes.
- O sandbox recebe `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` apontando para
  `http://openhandsnet-egress-proxy:3128` (nome resolvido via DNS embutido).
- ACL `dstdomain`: cada entrada vira `.dominio.tld` (cobre subdomínios).
  `CONNECT` é limitado à porta 443; tudo o mais é `deny` (**fail-closed**).

## Configuração

```bash
OPENHANDSNET_Sandbox__EgressMode=allowlist
OPENHANDSNET_Sandbox__EgressAllow="github.com;nuget.org;pypi.org;registry.npmjs.org"
# opcional:
# OPENHANDSNET_Sandbox__EgressProxyImage=ubuntu/squid:6.6-24.04_beta
# OPENHANDSNET_Sandbox__EgressConfigDir=/var/lib/openhandsnet/egress
```

`squid.conf` + `allowlist.txt` são gerados em `EgressConfigDir`
(default `{WorkspaceRoot}/../egress`) a cada start — mudança na lista recria
o proxy automaticamente (hash da config na label do container).

## Fail-closed

Se o proxy não puder ser criado/iniciado, o start do sandbox **falha** —
o modo `allowlist` nunca degrada para egress aberto. O readiness expõe
`checks.egress_proxy` (`openhandsnet doctor` mostra o probe).

## Limitações

- Allowlist é **instance-wide** (não por org/conversa).
- Só HTTP/HTTPS passam pelo proxy — UDP/ICMP continuam bloqueados.
- TLS é tunelado (CONNECT) — o Squid filtra pelo host do CONNECT/SNI,
  sem inspeção de conteúdo.
- Warm pool não se aplica em `allowlist` (env de proxy é imutável
  pós-create) — sandboxes fazem cold start.

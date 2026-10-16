# Surfaces públicas do sandbox — terminal web e VS Code (SPEC-40)

Surfaces são processos web que rodam **dentro** do sandbox e são expostos
ao usuário através do host-router da Api com preview token. Tudo opt-in —
nenhum processo extra roda por default.

## Flags da imagem/runtime

| Flag | Onde | Surface | Porta | Protocolo |
|---|---|---|---|---|
| `SANDBOX_TERMINAL=1` | env do container **ou** env do blueprint/`/api/init` | `terminal` (ttyd) | 7681 | ws |
| `SANDBOX_VSCODE=1` | idem | `vscode` (code-server) | 8080 | http |

- **ttyd**: binário estático pinned (`TTYD_VERSION` no `runtime/Dockerfile`)
  já vai na imagem.
- **code-server**: **fora** da imagem (+~200MB). Quando a flag liga e o
  binário falta, o agente roda uma vez
  `/opt/ohn/sdks/code-server.sh` (pinned `CODE_SERVER_VERSION`, default
  4.141.0) e instala em `/opt/ohn/code-server`.
- Supervisor: respawn com backoff até 10 tentativas; liga/desliga é
  registrado como evento `sandbox_surface` no ring `/activity` do agente.

No blueprint (`openhandsnet.yaml` / env do init):

```yaml
env:
  SANDBOX_TERMINAL: "1"
  SANDBOX_VSCODE: "1"
```

## Fluxo de publicação

```
client → GET /api/v1/app-conversations/{id}/surfaces
       → [{name, protocol, port, url, expires_at}]
       → url = SandboxUrlBuilder + ?token=<preview token HMAC>
```

- `RoutingMode=host` → `https://{sandboxId}-{port}.{PublicBaseDomain}?token=…`
- `RoutingMode=path` → `/sandbox/{id}/port/{port}?token=…`
- `RoutingMode=internal` → `url: null` (surface não alcançável).

## DNS wildcard (modo host)

O modo `host` exige wildcard `*.{PublicBaseDomain}` → Api, pois o router
extrai `{id}-{port}` do label do Host. Em dev, use `RoutingMode=path`
(não precisa de wildcard).

## Preview token (RF-004)

- `POST /api/v1/sandboxes/{id}/preview-token {port}` →
  `{token, expires_at}` — mint manual também existe.
- Stateless HMAC: `{sandbox_id, port, exp≤15min, nonce}`; o middleware
  valida **antes** de proxear — surfaces sem auth própria (ttyd/code-server
  rodam `--auth none`) ficam protegidas pela porta de entrada.
- `?token=` vale só na primeira request; WS/cookies internos da surface
  seguem no proxy autenticado.
- Secret: `Sandbox:PreviewTokenSecret` (configure em produção — sem ele a
  Api usa chave efêmera e tokens não sobrevivem a restart).

## Segurança

- Surfaces bind `0.0.0.0` **dentro** do container — alcançáveis só pela
  rede interna do sandbox; toda entrada pública passa pelo router.
- SSRF guard da SPEC-38 mantido: labels `{guid:N}-{porta}` apenas.
- Egress allowlist (SPEC-19) inalterado.
- Diagnóstico: `GET /activity` no agente lista `sandbox_surface`
  (`started|stopped|install`) por surface.

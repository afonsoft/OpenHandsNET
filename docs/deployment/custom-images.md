# Custom Sandbox Images (SPEC-17)

Todo sandbox roda a partir de uma imagem Docker escolhida pelo blueprint/spec.
Para ter o **agente HTTP in-container** (`/exec`, `/files`, deferred-init), a
imagem deve derivar da runtime base — paridade com os custom images upstream.

## Regras (compatíveis com `docs.openhands.dev/usage/custom-sandbox-image`)

1. **Nunca sobrescreva o `ENTRYPOINT`** — a imagem base sobe
   `tini -- openhandsnet-sandbox-agent`. Estenda com `RUN`/`ENV`.
2. **Nunca embuta secrets** — credenciais chegam via env no create/init.
3. Setup de repo pertence ao blueprint (`openhandsnet.yaml` /
   `.openhands/setup.sh`), não à imagem.
4. O agente escuta em `:8000` — o host o alcança pelo IP da bridge (default)
   ou via `Sandbox:PublishAgentPort=true` (Docker Desktop / daemon remoto).

```dockerfile
FROM ghcr.io/openhandsnet/runtime:latest
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
```

Uma imagem que **não** traz o agente funciona em modo legado: o host dirige o
container por `docker exec` (`sleep infinity` entrypoint). A detecção é por
prefixo — `Sandbox:AgentImagePrefixes` (default `openhandsnet/runtime`).

## Blueprint do repositório

`openhandsnet.yaml` na raiz do repo (todos os campos opcionais):

```yaml
image: ghcr.io/openhandsnet/runtime:latest
sdk: [ "dotnet:10", "node:22", "python:3.13", "chrome:stable" ]
env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
setup: .openhands/setup.sh   # default
```

- `sdk` roda os installers pinados `/opt/ohn/sdks/{name}.sh` (dotnet, node,
  python, java, go, **chrome**) — passwordless `sudo` restrito a eles.
- sem `sdk:` → **auto-detect** por manifestos (`*.csproj`, `package.json`,
  `requirements.txt`, `pyproject.toml`, `pom.xml`, `go.mod`, `global.json`,
  `.nvmrc`).
- `setup` roda como `openhands` em `/workspace/repo` com
  `DEBIAN_FRONTEND=noninteractive`.
- `env` → `/etc/profile.d/ohn-blueprint.sh` + `spec.Env` no próximo start.
- `image` → aplica no próximo create de sandbox da conversa.
- `.openhands/setup.sh` funciona também **sem** `openhandsnet.yaml`
  (convenção upstream).

Precedência do blueprint: **repo `openhandsnet.yaml` → org `settings.blueprint`
(YAML inline) → auto-detect**. Falha de blueprint não derruba a conversa —
o sandbox fica `blueprint_status=failed` com a cauda (4 KB) do log em
`GET /api/v1/sandboxes/{id}/blueprint-log`.

## Build da imagem base

```bash
./runtime/build-runtime.sh ghcr.io/openhandsnet/runtime:0.1.0
docker push ghcr.io/openhandsnet/runtime:0.1.0
```

A imagem é multi-stage: publica `openhandsnet-sandbox-agent` (single-file
self-contained) e instala sobre `ubuntu:24.04` — git/gh/git-lfs, python3,
node 22, toolchain de build, `tini` como init.

## SDK chrome — Chrome for Testing + chrome-devtools-mcp

`sdk: ["chrome:stable"]` (canal opcional: `stable|beta|dev|canary`) instala
Chrome for Testing (binário arch-aware via `@puppeteer/browsers`), as
deps de runtime e o **`chrome-devtools-mcp` global** (sem npx/rede no
start da conversa). Requer Node ≥ 22.12 (a imagem runtime traz 22.x).
Binário exposto em `/usr/local/bin/chrome` (symlink estável).

Config MCP para a conversa (stdio roda **dentro** do sandbox via
`docker exec` — ver `docs/specs/07_mcp_integration_spec.md`):

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

- `--chrome-arg=--no-sandbox`: necessário em Docker comum (sem user
  namespaces); sob sysbox (`Sandbox:DockerRuntime=sysbox-runc` /
  `runtimeClassName`) pode omitir.
- `--isolated` cria profile temporário por sessão (evita `Target closed`
  e vazamento de cookies entre conversas).
- Egress `deny`/`allowlist`: o MCP já vem pré-instalado — nenhuma rota
  extra necessária para o servidor; páginas visitadas seguem o egress
  normal do sandbox.

## Warm pool por imagem

`Sandbox:WarmPoolSize` cobre a imagem default; `Sandbox:WarmPoolImages`
(`imgA:2;imgB:1`) cria pools extras por imagem. Containers de imagem-agente
nascem **dormant** (`OHN_DEFERRED_INIT=true` + `OHN_INIT_API_KEY` na label
`openhandsnet.init_key`); o claim ativa via `POST /api/init` com uma session
key nova — paridade com o deferred-init upstream.

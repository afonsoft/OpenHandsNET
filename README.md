# OpenHandsNET

Port open-source (MIT) do ecossistema **OpenHands** para **.NET 10 / C# 14 /
Blazor WebAssembly** — agente autônomo de engenharia de software com conceitos
Cloud + Enterprise e UX inspirada no Devin Web, sobre a base de
[`afonsoft/open-webui`](https://github.com/afonsoft/open-webui).

> Status: **produto funcional** — SPECs 01–47 entregues (agente, sandbox,
> API, UI completa, enterprise ops). SPECs 48–51 aprovadas fecham os gaps
> restantes de paridade enterprise (org security policies, k8s warm-pool +
> Helm, BYOR LiteLLM, contrato OpenAPI). English: [README.en.md](README.en.md).

## Visão

- **EventStream V1** no núcleo (`kind` discriminador, ULID, `parent_id`) —
  toda ação/observação do agente é um evento persistido e transmitido.
- **Sandbox por conversa** (Docker.DotNet / Kubernetes): workspace isolado,
  bash session, browser Playwright, file/git ops, sleep/wake, warm pool.
- **API `/api/v1` compatível com OpenHands V1** + realtime via SignalR
  (deltas efêmeros + durable events).
- **UI Devin-like** (Blazor WASM): chat streaming com reasoning, composer
  rico (@menções, /slash, uploads, drafts), IDE workbench (Monaco + SCM +
  busca), checkpoints com revert, attention inbox, i18n em 8 locales.
- **Enterprise open-source**: OIDC + API keys `sk-*`, orgs/RBAC,
  multi-tenancy, planos/quotas/billing, auditoria, egress allowlist,
  security analyzer, secrets by-reference, S3 artifacts.
- **Automação**: routines agendadas (interval/daily/weekly/once) + webhook
  triggers anônimos rate-limited + tool `routine` para o agente.
- **CLI** `openhandsnet`: login device-flow, `run` headless, `serve`,
  `doctor --preflight`, `eval`, TUI.

## Comparação

| Área | OpenHands (OSS) | OpenHands Cloud/Ent | **OpenHandsNET** | Devin Web |
|---|---|---|---|---|
| Licença | MIT | Polyform (fechado) | **MIT** | Fechado (SaaS) |
| Stack | Python + React | Python + k8s | **.NET 10 + Blazor WASM** | Proprietária |
| Modelo de eventos | V1 `kind`/ULID | V1 + delta events | **V1 completo + `streaming_delta`** | Proprietário |
| Sandbox | Docker | k8s + Sysbox multi-host | **Docker em prod; engine k8s — conclusão em SPEC-49** | Proprietário |
| Warm pool / sleep-wake | Parcial | Sim | **Docker sim; k8s em SPEC-49** | Sim |
| Roteamento LLM | Config estática | LiteLLM proxy | **OmniRoute catalog + roles; BYOR em SPEC-50** | Proprietário |
| Segurança | SecurityAnalyzer | IdP orgs + analyzer | **analyzer plugável + org policies (SPEC-48)** | Proprietário |
| Secrets | LookupSecret by-ref | Idem | **by-reference (`SecretNamesJson`)** | Vault interno |
| Memória | `.openhands/memory/` | Idem | **project + org scopes** | Knowledge/playbooks |
| Subagentes | task tool | Idem | **`task` + presets + depth cap** | Sim |
| Permissões | allow/deny/ask | Idem + org policy | **`allow/deny/ask` + plan mode; org em SPEC-48** | Proprietário |
| Artefatos | workspace | S3/GCS filestore | **workspace + S3 provider** | Proprietário |
| Deploy | docker run/compose | Helm + KOTS/k0s | **compose + Helm base; chart completo em SPEC-49** | SaaS apenas |
| Multi-réplica | — | Valkey/Redis backplane | **`IDistributedCache` + backplane SignalR** | Sim |
| UI | Agent Canvas (React) | Cloud UI | **Devin-like Blazor: chat, IDE, surfaces** | Referência de UX |
| CLI | `openhands` TUI | cloud mode | **`openhandsnet` parity + eval + doctor** | `devin` API |
| i18n | en | en | **8 locales (pt-BR default)** | en |
| Automações | webhooks GitHub | schedules internas | **routines + hook triggers + `routine` tool** | Devin automations |
| API keys | — | `sk-*` + org binding | **`sk-*` + org binding + BYOR** | Sim |
| Eval | benchmarks OSS | — | **`openhandsnet eval` + asserts + llm_judge** | — |

Legenda: **negrito** = estado atual do OpenHandsNET; "em SPEC-N" = aprovado,
roadmap imediato.

## Quickstart

Pré-requisito: .NET SDK 10 (`dotnet --version`).

```bash
dotnet build OpenHandsNET.slnx          # build (9 projetos)
dotnet test OpenHandsNET.slnx           # testes
dotnet run --project src/OpenHandsNET.Api   # app em http://localhost:8080
cp .env.exemplo .env && docker compose up -d --build   # container em :3032
```

Configuração por env vars com prefixo `OPENHANDSNET_` (ex.:
`OPENHANDSNET_Database__Provider=PostgreSql`). Banco default: SQLite em
`data/openhandsnet.db` (migrado no boot). Endpoints: `GET /api/v1/health`,
`/api/v1/ready`, `/api/v1/config`.

## Layout do repositório

```
.specs/             SPECs SDD (51 módulos — comece por .specs/README.md)
migration-plan/     research (api-inventory, domains, risks) + roadmap
docs/               guias (deployment, ui, api, features) — índice em docs/README.md
docs/en/            documentação em inglês
charts/openhandsnet Helm chart (k8s — chart completo na SPEC-49, aprovada)
deploy/             manifests auxiliares (litellm, otelcol, prometheus)
src/                Domain · Application · Infrastructure · Api · Client (WASM)
                    · Sandboxing · RuntimeApi · SandboxAgent · Eval
cli/                OpenHandsNET.Cli (Spectre.Console — SPEC-06/31)
tests/              UnitTests · IntegrationTests · Client.Tests (bUnit)
                    · SandboxAgent.Tests
```

## Documentação

- **[docs/README.md](docs/README.md)** — índice completo (pt-BR).
- **[docs/en/](docs/en/README.md)** — English docs.
- Destaques: [deployment/local](docs/deployment/local.md) ·
  [kubernetes](docs/deployment/kubernetes.md) ·
  [security](docs/deployment/security.md) ·
  [automations](docs/automations.md) · [cli](docs/cli.md) ·
  [eval](docs/eval.md) · [comparação detalhada](docs/comparison.md).

## Fontes

- Upstream MIT: `OpenHands/software-agent-sdk`, `OpenHands/OpenHands` (Canvas),
  `OpenHands/OpenHands-CLI`, `OpenHands/automation` (baselines em
  `docs/reference/upstream-baseline.md`).
- Upstream Polyform (conceitos apenas — **não portamos código**):
  `OpenHands/enterprise`, `OpenHands/OpenHands-Cloud`.

## Licença

MIT — implementação original. Marcas e conceitos de terceiros pertencem a seus
detentores.

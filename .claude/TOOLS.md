# TOOLS.md — Ferramentas e integrações

## Runtime / CLI

| Ferramenta | Uso |
|---|---|
| `dotnet` 10 SDK | build/test/format/ef — slnx: `OpenHandsNET.slnx` |
| `dotnet-sonarscanner` | análise SonarCloud (`afonsoft_OpenHandsNET`) — via CI ou local com `SONAR_TOKEN` |
| `sonar-scanner` | instalado em `~/.local/share/sonar-scanner/` (alternativo) |
| `gh` (autenticado `afonsoft`) | issues, PRs, labels, `gh api` (SHA de actions) |
| `docker` + `docker compose` | sandbox local, Testcontainers, compose profiles |
| `git` | conventional commits, squash merge via `gh pr merge --squash` |

## Comandos-chave

```bash
dotnet restore OpenHandsNET.slnx && dotnet build OpenHandsNET.slnx -c Release
dotnet test                                   # 851 testes (4 projetos)
dotnet format OpenHandsNET.slnx --verify-no-changes   # lint gate
dotnet ef migrations add <Name> --startup-project src/OpenHandsNET.Api \
  --project src/OpenHandsNET.Infrastructure --context AppDbContext
dotnet run --project src/OpenHandsNET.Api     # app em :8080
docker compose up -d --build                  # app em :3032
./scripts/check-env-drift.sh                  # gate CI de env drift
```

## MCP (instalados no ambiente Devin)

composio, context7, deepwiki, fetch, filesystem, firecrawl, graphify,
knowledge, n8n-mcp, notebooklm-mcp, rea, wordpress-* — usar conforme a
skill correspondente (ex.: context7 para docs de libs).

## Integrações do produto (evidência em código)

- LLM: Semantic Kernel, OmniRoute catalog, BYOR LiteLLM (SPEC-50)
- SCM: GitHub App (Octokit) + GitLab/Bitbucket/Azure DevOps REST
- ChatOps: Slack; Ticketing: Linear/Jira; IdP: SCIM 2.0
- Sandbox: Docker.DotNet, Kubernetes engine, Squid egress proxy,
  Playwright (worker), sandbox-agent próprio (PID 1 HTTP)

# SPEC-77: IDE workbench — VS Code web no sandbox (code-server)

| Campo | Valor |
|---|---|
| Status | In implementation |
| Gap key | GAP-architecture-ide-workbench |
| Type | UX/Infra |
| Fonte | direção do produto (afonsoft) + `SPEC-40` surfaces × `IdeTab` |

## User Story

Como usuário, quero um **VS Code web de verdade** rodando dentro do
container do sandbox, na mesma pasta do repo (`/workspace`), subindo
junto com o container e provisionado pelo blueprint — em vez de um IDE
próprio em Monaco que precisa re-implementar cada feature do VS Code.

## Decisão

A infraestrutura já existia (SPEC-40): surface `vscode` no sandbox-agent
(porta 8080, `code-server --auth none` + preview token HMAC na borda),
instalador lazy `/opt/ohn/sdks/code-server.sh` ("instala conforme
necessário"), endpoint `GET /app-conversations/{id}/surfaces`. Faltavam
default-on e o embed no workbench — é o que esta SPEC entrega.

code-server já traz **nativamente** o que seria SPEC-77 no Monaco:
explorer, outline/símbolos, bottom panel (terminal/output), SCM rico,
extensões, debugger — tornando IdeBottomPanel/IdeOutlineView/`/ide`
standalone desnecessários no curto prazo.

## Requisitos

1. **RF-001 — Sobe com o container**: `SandboxService` injeta
   `SANDBOX_VSCODE=1` no env de imagens-agente quando o blueprint não
   define a flag. Opt-out via `env: SANDBOX_VSCODE: "0"` no
   `openhandsnet.yaml`. Instalação lazy via `sdks/code-server.sh`
   (pinned `4.141.0`) — imagem não carrega ~200MB por default.
2. **RF-002 — Embed no workbench**: `IdeTab` ganha toggle na activity
   bar (ícone `code`, `aria-pressed`) que troca a área do editor para
   `<iframe>` do surface URL (preview token fresco a cada ativação,
   expira ~15min → refetch em `RefreshVscodeAsync`). Monaco workbench
   permanece como fallback quando a surface está ausente (sandbox
   legado/agente off).
3. **RF-003 — Blueprint provisiona repo**: tools via `sdks:` do
   blueprint (`/opt/ohn/sdks/*.sh` — node/python/dotnet/go/java) +
   `setup.sh` + `env:` já existentes; config do editor via
   `.vscode/settings.json`/`extensions.json` commitadas no repo —
   code-server lê nativamente. Sem contrato novo.
4. **RF-004 — Mantidos**: SCM dirty badge + zen mode (já entregues na
   primeira fatia).

## Critérios de aceite

- Sandbox com imagem-agente sobe com `vscode` em `GET …/surfaces` sem
  env extra; blueprint `SANDBOX_VSCODE=0` desliga.
- `IdeTab` mostra o toggle só quando a surface existe; iframe carrega
  `/workspace` com preview token.
- Build 0w/0e; bUnit cobre toggle + iframe + ausência de surface.

## Fora de escopo

Extensões code-server via blueprint (declarar `extensions:` no yaml),
`/ide` standalone fora de conversa, LSP/customizações do Monaco.

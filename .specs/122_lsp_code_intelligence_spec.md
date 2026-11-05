# SPEC — 122 · LSP code-intelligence tools

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `lsp-code-intelligence` |
| Type | `Feature` |
| Status | `Approved` |
| Ticket | #324 (Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Infrastructure/Agent/Tools/Lsp*` + `OpenHandsNET.SandboxAgent`/`Sandboxing` (language servers no runtime image) |

## 1. User Story

**As a** agente no sandbox, **I want** tools de inteligência de código
via Language Server Protocol (diagnostics, symbols, definition,
references, hover), **so that** eu navegue e altere código por
semântica em vez de grep textual — a capacidade que o OWUIC ganhou
com a suíte `lsp_*`.

## 2. Contexto / escopo

AS-IS: zero suporte LSP — o agente navega via `file_read`/`bash grep`.

OWUIC (`Infrastructure/Lsp/` + `ChatTools/Tools/LspBuiltinTools.cs`):
`lsp_diagnostics`, `lsp_symbols`, `lsp_workspace_symbols`,
`lsp_definition`, `lsp_references`, `lsp_hover` — client LSP gerido
pelo host que sobe language servers sob demanda no workspace.

Desafio no nosso contexto: o workspace do OHN vive **dentro do
sandbox** (container), não no host — então o LSP precisa rodar no
sandbox (runtime image já instala toolchains; verificar
`17_runtime_image_blueprint_spec`) ou via LSP-over-stdio túnel pelo
`Executor`. OWUIC roda no host com workspace montado — modelo mais
simples que o nosso.

Decisão de arquitetura a fixar na implementação (recomendada):
**language servers dentro do sandbox** (instalados na runtime image
ou on-demand por linguagem), acessados por um mini-cliente LSP no
`SandboxAgent`/executor; `BuiltinTools` só delega. Fallback:
`lsp_diagnostics` via compiler linters se LS indisponível.

Fora de escopo: code_search semântico/embeddings (vetoriza índice —
follow-up separado); edição refator-aware (rename symbol) — fase 2.

## 3. Requisitos

- RF-001 — **Infra LSP**: serviço `LspClientService` no sandbox side
  (spawn/kill por (language,root), JSON-RPC stdio, initialize/
  shutdown, idle-timeout 10 min, máx N=2 servers por sessão).
- RF-002 — **Runtime image**: blueprint instala LS por linguagem
  detectada (C# `csharp-ls`/`roslyn`, TS `typescript-language-server`,
  Py `pyright`, Go `gopls`, Rust `rust-analyzer` — mínimo TS+Py na
  fase 1); detecção por arquivos manifesto no workdir.
- RF-003 — **Tools**: `lsp_diagnostics` (severity, path filter),
  `lsp_symbols` (document symbols), `lsp_workspace_symbols` (query),
  `lsp_definition` (file:line:col → locations),
  `lsp_references` (incl. declaration opcional),
  `lsp_hover` (markdown docs + signature). Respostas compactas
  (uri relativo + range + preview ≤200 chars) — schemas no padrão
  `ToolSchema` atual; `Risky: false`.
- RF-004 — **Degradação**: sem LS para a linguagem → resposta
  estruturada `{supported:false, languages:[…]}` (o agente cai p/
  `file_grep` da SPEC-121); LS crash → 1 restart depois resposta de
  erro clara.
- RF-005 — **Prompt/contrib**: descrições ensinam quando usar
  (`lsp_*` > `file_grep` para navegação semântica) —
  `PromptContributors` atualizado.
- RF-006 — **Testes**: unit p/ parser de resposta LSP; integração
  Testcontainers com sandbox real subindo `typescript-language-server`
  e resolvendo `definition` num repo fixture.

## 4. Acceptance

- [ ] Repo TS fixture: `lsp_definition` e `lsp_references` retornam
      locations corretas dentro do sandbox.
- [ ] `lsp_diagnostics` lista erros de um arquivo .ts quebrado.
- [ ] Linguagem sem LS → `{supported:false}` sem falhar a run.
- [ ] Servers idle são encerrados (log prova shutdown após timeout).
- [ ] `dotnet test` verde; nenhum endpoint público novo.

## 5. API Contract

Tools seguem `AgentTool`/`ToolSchema` (nomes `lsp_*`, args
`path`/`line`/`character`/`query` conforme o tool). Nenhum endpoint
REST novo; eventos de diagnóstico de infra via
`AgentDiagnosticEvent` existente (`code: lsp_unavailable`).

## 6. Task Plan

1. `LspClientService` (spawn/JSON-RPC/lifecycle) + testes unit.
2. Runtime image: instalar TS+Py LS + detecção.
3. Os 6 tools + schemas + prompts.
4. Integração Testcontainers + docs (`docs/features/`).

## 7. Riscos / notas

- Peso da imagem: cada LS adiciona MB ao runtime image — instalação
  on-demand ( primeira chamada baixa/compila? ) é alternativa;
  documentar custo no blueprint.
- `csharp-ls` exige runtime .NET no sandbox — avaliar se vale fase 1
  (talvez fique para fase 2 com rust-analyzer/gopls).
- Segurança: LS é processo arbitrário no sandbox — coberto pelo
  modelo de isolamento existente (SPEC-16/69); nada novo exposto.

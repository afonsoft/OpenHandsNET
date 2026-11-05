# SPEC — 129 · MCP hardening (golden snapshot + E2E harness)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `mcp-hardening` |
| Type | `Tooling/QA` |
| Status | `In implementation` |
| Ticket | #334 (umbrella #329, Epic #317) — débito SPEC-124 |
| Scope | `src/OpenHandsNET.Mcp/`, `tests/` (MCP E2E), `tools/` (drift guard), `docs/mcp-server.md` |

## 1. User Story

**As a** mantenedor, **I want** drift-guard do catálogo MCP e teste E2E
do servidor stdio, **so that** mudanças acidentais em `tools/list` ou
quebra do dispatch JSON-RPC sejam pegas no CI/local antes do release —
o mesmo nível de guard dos outros catálogos (i18n, css).

## 2. Contexto / escopo

- SPEC-124 entregou `OpenHandsNET.Mcp` com 12 `ohn_*` tools + 26 testes
  de dispatch — mas **sem snapshot guard** do catálogo e sem E2E real
  (subprocesso stdio ↔ API mock).
- Repositório já tem padrão de drift guards em `tools/check-*.py` com
  baseline ratchet (`tools/drift-baseline/`).

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | `tools/check-mcp-tools-golden.py` — emite `tools/list` snapshot (nome + inputSchema ordenado) e compara com `tools/drift-baseline/mcp-tools.json`; `--write-baseline` atualiza. |
| RF-002 | Teste E2E: sobe `openhandsnet-mcp` como subprocesso com `OHN_API_URL` apontando para `HttpListener`/mock server in-process; roda `initialize`→`tools/list`→`tools/call`→resposta real da API mock. |
| RF-003 | Cobertura de erro real: API mock 401/500 → `tools/call` retorna `isError: true` com mensagem (fail-closed, sem payload fabricado). |
| RF-004 | Verificação manual documentada: `.mcp.json` com Claude Code (ou inspector MCP) conectando em `openhandsnet mcp serve` — passo a passo em `docs/mcp-server.md` com evidência. |
| RF-005 | Guard integrado ao workflow de verificação (mesmo padrão dos check-*.py — roda local e em CI quando houver). |

## 4. Acceptance

- [ ] `check-mcp-tools-golden.py` falha ao adicionar/remover/renomear tool sem atualizar baseline.
- [ ] E2E subprocesso: initialize → tools/list (12) → call real mockada → resposta correta.
- [ ] API indisponível → erro estruturado MCP (não crash/timeout).
- [ ] Docs com seção "verificação com cliente MCP".

## 5. API Contract

Snapshot = saída canônica de `tools/list` (JSON ordenado por nome).
Sem mudança de protocolo (MCP `2024-11-05`).

## 6. Task Plan

1. Flag/env no `McpServer` para dump de catálogo (ou extrair via dispatch em processo nos testes).
2. `check-mcp-tools-golden.py` + baseline inicial.
3. `McpE2ETests` — subprocesso + mock API (UnitTests ou IntegrationTests).
4. Atualizar `docs/mcp-server.md` + `CLAUDE.md` comandos (seção guards).

## 7. Riscos / notas

- Snapshot frágil se schema muda por release — documentar fluxo `--write-baseline` no PR template/checklist.
- E2E subprocesso em CI: garantir que `dotnet run`/dll publicada está disponível (ou compilar no teste com `dotnet build` prévio).

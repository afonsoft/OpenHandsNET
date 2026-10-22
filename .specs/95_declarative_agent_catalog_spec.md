# SPEC — 95 · Declarative agent catalog — *.agent.md + agent_catalog tool

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `declarative-agent-catalog` |
| Type | `Feature` |
| Stack | `.NET 10 · agent runtime · sandbox workspace` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | análise comparativa `afonsoft/open-webui` (2026-10-22) |
| Depends on | SPEC-90 (delegation-policy block consome o catálogo; sem ele o catálogo ainda funciona — só o bloco de prompt fica menor) |
| Reference | `open-webui` `src/OpenWebUI.Infrastructure/Services/AgentDefinitionService.cs` + `ChatTools/Tools/AgentCatalogBuiltinTool.cs` + `DelegateTaskBuiltinTool.cs` |
| Touches | `Application/Agent` (contratos), `Infrastructure/Agent/Subagents/`, `Infrastructure/Agent/Tools/BuiltinTools.cs` (`task`), `Infrastructure/Skills/RepoSkillProvider.cs` (padrão análogo), sandbox executor (leitura de arquivos do workspace), `tests` |

## 1. User Story

**As a** operador de um workspace com agentes declarativos
**I want** que o runtime descubra `*.agent.md` versionados no repo
(`.claude/agents/`, `.agents/agents/`, `.opencode/agents/`,
`.openhandsnet/agents/`) — em camadas user→repo — e os exponha via
tool `agent_catalog` + `subagent_type` do `task`
**So that** a delegação a especialistas (reviewer, tester, researcher)
seja dirigida por arquivos versionados com `name`/`description`/
`tools`/`model` no frontmatter, como já acontece com skills — hoje
`subagent_type` aceita um conjunto fixo interno e o agente não tem
como descobrir quais tipos existem no workspace.

**Contexto:** SPEC-28 entregou subagents (`task`/`task_status`/
`task_result` com `subagent_type`), mas o *catálogo* de tipos é
hard-coded/interno — o modelo chuta nomes. SPEC-25 (`RepoSkillProvider`)
já prova o padrão análogo para skills: lê `SKILL.md` de
`.agents/skills`, `.claude/skills` etc. do workspace e injeta como
contexto. O open-webui levou a simetria adiante: `AgentDefinitionService`
lê `*.agent.md` em camadas (embutido < user < data < workspace),
frontmatter `name`+`description` obrigatório, e expõe
`agent_catalog` (lista) + `delegate_task` (persona/agent/team/
`resume_chat_id`).

## 2. Escopo

In-scope:

- `AgentDefinition` record: `Name`, `Description`, `Tools`
  (allowlist opcional), `Model` (opcional), `Body` (instruções),
  `Source` (`builtin|user|repo|workspace`), `FilePath`.
- `IAgentCatalog`/`AgentCatalogService` (Infrastructure):
  - Descobre `*.agent.md` em: built-ins embutidos (`agents/*.agent.md`
    no assembly) < diretório de usuário do host (`~/.openhandsnet/agents`
    ou equivalente do deployment) < workspace do sandbox
    (`.openhandsnet/agents`, `.claude/agents`, `.agents/agents`,
    `.opencode/agents` — mesma família de dirs do `RepoSkillProvider`).
    Mesmo `name`: camada maior sobrepõe (documentar ordem).
  - Parser: frontmatter `---` YAML-lite (`name`, `description`,
    `tools`, `model`) + corpo markdown = instruções do agente;
    `name`/`description` obrigatórios — arquivo inválido é ignorado
    com `LogWarning` (nunca falha o run).
  - Caps: teto por arquivo (ex.: 64KB) e por catálogo (ex.: 64
    agentes) — defensivo contra repo hostil.
- `agent_catalog` builtin tool: lista `{name, description, source}`
  do catálogo efetivo para o modelo consultar antes de delegar.
- `task` tool: `subagent_type` validado contra o catálogo efetivo —
  tipos declarativos ganham `Body` como persona/instruções do subagent
  e `Tools`/`Model` quando suportados pelo `SubagentRunner`; tipos
  built-in atuais continuam funcionando (catálogo estende, não
  substitui); erro de tipo desconhecido lista os válidos.
- Segurança: `tools` do frontmatter **nunca** expande permissões além
  do `PermissionEvaluator` do run — allowlist só pode *restringir* o
  conjunto do subagent, documentado no SPEC e testado.
- Testes: parser (válido/inválido/sem frontmatter), overlay de
  camadas, caps, `agent_catalog` output, `task` com tipo declarativo,
  allowlist não escala permissão.

Out-of-scope: `team:true`/`resume_chat_id` (continuidade entre runs —
follow-up); UI de catálogo no Client; agents de org via DB;
marketplace/distribuição remota de agents.

## 3. Requisitos

1. **RF-001 — Descoberta em camadas**: ordem documentada builtin <
   user < workspace; mesmo nome → camada maior vence; dirs do
   workspace alinhados aos de skills quando fizer sentido.
2. **RF-002 — Parser tolerante**: frontmatter mínimo
   `name`+`description`; corpo vira instrução; arquivo inválido →
   skip + warning, nunca throw.
3. **RF-003 — agent_catalog tool**: retorna lista ordenada
   (source→name) com descrições; formato texto compatível com tools
   atuais.
4. **RF-004 — subagent_type declarativo**: `task` aceita nomes do
   catálogo; `Body` vira system/persona do subagent; erro lista
   válidos (comportamento atual de "unknown subagent_type" mantido
   e estendido).
5. **RF-005 — Permissões não-escaláveis**: `tools` do agent só
   restringe o toolset do subagent dentro do já permitido pelo run —
   teste prova que `tools: [admin_shell]` num `*.agent.md` não
   concede nada novo.
6. **RF-006 — Caps**: arquivo >64KB ou catálogo >64 entradas →
   truncamento + warning.
7. **RF-007 — Prompt**: SPEC-90 `DelegationPolicyContributor` lê o
   mesmo `IAgentCatalog` — sem segunda fonte de verdade.

## 4. API Contract

Tools novos/alterados (contrato de modelo, não HTTP):

```
agent_catalog() → "name — description (source)\n…"
task(subagent_type="repo-reviewer", task="…", …)
  → erro: "unknown subagent_type 'x' — válidos: builtin-a, repo-reviewer, …"
```

Frontmatter mínimo de `*.agent.md`:

```markdown
---
name: repo-reviewer
description: Reviews diffs for correctness and style
tools: [file_read, bash]
model: inherit
---
You are a strict code reviewer…
```

## 5. Critérios de aceite

- Repo com `.claude/agents/reviewer.agent.md` → `agent_catalog`
  lista `repo-reviewer (workspace)` e `task subagent_type=repo-reviewer`
  spawna com o Body como instrução.
- `*.agent.md` sem `name` → ignorado com warning; run não falha.
- Agent declarativo com `tools` além do permitido → subagent nasce
  só com o subset já autorizado.
- Build+test+format verdes.

## 6. Task Plan

1. `AgentDefinition` + parser + testes.
2. `AgentCatalogService` (camadas + caps) + built-ins mínimos.
3. `agent_catalog` builtin tool + validação no `task`.
4. Wire com `DelegationPolicyContributor` (SPEC-90) se disponível;
   senão, contrato pronto.
5. Docs `docs/features/` + exemplo de `*.agent.md`.

## 7. Deliverables

- Catálogo declarativo funcional + tool + validação de
  `subagent_type` + testes + docs.

## 8. Referências

- `src/OpenHandsNET.Infrastructure/Agent/Tools/BuiltinTools.cs:1066-1100`
  (`task`/`subagent_type`), `Subagents/SubagentRunner.cs`,
  `Application/Agent/SubagentContracts.cs`,
  `Infrastructure/Skills/RepoSkillProvider.cs:24` (dirs `.agents/skills`,
  `.claude/skills` — padrão de descoberta análogo).
- `open-webui`: `AgentDefinitionService.cs` (camadas + parser),
  `AgentCatalogBuiltinTool.cs`, `DelegateTaskBuiltinTool.cs`.

## 9. Guardrails

- Conteúdo de `*.agent.md` é **untrusted** (vem do repo clonado):
  entra como instrução de subagent, nunca sobe privilégio — alinhado
  à doutrina SPEC-91 (envelope) e SPEC-25 (repo context citado).
- Sem execução de frontmatter arbitrário (só campos conhecidos);
  sem fetch remoto.
- Catálogo por-run (não cache global entre conversas de orgs
  diferentes sem invalidação).

## 10. DoD

- Catálogo em camadas + tools integrados + testes de segurança de
  permissão + docs; build verde.

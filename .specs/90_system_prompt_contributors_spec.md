# SPEC — 90 · System prompt contributors — pipeline componível

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `system-prompt-contributors` |
| Type | `Refactor / Feature (harness seam)` |
| Stack | `.NET 10 · ASP.NET Core · agent runtime` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #233 (Epic #231) · análise `afonsoft/open-webui` (2026-10-22) |
| Depends on | — (SPEC-95 consome o bloco de delegation policy) |
| Reference | `open-webui` `src/OpenWebUI.Infrastructure/SystemPrompt/ISystemPromptContributor.cs` + `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (SPEC-20261021-harness-seams, SPEC-20261023-sdd-orchestrator-prompts) |
| Touches | `Application/Agent` (contratos), `Infrastructure/Agent/SystemPromptBuilder.cs`, `Infrastructure/Agent/AgentRunner.cs`, DI do agent runtime, `tests` |

## 1. User Story

**As a** mantenedor do runtime de agentes
**I want** o system prompt composto por uma cadeia de contributors
ordenados e isolados (`ISystemPromptContributor` + `AgentPromptContext`)
**So that** novos blocos (doutrina SDD, política de delegação, tiers de
autonomia, digest de memória, contexto de repo, skills) entrem como
contribuições independentes — testáveis, com `Order` explícito e falha
de um contributor não derrubando o prompt inteiro.

**Contexto:** hoje `SystemPromptBuilder.Build` é um método estático
monolítico (~78 linhas) que concatena persona + tools + repo context +
skills num único `StringBuilder`. Cada bloco novo exige editar o método
central e cresce o acoplamento (skills, repo context e memória já são
parâmetros opcionais acumulados). O open-webui resolveu o mesmo problema
com uma cadeia de 13 contributors registrados via DI, cada um com
`Order` fixo e exceção tolerada pelo composer (seção omitida +
`LogWarning`) — incluindo blocos que o OpenHandsNET não tem: doutrina
SDD por superfície de `.specs/`, tiers de autonomia T1/T2/T3 e política
de delegação para agentes declarativos.

## 2. Escopo

In-scope:

- Contrato `IAgentPromptContributor` (`Order` + `ContributeAsync(ctx)`)
  em `Application/Agent` (zero deps de Infrastructure — `PromptContext`
  carrega contratos, não entidades EF).
- `AgentPromptContext`: conversation/run corrente, `SelectedRepository`,
  tools do run, skills resolvidas, digest de memória (via
  `IMemoryService.DigestAsync`), ct — com factories memoizadas para
  workdir/repo (mesma ideia do `PromptContext.GetWorkdirAsync` do
  open-webui) para não repetir IO por contributor.
- Composer: itera `IEnumerable<IAgentPromptContributor>` por `Order`
  crescente, concatena seções, tolera exceção por contributor (omite
  seção + `LogWarning`, nunca falha o run).
- Contributors iniciais (migração 1:1 dos blocos atuais):
  - `BaseAgentContributor` (Order 0): persona + regras de sandbox +
    catálogo de tools (texto atual do `SystemPromptBuilder`).
  - `RepoContextContributor` (Order 40): `## Repository context` —
    instruções do repo citadas como não-privilegiadas (comportamento
    SPEC-25 preservado).
  - `SkillsContributor` (Order 50): skills db+repo injetadas (bloco
    atual).
  - `MemoryDigestContributor` (Order 60): digest `IMemoryService`
    (SPEC-30 RF-003) — hoje injetado ad-hoc.
- Contributors novos (valor portado do open-webui):
  - `AutonomyTiersContributor` (Order 10): diretivas T1/T2/T3 +
    "no silent execution" — T1 reversível executa; T2 dentro de
    SPEC/escopo executa em batch; T3 (feature nova, modelo de dados,
    secrets, deploy, destrutivo) pede aprovação antes. Coerente com
    `AutonomyGuardrail`/`PermissionEvaluator` existentes (prompt informa,
    gate executa).
  - `SddWorkflowContributor` (Order 15): quando o workspace tem
    `.specs/SPEC-*.md` ou `docs/specs/` (probe via executor de
    sandbox/filesystem), injeta bloco SDD: ciclo Draft→Approved→
    In implementation→Completed, RF-###/AC-##, TDD por slice, sync de
    status com Issue. `Agent:Sdd` config: `auto|on|off` (default auto).
  - `DelegationPolicyContributor` (Order 85): quando há subagents
    habilitados (SPEC-28), injeta política "delega, não executa" —
    slice→assign→review; aponta para `task`/`task_status`/`task_result`.
- `AgentRunner` passa a chamar o composer (mantém assinatura pública dos
  pontos de entrada atuais; `SystemPromptBuilder` vira shim obsoleto ou
  é removido com referências atualizadas).
- Testes unitários: ordem de concatenação, omissão por exceção, memoização
  de factories, bloco SDD ligado/desligado/auto, tiers presentes.

Out-of-scope: `builtin_specs` tool (list/show/transition de SPECs —
follow-up próprio); MCP elicitation; contributors por-org/tenant
configuráveis via UI; condenser/context snapshots.

## 3. Requisitos

1. **RF-001 — Contrato**: `IAgentPromptContributor { int Order;
   Task<IReadOnlyList<string>> ContributeAsync(AgentPromptContext ctx); }`
   em `OpenHandsNET.Application.Agent`; `AgentPromptContext` record/class
   com factories memoizadas (`Func<CancellationToken, Task<T>>` +
   lazy task) para workdir e repo binding.
2. **RF-002 — Composer ordenado e tolerante**: concatena seções por
   `Order`; exceção num contributor → seção omitida + `LogWarning`
   (nunca `throw`); contributors que retornam `[]` não emitem texto.
3. **RF-003 — Paridade de saída**: com apenas os contributors de
   migração (Base/Repo/Skills/Memory), o prompt resultante é
   equivalente ao atual — mesma ordem de blocos, mesmo conteúdo
   (teste de golden string nos blocos principais).
4. **RF-004 — Autonomy tiers**: bloco T1/T2/T3 + "no silent execution"
   presente sempre que `AutonomyGuardrail` estiver ativo para o run.
5. **RF-005 — SDD surface**: `Agent:Sdd=auto` (default) injeta o bloco
   apenas quando `.specs/SPEC-*.md` ou `docs/specs/` existe no
   workspace; `on` sempre injeta; `off` nunca.
6. **RF-006 — Delegation policy**: bloco presente só quando o run tem
   `task`/`subagent` habilitado e catálogo não-vazio (SPEC-95: quando
   o catálogo for file-driven, reusar a mesma fonte).
7. **RF-007 — DI**: contributors registrados no container do agent
   runtime; `AgentRunner` resolve `IEnumerable<IAgentPromptContributor>`
   — sem `new` manual dentro do runner.
8. **RF-008 — Idioma/estilo**: blocos novos em inglês (prompt é
   contrato de modelo); XML docs em APIs públicas.

## 4. API Contract

Sem endpoints novos. Config nova:

```
Agent__Sdd = auto | on | off     # default: auto
```

## 5. Critérios de aceite

- Dado um run com skills+repo context+memória, o prompt contém os
  blocos na mesma ordem relativa atual (regressão por golden test).
- Dado um contributor que lança exceção, o run segue com a seção
  omitida e `LogWarning` registrado.
- Dado workspace sem `.specs/` e `Agent:Sdd=auto`, o bloco SDD não
  aparece; com `.specs/SPEC-*.md`, aparece.
- `dotnet test` verde; `dotnet format --verify-no-changes` limpo.

## 6. Task Plan

1. Contratos (`IAgentPromptContributor`, `AgentPromptContext`) +
   composer com testes de ordem/tolerância.
2. Migrar blocos existentes para Base/Repo/Skills/Memory contributors;
   `AgentRunner` consome o composer; paridade de saída (RF-003).
3. Novos contributors: AutonomyTiers, SddWorkflow (probe de superfície),
   DelegationPolicy — com testes por modo.
4. Remover/adaptar `SystemPromptBuilder`; atualizar docs (`docs/features/`,
   `docs/en/` se houver seção de prompt).

## 7. Deliverables

- Pipeline de contributors funcional, 7 contributors, testes, docs.

## 8. Referências

- `src/OpenHandsNET.Infrastructure/Agent/SystemPromptBuilder.cs`
  (monólito atual), `AgentRunner.cs`, `AutonomyGuardrail.cs`,
  `IMemoryService.DigestAsync` (`Application/Memory/MemoryContracts.cs`),
  `RepoSkillProvider` (SPEC-25).
- `open-webui`: `ISystemPromptContributor.cs` (PromptContext memoizado,
  Order, tolerância), `SystemPromptContributors.cs` (13 blocos —
  `SddWorkflowContributor` Order 15, `DelegationPolicyContributor`
  Order 85, `AlwaysAllowContributor` Order 10).

## 9. Guardrails

- Prompt é contrato de modelo: blocos novos concisos (o bloco SDD do
  open-webui tem ~1KB — manter proporção), sem interpolar dados de
  usuário fora dos envelopes (ver SPEC-91).
- Não quebrar `InitialMessage`/contratos V1 de run; sem mudança de
  schema EF.
- `Agent:Sdd` documentado em `.env.exemplo`/`config/`.

## 10. DoD

- Composer + 7 contributors em produção; paridade de saída provada por
  teste; `Agent:Sdd` documentado; build+test+format verdes.

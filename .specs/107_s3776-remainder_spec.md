# SPEC — 107 · S3776 cognitive complexity — remainder

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `s3776-remainder` |
| Type | `Refactor` |
| Status | `In Review` |
| Ticket | gap-analysis 2026-10-24 · GAP-sonar-s3776-remainder |
| Scope | `src/**` exceto `Api/Endpoints` (SPEC-103), `Infrastructure/Agent` (SPEC-102), `cli/` (SPEC-101) |

## 1. User Story

**As a** mantenedor, **I want** os achados S3776 fora das fatias 101–103
resolvidos, **so that** a regra zera em todo o codebase first-party.

## 2. Contexto / escopo

Live 2026-10-24 (`tools/sonar-rescan.py`): csharpsquid:S3776 = **52 abertas**.
Após merges #263/#264: `cli/` = 0, `Infrastructure/Agent` = 4 residuais,
`Api/Endpoints` = 17 (SPEC-103). **31 não cobertas por nenhuma SPEC:**

- `SandboxAgent/Program.cs` ×3 · `Tickets/TicketProviders.cs` ×3
- `Client/Services/ConversationStateService.cs` ×2 · `ChatOps/SlackChatOpsService.cs` ×2
- `Scm/GitHubWebhookService.cs` ×2 · `Discovery/RepoContextLoader.cs` ×2
- ×1 cada: `Api/Program.cs`, `Api/Policies/PermissionPolicy.cs`,
  `Client/Components/{ChatComposer,IdeExplorer,IdeTab}.razor`,
  `Eval/{EvalRunner,EvalTask}.cs`, `Infra/{Automations,Conversations,
  DependencyInjection,Git/{DiffParser,StatusParser},Identity/ApiKeyService,
  Insights/SessionInsightService,Sandbox/{SandboxAgentExecutor,SandboxService},
  Security/InputGuardService}` + Agent residuals ×4.

## 3. Requisitos

- RF-001: corrigir as ocorrências da regra no escopo — sem mudar comportamento.
- RF-002: testes de regressão cobrindo áreas tocadas (`check-pr-test-policy.sh`).
- RF-003: won't-fix justificado documentado onde o design for intencional.
- RF-004: `dotnet format` + build 0 warnings + suite verde.

## 4. Acceptance

- [ ] Sonar deixa de reportar S3776 fora dos escopos 101–103 (ou won't-fix).

## 5. Execução (2026-10-25)

**34/35 sites decompostos** — extração behavior-preserving:

- `Git/{StatusParser,DiffParser}`: handlers por linha + classe `Parser`.
- `Tickets/TicketProviders`: `ParseIssueUpdate/CommentCreate` + `WalkAdf`.
- `Scm/GitHubWebhookService`: record `MentionFields` + handlers por kind.
- `Client/Services/ConversationStateService`: pumps + dispatch `Apply*`.
- `SandboxAgent`: `AgentHandlers` (endpoints) + `RssKb` + `EmitTap`.
- `DependencyInjection.AddInfrastructure` (~600ln) → 9 `Register*`.
- `Security/InputGuardService`: `ResolveModeAsync` + `LlmVerdictAsync`.
- `Identity/ApiKeyService`: `TouchLastUsedAsync`.
- `Insights/SessionInsightService`: `BuildAnalysis` + `SuggestLearningsAsync`.
- `Discovery/RepoContextLoader`: `Append{Instructions,Readme,Skills}`
  + `ParseSkillRefs`.
- `Conversations/ConversationStartWorker`: `CloneRepositoryAsync` +
  `ResolveSkillsAsync`.
- `ChatOps/SlackChatOpsService`: `DispatchEventAsync` +
  `DispatchInteractionAsync` + `PostbackAsync`.
- `Sandbox/SandboxService`: `AppendSecretEnvAsync` + `AppendGitEnvAsync`.
- `Automations/AutomationService`: `FireDueAsync` + `ReconcileRunningAsync`.
- `Sandbox/SandboxAgentExecutor`: `ParseSseLine`.
- `Eval/{EvalRunner,EvalTask}`: `SummarizeEvents` +
  `CollectAssertArtifacts` + `ValidateAssertType`.
- `Api/Program.cs` top-level → `ProgramParts` (`AddObservability`,
  `AddOpenApiDoc`, `AddAuth`, `UsePipeline`, `MapApi`).
- `Api/Policies/PermissionPolicy`: `ResolveTargetOrgAsync` +
  `OrgRolePermitsAsync`.
- `Client/Components/{ChatComposer,IdeExplorer,IdeTab}`: `HandlePopupKeyAsync`,
  `ApplyChanges`+`GitLetter`, `Create{Normal,Diff}EditorAsync`.
- Agent residuals: `LlmSummarizingCondenser.SummarizePhaseAsync`,
  `OpenAiCompatLlmProvider.{ReasoningChunks,ToolCallChunks}`,
  `SubagentRunner.{CollectTurnAsync,ExecuteOneCallAsync}`.

**Deferido (fatia própria):** `AgentRunner.LoopAsync` (~890 linhas,
CC~210) — core loop do agente; precisa de context-class dedicada e
cobertura de regressão forte. Risco alto demais para extração mecânica
no mesmo PR; será SPEC filha de refatoração dirigida por testes.

**Gate de testes:** `EvalHarnessTests` +2 facts (`Validate` por tipo de
assert + happy path).

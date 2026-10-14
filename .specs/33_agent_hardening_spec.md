# SPEC — 33 · Agent Hardening (SecurityAnalyzer + by-ref secrets + resume validation)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `agent-hardening` |
| Type | `Feature / Security` |
| Stack | `.NET 10 · AgentRunner · DI` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | #65 |
| Depends on | `09`, `22`, `23`, `29` |
| Touches | `AgentRunner` (dispatch), `ConversationStartWorker`, `ISecretsStore`, `ConversationHistoryBuilder` |

## 1. User Story

**As a** admin de segurança
**I want** análise de risco por tool-call (pattern/LLM/policy), secrets
passados por referência (nunca no payload do start-task) e validação de
resume (toolset do run não pode perder tools silenciosamente)
**So that** o agente falha fechado em ações destrutivas e não vaza
secret em eventos.

Referências: upstream `SecurityAnalyzer` (LLM/pattern/policy,
DefenseClaw — SPEC-32 RF-03), `LookupSecret` URLs (RF-04), resume
validation (RF-10).

## 2. Scope (RF)

### RF-001 — `ISecurityAnalyzer`

```csharp
public interface ISecurityAnalyzer
{
    Task<SecurityRisk> AnalyzeAsync(ToolCallContext ctx,
        string toolName, JsonElement args, CancellationToken ct);
}
public enum SecurityRisk { Low, Medium, High }
```

- `PatternSecurityAnalyzer` (**default**): `rm -rf /`, `mkfs`, `dd of=/dev`,
  `:(){:|:&};:`, `curl|wget …|sh`, `chmod -R 777 /`, write fora de
  `/workspace`, `git push --force` para `main`, download de binário +
  exec.
- `LlmSecurityAnalyzer` (opt-in `Security:Analyzer=llm`): role
  `Summarizer` da SPEC-23 classifica args → `High|Low`; cache por
  `sha256(toolName+args)` (LRU 512).
- `PolicyRailSecurityAnalyzer` (enterprise, stub): externo via HTTP
  `Security:PolicyEndpoint` — mesmo shape (DefenseClaw-like, conceito).

### RF-002 — Gate no dispatch

`AgentRunner` antes de executar tool `Risky=true` **ou** `ask`:
`analyzer.AnalyzeAsync` → `High` ⇒ `ConfirmationRequired` forçado
(mesmo com `allow`), e `agent_diagnostic code="security_flag"`
`{tool, rule|model, preview}`; `Medium` ⇒ marca no observation.
Nunca bloqueia silenciosamente — `High` vira `ask`.

### RF-003 — Secrets by-reference

- Start-task aceita `secrets: ["DB_PASSWORD", …]` (nomes).
  `ConversationStartWorker` resolve os valores **na borda** (chama
  `ISecretsStore.GetAsync(name)`) e injeta env no sandbox — o valor
  nunca vai no payload nem em eventos (hoje o valor trafega no
  request quando o caller já sabe — continua aceito, mas nomes são
  preferred).
- `env` publicada p/ o sandbox registra só nomes no evento
  `sandbox_env` (valor nunca serializado).

### RF-004 — Resume validation

- No start do run, `ConversationStateUpdateEvent(key="agent_contract")`
  com `{agent_version, tools:[…], tools_hash}`.
- Resume (idle→run): se o fingerprint mudou e **removeu** tools,
  `agent_diagnostic code="resume_drift"` + fail-open (log). Fail-closed
  fica flag `AgentLlm:StrictResume=false` (default).

## 3. API Contract

Nenhum endpoint novo. Events: `agent_diagnostic` codes
`security_flag`, `resume_drift`; state `agent_contract`, `sandbox_env`
(names only).

## 4. Data model

Sem migration. Regras do analyzer em `AgentLlmOptions`/appsettings
(org override: `security_policies` JSON — admin, futuro).

## 5. NFR / Security

- Analyzer pattern é O(args) e local; llm analyzer é opt-in + cache.
- By-ref secrets: o valor só existe em memória do worker + env do
  sandbox; SecretRedactor cobre output (SPEC-24 já redige stream).
- Resume validation é observabilidade (fail-open) — nunca quebra run
  existente.

## 6. Task Plan

1. `ISecurityAnalyzer` + `PatternSecurityAnalyzer` + gate no dispatch.
2. `LlmSecurityAnalyzer` + cache + role Summarizer.
3. Secrets by-ref no start-task + `sandbox_env` names-only.
4. `agent_contract` fingerprint + `resume_drift` diagnostic.
5. Testes: pattern rules (rm -rf → ask), llm cache, names-only env
   no evento, drift diagnostic.

## 7. Deliverables

`Agent/Security/*`, wiring no runner/worker, testes.

## 8. Referências

- upstream `SecurityAnalyzer` + DefenseClaw (conceito — SPEC-32).
- upstream `LookupSecret` (conceito).

## 9. Guardrails

- `High` ⇒ `ask`, nunca abort silencioso.
- Secret value nunca serializado em evento (nem mascarado — só nomes).
- `resume_drift` é fail-open por default.

## 10. DoD

- `bash "rm -rf /"` ⇒ confirmation forçada + diagnostic.
- Start-task com `secrets:["K"]` injeta env sem valor no evento.
- Resume c/ toolset reduzido emite `resume_drift`.
- `dotnet test` verde.

# SPEC — 125 · Advisor role (segundo modelo opinando em decisões arriscadas)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `advisor-role` |
| Type | `Feature` |
| Status | `In review` |
| Ticket | #330 (umbrella #329, Epic #317) — gap-analysis OpenWebUiCode 2026-11-04 |
| Scope | `src/OpenHandsNET.Application/Agent/`, `src/OpenHandsNET.Infrastructure/Agent/` (runner, LLM client, prompts) |

## 1. User Story

**As a** operador do agente, **I want** que um segundo modelo ("advisor")
opine sobre ações arriscadas ou de alto impacto antes da execução, **so
that** decisões destrutivas ou ambíguas ganhem uma segunda análise — como
o `advisor-system.md` do OpenWebUiCode faz.

## 2. Contexto / escopo

- `ModelRole` já existe e `EmbeddedPrompts` já carrega
  `prompts/advisor-system.md` (embutido na SPEC-123), mas **nenhum
  caminho no `AgentRunner` invoca o advisor**.
- No OWUIC, o advisor é consultado em pontos de decisão (antes de ações
  marcadas risky pelo permission judge, ou quando o modelo pede
  `advisor` explicitamente).
- O advisor **não executa tools** — recebe contexto compacto e devolve
  parecer estruturado que alimenta o próximo turno do modelo principal.

## 3. Requisitos

| ID | Requisito |
|---|---|
| RF-001 | Config `Agent:AdvisorModel` (opcional); quando ausente, feature inerte — zero overhead. |
| RF-002 | `IAdvisorService.ConsultAsync(context, question, ct)` — chamada LLM one-shot com `advisor-system.md` como system prompt; temperatura baixa; sem tools. |
| RF-003 | Ponto de consulta automático: quando `SmartPermissionJudge`/security analyzer marca uma ação como risky-mas-permitida, o parecer do advisor entra como evento `advisor` no stream (não bloqueia permissão — o usuário já decide). |
| RF-004 | Ponto manual: tool `advisor` (deferred — carrega via `tool_search`) que o modelo invoca com `{question}` para pedir segunda opinião. |
| RF-005 | Resposta do advisor vira mensagem `<advisor>...</advisor>` na conversa + evento persistido no stream V1 (`kind: advisor`). |
| RF-006 | Timeout curto (≤ 30s) e fail-open: advisor indisponível não quebra o loop. |

## 4. Acceptance

- [ ] Com `AdvisorModel` vazio, zero chamadas extras (teste).
- [ ] `advisor` tool invocada → parecer aparece como evento na conversa.
- [ ] Ação risky → evento `advisor` emitido antes da decisão final (quando habilitado).
- [ ] Timeout/erro do advisor não interrompe o run (fail-open).

## 5. API Contract

Sem mudança de contrato REST. Evento novo: `kind: "advisor"` no stream
de eventos V1 (campos: `question`, `answer`, `model`, `elapsed_ms`).

## 6. Task Plan

1. `IAdvisorService` + `AdvisorService` (Infrastructure) — consulta LLM via cliente existente.
2. Wiring no `AgentRunner` — ponto risky + dispatch da tool `advisor`.
3. `BuiltinAdvisor` tool + registro deferred em `DeferredToolContracts`.
4. Evento `advisor` no Domain + serialização.
5. Testes unitários (fail-open, deferred, evento) + doc em `docs/`.

## 7. Riscos / notas

- Custos: cada consulta = 1 chamada LLM extra — opt-in por config.
- Latência: fail-open com timeout garante que não trava o run.
- Não confundir com `SmartPermissionJudge` (judge decide permissão;
  advisor opina — papéis separados como no OWUIC).

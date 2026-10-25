# SPEC-111 — AgentRunner.LoopAsync decomposition (dedicated slice)

| Campo | Valor |
|---|---|
| Status | `Draft` |
| Origem | gap-analysis 2026-10-25 — deferral explícito da SPEC-107 |
| Severidade | P1 |
| Epic | SonarCloud remediation (#152) / gap-analysis followup |

## 1. Contexto

`AgentRunner.LoopAsync` (`src/OpenHandsNET.Infrastructure/Agent/AgentRunner.cs`)
é o core loop do agente — ~890 linhas, complexidade cognitiva estimada ~210
( Sonar marca-o como o maior site S3776 remanescente). A SPEC-107 o deferiu
explicitamente: blast radius alto, extração mecânica apressada seria arriscada.
O método concentra: drain de mensagens pendentes, cadeia de triggers (forced,
scheduled, mention), chamada LLM em streaming, dispatch de tool calls,
checkpointing, sinais de controle (pause/stop/interrupt), heartbeat e
tratamento de erro/cancelamento.

## 2. Escopo

Decompor `LoopAsync` em fases nomeadas, **uma fase por PR interna** ou um
único PR com extrações sequenciais revisáveis commit-a-commit:

1. **Preâmbulo**: setup do loop (carregar conversa, resolver modelo,
   inicializar estado de iteração) → `PrepareIterationContext`.
2. **Drain + triggers**: já parcialmente extraído (SPEC-102:
   `DrainPendingAsync`, `ForcedTrigger`) — avaliar o que resta.
3. **Dispatch de tool calls**: loop de execução/apêndice de observações →
   `ExecuteToolCallsAsync`.
4. **Signal handling**: pause/stop/interrupt/checkpoint → `HandleSignalsAsync`.
5. **Tail**: persistência, eventos de fim de iteração, erro/backoff →
   `FinalizeIterationAsync`.

Fora de escopo: mudança de comportamento, reordenação de lógica, novo
paralelismo. Apenas extração comportamento-preservante.

## 3. Riscos e mitigação

- Core loop do agente — qualquer desvio sutil de ordem de await/catch/ct
  quebra runs reais. Mitigação: extração por fases com build+test entre cada;
  diffs commit-a-commit pequenos; revisão humana recomendada.
- `ct` threading: todo helper novo recebe `CancellationToken` explícito.
- Capturas de closure: preferir record de estado (`IterationState`) a
  dezenas de parâmetros (lição S107 da SPEC-107).

## 4. Testes

- `tests/OpenHandsNET.UnitTests/Agent/*AgentRunner*` existentes devem
  permanecer verdes sem alteração (prova de preservação).
- Acrescentar teste de regressão só se a extração revelar caminho sem
  cobertura (ex.: ordem de drain × forced trigger).
- `AgentEngineE2ETests` (integration) é o guardrail e2e.

## 5. DoD

- `LoopAsync` < ~120 linhas de corpo; fases nomeadas; CC estimado < 25.
- Build 0 warnings; `dotnet format --verify-no-changes` limpo.
- Unit + integration tests verdes; CI completo verde.
- Sem mudança comportamental observável (mesmos eventos emitidos).

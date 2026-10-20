# SPEC-78: Integration tests — hardening contra flakes de CI

| Campo | Valor |
|---|---|
| Status | Draft |
| Gap key | CI-flake-integration-tests |
| Type | Quality/Testing |
| Fonte | falhas observadas no run `39352893002` (PR #197) e anteriores |

## User Story

Como mantenedor, quero que os testes de integração não falhem por causas
ambientais (timing, estado residual de sandbox) — flakes gastam re-run
inteiro de CI e corroem a confiança no gate.

## Contexto / evidência

Dois flakes confirmados (ambos passam no re-run, gate não relacionado ao
diff):

1. **`SkillsHooksE2ETests.Skills_Inject_InvokeSkill_Works_Hooks_Emit_Events`** —
   `sawFinished` false após deadline fixo de **3min** (`SkillsHooksE2ETests.cs:115`).
   O teste roda um agent loop real e faz poll de
   `/events/search?limit=200` a cada 1.5s; em runner carregado o run
   não termina a tempo. Falhou na main e no PR #197.
2. **`Spec42WorkspaceEndpointsTests.File_Ops_And_Search_Roundtrip`** —
   `POST …/files/mkdir /workspace/repo/spec42dir` retornou **409**
   (esperado 204): o dir já existia. Causa: path fixo +
   **warm-pool** reutiliza container/workspace entre conversas, e
   resíduo de execução anterior contamina o workspace "novo".

## Requisitos

1. **RF-001 — Paths únicos por execução**: todo teste que escreve em
   `/workspace/repo/` usa sufixo único (`spec42dir-{guid:n[:8]}` ou
   `Path.GetRandomFileName`). Cobrir `Spec42WorkspaceEndpointsTests`
   (8 paths fixos), `Spec43CheckpointEndpointsTests` (3) e demais
   literais encontrados — mesma classe de bug.
2. **RF-002 — E2E resiliente a timing**:
   - deadline por env (`E2E_DEADLINE_MINUTES`, default 3 → 6 no CI);
   - poll do estado da conversa (`GET /app-conversations/{id}` →
     `execution_status`) em vez de varrer 200 eventos por rodada —
     barato e direto; eventos só consultados uma vez ao final para as
     demais asserções;
   - mensagem de falha com a cauda dos eventos observados (diagnóstico
     sem precisar baixar o log inteiro).
3. **RF-003 — Sweep anti-contaminação**: assert/guard no pool — se o
   warm-pool deve garantir workspace limpo por claim, teste de
   invariante dedicado documenta a garantia; caso contrário, RF-001 é
   a regra permanente (registrar decisão na SPEC).
4. **RF-004 — Quarentena explícita**: flake conhecido mas não corrigido
   → `[Trait("Category","Flaky")]` + retry automático do step
   (`dotnet test` rerun failed) em vez de gate vermelho silencioso.

## Critérios de aceite

- `File_Ops_And_Search_Roundtrip` não pode 409 por resíduo (path único).
- E2E falha apenas quando o run realmente não termina no prazo
  configurável — e a mensagem traz evidência (últimos eventos/status).
- CI verde 3× consecutivas na main sem re-run manual.
- `dotnet format --verify` limpo; testes novos cobrem o helper de path.

## Fora de escopo

Reescrita do warm-pool (reset de workspace por claim) — avaliar em
SPEC separada se RF-003 concluir que a garantia deve existir no produto
e não só no teste.

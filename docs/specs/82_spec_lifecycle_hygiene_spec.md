# SPEC-82: Spec lifecycle hygiene — arquivar entregues + fechar slices

| Campo | Valor |
|---|---|
| Status | Completed |
| Gap key | GAP-automation-spec-lifecycle |
| Type | Automation/Docs |
| Fonte | gap-analysis 2026-10-20 — `.specs/README.md:19` (regra de arquivo) vs AS-IS |

## User Story

Como mantenedor, quero que SPECs entregues saiam de `.specs/` para
`docs/specs/` com `Status: Completed` e que as issues slice/epic
fechem — hoje SPECs mergeadas permanecem ativas e issues abertas,
diluindo o índice e confundindo o orchestrator (que pode reenfileirar
trabalho já entregue).

## Contexto / evidência

Regra (`.specs/README.md`): `Draft → Approved → Done → mover para
docs/specs/`. AS-IS:

- `75_layout_shell_mobile_a11y_spec.md` — entregue (PR #196), Status
  ainda `Draft`; issue #193 aberta (`slice,todo`).
- `76_chat_run_ux_spec.md` — entregue (PRs #196/#197), Status `Draft`;
  issue #194 aberta.
- `77_ide_workbench_deepen_spec.md` — entregue (PR #197, vscode embed),
  Status `In implementation`; issue #195 aberta.
- `78_integration_test_hardening_spec.md` — entregue (PRs #201/#204),
  Status `Approved → In implementation`; issue #199 já fechada.
- Epic #192 (gap-analysis-20261019) aberto com todas as slices entregues.
- Recorrência: SPEC-65 criou a regra, mas o ciclo voltou a ocorrer —
  falta verificação/automação do fechamento.

## Requisitos funcionais

1. **RF-001 — Arquivar**: mover 75–78 para `docs/specs/` com
   `Status: Completed`; atualizar `.specs/README.md` e
   `docs/specs/README.md` (índices).
2. **RF-002 — Fechar issues**: comentar+fechar #193, #194, #195 e o
   epic #192 (todas entregues; referenciar PRs).
3. **RF-003 — Guard leve**: `scripts/check-spec-lifecycle.sh` (ou job
   no CI/docs-lint) que falha/avisa quando uma SPEC ativa tem status
   `Done`/`Completed`, ou quando issue slice vinculada está `MERGED`/
   fechável — mínimo: listar SPECs ativas com >N dias sem mudança de
   status. Escopo deliberadamente pequeno (warning, não gate).

## Critérios de aceite

- [ ] `.specs/` só contém SPECs realmente ativas.
- [ ] #192–195 fechadas com referência aos PRs de entrega.
- [ ] Guard executável documentado (script ou step).

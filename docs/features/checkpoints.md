# Checkpoints + Revert (SPEC-43)

Snapshots do workdir fora da árvore git do usuário — segurança para
deixar o agente mexer livremente.

## Modelo

- Git-dir separado (`/workspace/.ohn-checkpoints/`) apontando para o
  worktree — o `.git` do projeto do usuário não é tocado.
- Snapshot automático ao fim de cada turno (`Finished`) e em `Paused`;
  manual via tool `checkpoint` (agente) ou botão na UI.
- Retenção `Checkpoints:MaxCount` (default 50): truncamento por
  `git replace --graft` — mantém os N mais novos, preserva a cadeia
  acima do graft.

## Revert

`POST /{id}/checkpoints/{hash}/revert` com drift guard:

1. `force=false` (default): compara o workdir atual com o snapshot do
   topo — se houver drift (mudanças posteriores ao checkpoint), `409`
   retorna a lista de arquivos conflitantes.
2. `force=true`: `git checkout -f` descarta o drift.

Reverts bloqueiam enquanto `ExecutionStatus=Running` (409
`run_active`).

## UI

`CheckpointList` (aba SCM do IDE + fallback na aba commits): hash
curto, label, stats `+ins/-del`, botão revert → modal de confirmação
com lista de conflitos e opção `revert (force)`.

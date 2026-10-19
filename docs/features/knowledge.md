# Knowledge Pipeline (SPEC-63)

Pipeline de conhecimento: aprendizados capturados por runs (via
session insights) e pelo groom de dedup entram numa **fila de
revisão** — nada é escrito na memória da org sem aprovação humana.

## Modelo

`knowledge_suggestions` — `Scope` (`org` | `project`), `RepoPath?`,
`Title`, `Content`, `Kind` (`entry` | `merge` | `conflict`),
`Status` (`pending` | `approved` | `dismissed`), `Reason`,
`SourceConversationIds`/`SourceEntryIds` (JSON), `Resolution`.

## API (`/api/v1`)

| Método | Rota | Ação |
|---|---|---|
| GET | `/knowledge/suggestions?status=&scope=` | fila |
| POST | `/knowledge/suggestions` | cria sugestão manual |
| POST | `/knowledge/suggestions/{id}/approve` | grava via `IMemoryService` (aceita `content_override`) |
| POST | `/knowledge/suggestions/{id}/dismiss` | fecha sem gravar |
| POST | `/knowledge/groom` | roda dedup/conflict/stale agora (`perm:edit_org_settings`) |
| GET | `/knowledge/entries` | lista entries da org efetiva |

Approve exige `perm:memory:write` ou admin. Conteúdo sugerido passa
pelo `SecretRedactor` antes de persistir.

## Fontes de sugestões

- **Session insights (SPEC-61)**: ao final de um run, cada
  `learning` do insight vira uma suggestion `entry` pendente
  (scope `org`, `repoPath` = `selected_repository`, reason
  `session-insight`).
- **Groom (RF-003)**: `KnowledgeGroomer` (hosted service, intervalo
  `Knowledge:GroomIntervalHours`, default 24h) varre as memory
  entries e gera:
  - `merge` — similaridade Jaccard de tokens ≥ 0.7;
  - `conflict` — similaridade ≥ 0.4 + negação detectada;
  - `stale` — entries sem update há mais que
    `Knowledge:StaleDays` (default 90) — reporta no resultado,
    nunca deleta.

## UI

`/knowledge` — fila com filtro por status, approve/dismiss inline
e botão "run groom".

## Divergências do upstream

- O upstream (Python) faz "dreaming" direto na memória; aqui
  tudo passa pela fila — decisão consciente de auditabilidade.
- `suggest_knowledge` tool / `AgentWriteMode=suggest`: deferred —
  o dispatch de tools vive no agente do sandbox (SPEC-62);
  documentado como pendência na spec.

## Config

| Chave | Default | Uso |
|---|---|---|
| `Knowledge:GroomIntervalHours` | 24 | intervalo do groom |
| `Knowledge:StaleDays` | 90 | idade para stale |
| `Knowledge:SuggestionTtlDays` | 30 | expiração de pending |
| `Knowledge:AgentWriteMode` | `direct` | `direct`/`suggest` (deferred) |

# Ask mode (SPEC-60)

Sessão de perguntas **read-only** sobre um repositório indexado: busca
full-text nas unidades de código → LLM com obrigação de citações
`path:Lx-Ly` → resposta ancorada no código real.

## Indexação

- `POST /api/v1/repos` `{repository, branch?}` → cria índice (`indexing`)
  e enfileira o job (`RepoIndexWorker`, clone shallow `--depth 1`).
- `.openhands/indexignore` no repo: globs estilo `.gitignore` (comentários `#`,
  `**`, `*`, `?`; padrão sem `/` casa em qualquer nível).
- Limites: `Index:MaxFiles` (4000), `Index:MaxFileKb` (256),
  `Index:MaxRepoMb` (200), `Index:Embeddings=off`.
- Push no webhook SCM → índice marcado `stale`;
  `POST /{id}/reindex` re-enfileira.
- Conteúdo passa pelo `SecretRedactor` antes de persistir.

## Busca

- SQLite: FTS5 shadow table `code_units_fts` (bm25, termos com prefixo).
- Postgres/fallback: ILIKE por termo + score com peso no path.
- `GET /api/v1/repos/{id}/search?q=` (20 hits).

## Ask sessions

- `POST /api/v1/ask-sessions` `{repository, question}` → 400
  `index_required` se não indexado, 409 `index_in_progress` se indexando.
- Follow-ups: `POST /{id}/messages` `{question}`.
- `POST /{id}/promote` → gera plan_document com o transcript + citações e
  cria a conversa real (`AgentType=plan`).

## UI

- Home: toggle **Ask | Agent** no composer — Ask cria sessão e navega
  para `/ask/{id}` (citações clicáveis, botão *Promote to task*).
- `/admin/repos`: adicionar repo, status, reindex, delete.

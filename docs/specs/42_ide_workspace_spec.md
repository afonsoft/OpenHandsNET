# SPEC — 42 · IDE Workspace Tab (Explorer + Monaco + SCM + Search + Tests)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `ide-workspace` |
| Type | `Feature / Client + API` |
| Stack | `Blazor WASM · Monaco (vendored) · ISandboxExecutor` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #108)` |
| Ticket | [#100](https://github.com/afonsoft/OpenHandsNET/issues/100) |
| Depends on | `03` (sandbox/executor), `05` (layout conversa), `12` (git), `21` (Monaco), `24` (exec/stream) |
| Soft dep | `43` (CheckpointList embutida no SCM — degrada se ausente) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `Pages/Ide.razor`, `Ide*` components, `WorkspaceFileEndpoints`, `IdeQuickOpenFuzzy.cs` |
| Touches | `src/OpenHandsNET.Api/Endpoints` (write/search proxy), `src/OpenHandsNET.Client` (nova aba + componentes Ide*), `MonacoInterop` |

## 1. User Story

**As a** usuário inspecionando o workspace do agente
**I want** uma aba IDE dentro da conversa — explorer, editor Monaco
editável, busca global, painel SCM (changes/diff/commit), runner de
testes e quick-open
**So that** eu reviso e ajusto o trabalho do agente sem sair da página —
hoje só existem abas read-only (`files`/`diff`) e a alternativa é abrir o
code-server inteiro (SPEC-40) noutra aba do browser.

**Contexto:** fork entrega workbench completo (`Ide*`: activity bar,
explorer, editor, SCM, search, tests, bottom panel, quick-open,
status bar) — porém em **CodeMirror 6 + LSP**. Decisão travada: **Monaco**
(já vendored + `MonacoInterop` com `CreateEditorAsync`/diff/`LanguageFor`),
**LSP/diagnostics fora do MVP**. Backend: sandbox-agent já expõe
`PUT /files/{path}` e `/exec` — a Api só precisa de proxies; `GET files`,
`git/changes|diff|log` já existem; falta **commit endpoint** e
**busca global**.

## 2. Scope (RF)

### RF-001 — Workspace write/search API

- `PUT /api/v1/app-conversations/{id}/file?path=` — body `{content_base64,
  etag?}` → `ISandboxExecutor.WriteFileAsync` no `/workspace/repo`;
  `GET /{id}/file` passa a retornar `ETag: sha256(content)`; PUT com etag
  divergente → **409** `{current_etag}` (optimistic concurrency).
- `POST /{id}/files/mkdir|rename|delete` — operações via `POST /{id}/exec`
  one-shot existente (zero mudança no sandbox-agent) ou proxy de novos
  verbos do agent — decisão na implementação, preferir exec.
- `GET /{id}/files/search?q=&glob=` — `grep -rIn` via exec (cap 500
  matches, 30s timeout), retorna `{path,line,col,preview}[]`.
- Path guard: rejeita `..`/absoluto fora de `/workspace/repo` → 400.

### RF-002 — Aba `ide` no ConversationPage

- Nova aba `ide` (ordem: `chat | ide | terminal | browser | files | diff |
  commits | usage`): layout workbench — `IdeActivityBar` (explorer,
  search, scm, tests), side panel, área de editor com tabs, `IdeStatusBar`.
- Abas `files`/`diff` atuais **permanecem** (leves); a aba IDE é o
  workbench completo. Empty state: sem repo → call-to-action.

### RF-003 — Explorer

- File tree lazy (reuse do `FilesTab`), decorations git
  (`git/changes` → M/A/D badges), context menu: new file/folder, rename,
  delete (via endpoints RF-001), refresh on `files_changed` event.

### RF-004 — Editor Monaco editável

- `MonacoInterop.CreateEditorAsync(readonly:false)` + tabs com dirty-dot,
  `Ctrl+S` salva via PUT com ETag (conflito → banner "arquivo mudou —
  recarregar/forçar"), `LanguageFor` já existe, autosave opcional off.
- Click em resultado de busca/SCM abre o arquivo na linha.

### RF-005 — SCM view

- Lista `git/changes` (reuse), click → Monaco diff (reuse `DiffTab`
  internals), commit box (message) → **`POST /{id}/git/commit`** (novo —
  `GitService.CommitAsync` já existe), staged/all-toggle simples.
- `CheckpointList` (SPEC-43) embutida abaixo quando endpoint existir.

### RF-006 — Search view

- Input com debounce 400ms → `files/search`; resultados agrupados por
  arquivo com preview; click abre editor na linha.

### RF-007 — QuickOpen (Ctrl+P dentro da aba)

- Port de `IdeQuickOpenFuzzy.cs` (MIT) + lista de paths (tree cache);
  abre arquivo no editor.

### RF-008 — Tests panel

- Comando configurável (`openhandsnet.yaml` `ide.test_command` ou default
  heurístico `dotnet test`/`npm test` por marker file) → exec via
  `POST /{id}/exec` (stream existente); painel mostra output + exit code;
  parse estruturado (pass/fail por arquivo) fica follow-up.

### RF-009 — StatusBar

- Branch (`git log -1`/changes header), dirty count, cursor `line:col`
  (evento Monaco), language do arquivo ativo.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| PUT | `/api/v1/app-conversations/{id}/file?path=` | `{content_base64, etag?}` → `{etag}`; 409 conflito |
| POST | `/api/v1/app-conversations/{id}/files/{mkdir,rename,delete}` | `{path}`/`{from,to}` |
| GET | `/api/v1/app-conversations/{id}/files/search?q=` | `[{path,line,col,preview}]` cap 500 |
| POST | `/api/v1/app-conversations/{id}/git/commit` | `{message, files?}` → `{sha}` |

GET `/file` existente passa a emitir header `ETag`. Demais rotas inalteradas.

## 4. Data model

Sem migration. Estado de editor (tabs/dirty) é client-side.

## 5. NFR / Security

- Path traversal guard em **toda** rota de file (`..`, symlink fora do
  root → 400); escrita cap 5MB/arquivo.
- ETag = sha256 conteúdo — optimistic lock entre usuário e agente (o
  agente escreve via tools no mesmo workdir: conflito explícito, nunca
  sobrescrita silenciosa).
- Todas as rotas: owner/org scope (mesmo guard das rotas git atuais).
- Exec para mkdir/rename/delete: comando fixo server-side (nunca shell
  arbitrária do client) — args escapados.

## 6. Task Plan

1. PUT file + ETag + path guard + proxies exec (mkdir/rename/delete) +
   files/search + git/commit endpoint; testes de contrato.
2. `MonacoInterop` modo editável + tabs/dirty/save+conflito.
3. Workbench shell: aba `ide`, `IdeActivityBar`, layout, `IdeStatusBar`.
4. Explorer (tree+decorations+context menu) + integração `files_changed`.
5. SCM view (changes/diff/commit) + CheckpointList embed (soft-dep 43).
6. Search view + QuickOpen fuzzy port.
7. Tests panel (exec stream) + polish + bUnit + `dotnet test` verde.

## 7. Deliverables

Endpoints write/search/commit, `Client/Components/Ide/*` (ActivityBar,
Explorer, EditorTabs, ScmView, SearchView, TestsPanel, QuickOpen,
StatusBar), `MonacoInterop` editável, aba `ide` na ConversationPage,
testes.

## 8. Referências

- `afonsoft/open-webui` (MIT): `Pages/Ide.razor`, `Components/Ide*.razor`,
  `Api/Endpoints/WorkspaceFileEndpoints.cs` (ETag/mkdir/search/commit),
  `Utils/IdeQuickOpenFuzzy.cs` — modelo de port (trocar CM6→Monaco).
- `docs/specs/21` (Monaco vendored), `docs/specs/40` RF-003 (code-server —
  alternativa externa, não conflita), `docs/specs/12` (GitService).

## 9. Guardrails

- **Monaco, não CodeMirror** — sem segundo editor no bundle; LSP/
  diagnostics/intellisense **fora do MVP** (follow-up dedicado).
- Sem iframe do code-server na aba IDE (surface SPEC-40 continua externa).
- Escrita só via endpoints da Api — nunca direto no sandbox-agent
  (auth/audit na borda).
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- Aba `ide` abre explorer → edita arquivo → Ctrl+S persiste no workspace
  (visível p/ agente e aba `files`); conflito ETag → 409 tratado.
- SCM lista changes, diff abre, commit cria commit real (`git log`).
- Busca acha texto no workdir e abre na linha; Ctrl+P navega.
- Tests panel roda `dotnet test` e mostra output/exit.
- `dotnet test` verde; bUnit cobre explorer/scm/editor.

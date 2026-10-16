# SPEC — 45 · Composer (Mentions, Slash Commands, Drafts, Uploads)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `composer` |
| Type | `Feature / Client + API` |
| Stack | `Blazor WASM · ISandboxExecutor (upload)` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `In review (PR #111)` |
| Ticket | [#103](https://github.com/afonsoft/OpenHandsNET/issues/103) |
| Depends on | `04` (send-message), `12` (files API), `25` (repo skills catalog) |
| Reference | `afonsoft/open-webui` (MIT — port permitido): `ChatView` composer (@menções, /slash, drafts, histórico), `RepoSlashCommands.cs`, `AttachmentThumb` |
| Touches | `src/OpenHandsNET.Client` (`ChatComposer.razor` novo, Home, ChatPane), `src/OpenHandsNET.Api/Endpoints` (uploads) |

## 1. User Story

**As a** usuário dirigindo o agente
**I want** um composer rico — `@` para referenciar arquivos do workspace,
`/` para slash commands de skills/ações, rascunho persistente, histórico
de input e anexo de arquivos
**So that** eu escrevo prompts precisos e curtos sem digitar paths nem
repetir instruções — hoje: `<input>` single-line cru (ChatPane) e
textarea básica (Home), sem mention/skill/draft/anexo.

**Contexto:** fork tem tudo isso embutido no `ChatView` (mentions com
file-tree fuzzy, slash mergeando builtins+skills+prompts com
`$ARGUMENTS`, drafts+histórico em localStorage, anexos `FileIds`).
OpenHandsNET já tem os ingredientes: `GET /{id}/files` (tree),
`GET /{id}/skills` (repo skills SPEC-25), `POST /{id}/mode` +
`/context/compact` (builtins), `WriteFileAsync` (upload path).
Decisões travadas: anexos = **upload p/ workspace + path reference**
(sem vision/binary inline); voice fora.

## 2. Scope (RF)

### RF-001 — `ChatComposer.razor` compartilhado

- Extrai composer único usado por `Home` (start-task) e `ChatPane`:
  textarea multi-line auto-grow (JS, max ~10 linhas), `Enter` envia /
  `Shift+Enter` quebra, contador de chars, estado de run (spinner/stop
  quando `Running`).
- ChatPane atual `<input>` é substituído; `Send` continua via
  `OpenHandsNetApiClient`/`ConversationHubClient.SendUserMessage`.

### RF-002 — `@` mentions (arquivos do workspace)

- Digitar `@` abre popup fuzzy sobre o file tree (`GET /{id}/files?dir=`
  lazy + cache client, reuse `IdeQuickOpenFuzzy`-style scoring quando 42
  land — duplicar helper mínimo até lá).
- Selecionado vira **chip** `@src/foo.cs` removível (backspace no fim
  remove chip inteiro); texto enviado mantém token `@path` inline —
  referência de path apenas (agente resolve via `file_read`; **não**
  injeta conteúdo).
- Cap 10 chips/mensagem; mention a path inexistente (workdir mudou)
  renderiza chip stale (não bloqueia envio).

### RF-003 — `/` slash commands

- Popup ao digitar `/` no início; fontes mescladas:
  - **Builtins**: `/plan`→`POST /{id}/mode plan`, `/build`→mode build,
    `/compact`→`POST /{id}/context/compact`, `/new`→nova conversa.
  - **Repo skills**: `GET /{id}/skills` (SPEC-25) — prompt template da
    skill com `$ARGUMENTS`/`$1..$N` interpolado no texto enviado.
  - **User skills**: `GET /api/v1/skills` (SPEC-11).
- Execução é **client-side**: slash expande para texto/ação — o wire
  continua `send-message` comum (auditável, sem endpoint novo de comando).

### RF-004 — Drafts + histórico

- Draft por conversa em `localStorage ohn.draft.{convId}` (restaura ao
  abrir; limpa ao enviar); draft da Home em `ohn.draft.new`.
- Histórico `↑`/`↓` no composer vazio/fim — últimas 50 mensagens
  enviadas **daquela conversa** (`ohn.history.{convId}`).

### RF-005 — Anexos (upload → path reference)

- Botão attach → file picker (`.png,.jpg,.txt,.md,.pdf`… cap **10MB**)
  → `POST /{id}/uploads` (multipart) → `WriteFileAsync` em
  `/workspace/uploads/{yyyyMMdd}/{sanitized-name}` → retorna `{path}`.
- Chip no composer com nome; no envio, append `\n\n[anexo: {path}]` ao
  texto. Sem inline/vision — modelo acessa via `file_read`/tools.
- Sanitize nome (slug), rejeita path traversal, quota de uploads por
  conversa (cap 100MB — `UsageLimits` existente se aplicável).

### RF-006 — Edit & resend

- Hover na última `MessageEvent` do usuário → "Editar" carrega o texto
  no composer (reenvia como **nova** mensagem; evento original intacto
  — event stream é append-only, nunca rewrite).

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/app-conversations/{id}/uploads` | multipart `file` → `{path}`; 413 >10MB; 400 nome inválido |

Demais fontes são rotas existentes (`files`, `skills`, `mode`,
`context/compact`, `send-message`).

## 4. Data model

Sem migration — drafts/histórico client-side (localStorage); uploads
são arquivos no workspace do sandbox (limpeza segue lifecycle do sandbox,
SPEC-16).

## 5. NFR / Security

- Upload: extensão allowlist + MIME sniff básico (magic bytes p/ imagem),
  cap 10MB, sem execução — é só arquivo no workdir.
- `$ARGUMENTS` de skill é prompt-template — usuário vê/expande antes do
  envio (nunca exec cega).
- Mention/slash queries cancellable + debounce (≤1 req/300ms).
- Attachments não saem do sandbox; audit `file_upload` em access log.

## 6. Task Plan

1. `ChatComposer.razor` base (textarea/auto-grow/estado) + swap Home +
   ChatPane; bUnit.
2. Uploads endpoint + WriteFile + sanitize/quota + testes.
3. @mentions (popup fuzzy + chips + cache tree); bUnit.
4. /slash (builtins + repo/user skills + `$ARGUMENTS`); testes expansão.
5. Drafts + histórico + edit-resend; bUnit.
6. Polish mobile (SPEC-15) + `dotnet test` verde.

## 7. Deliverables

`ChatComposer.razor`, `UploadEndpoints`, `MentionPopup`/`SlashPopup`,
`ComposerState` (draft/history), `AttachmentThumb` adaptado, testes
unit+client.

## 8. Referências

- `afonsoft/open-webui` (MIT): `ChatView.razor` (mention regex/chips,
  slash popup, drafts, edit-resend), `Services/RepoSlashCommands.cs`,
  `Components/AttachmentThumb.razor`,
  `docs/specs/SPEC-20261009-repo-skills-slash-commands.md`.
- `.specs/25` (repo skills + `/{id}/skills`), `.specs/22`
  (`context/compact`), `.specs/29` (`mode`).

## 9. Guardrails

- Slash/mention expandem no client — wire continua `send-message` comum.
- Anexo = path reference; **sem** vision/binary inline no MVP.
- Skills de repo executam como prompt, nunca código client-side.
- `.github/workflows/` intocado; `main` só via PR.

## 10. DoD

- `@arq` vira chip e chega como `@path` no evento da mensagem.
- `/plan` muda mode; `/skill-foo args` expande template e envia.
- Recarregar página → draft restaura; `↑` recupera último envio.
- Upload de imagem → `{path}` em `/workspace/uploads` + chip + envio.
- `dotnet test` verde; bUnit cobre popup/chips/draft/expansão.

# SPEC — 21 · Client Completion (Admin UI, Monaco, Tailwind v4)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `client-completion` |
| Type | `Feature / UI` |
| Stack | `Blazor WASM · Monaco Editor · Tailwind CSS v4` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` (PR #49) |
| Ticket | [#45](https://github.com/afonsoft/OpenHandsNET/issues/45) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `05_devin_ui_integration_spec`, `08_enterprise_security_rbac` (admin endpoints), `14_enterprise_operations_spec` (support-bundle), `20_plan_enforcement_billing_spec` (plans endpoints) |
| Touches | `OpenHandsNET.Client` (Pages, Services, Layout, wwwroot), `OpenHandsNetApiClient` |

## 1. User Story

**As a** super-admin e usuário do Client
**I want** páginas de administração (users, super-admins, audit, feature-flags,
plans, usage, support-bundle), editor Monaco para arquivos/diffs e o pipeline
Tailwind v4 instalado
**So that** a superfície enterprise deixa de ser só-REST e o frontend ganha o
editor e o sistema de utilidades previstos no stack.

**Problem context:**

- Admin surface é só endpoints: `/admin/super-admins`, `/admin/users`,
  `/admin/audit`, `/admin/feature-flags`, `/support-bundle`
  (`AdminEndpoints.cs`) + plans do SPEC-20 — nenhuma página no Client
  (`Pages/` tem 6 arquivos, nenhum admin).
- Diffs renderizam em `<pre>` colorido (`DiffTab.razor`); files em `<pre>`
  readonly — SPEC-05 previa Monaco (deferido).
- Tailwind v4 está no stack (CLAUDE.md) mas o Client usa `app.css` 100%
  hand-written — nenhum build step existe.

## 2. Scope

**In scope:**

- **RF-001 — Gate de admin**: `GET /admin/super-admins/me` no boot →
  `AdminState.IsSuperAdmin`; item "Admin" no nav só aparece se true;
  rotas `/admin/*` redirecionam para `/` quando false (client-side gate —
  enforcement real continua no servidor).
- **RF-002 — Páginas admin** (`Pages/Admin/`):
  - `AdminIndexPage` `/admin` — cards com counts (users, orgs, audit link,
    support-bundle download button).
  - `AdminUsersPage` `/admin/users` — search `?q=`, tabela paginada, delete
    (confirmação), count.
  - `AdminSuperAdminsPage` `/admin/admins` — lista, grant por email, revoke
    (409 last-admin mostra erro).
  - `AdminAuditPage` `/admin/audit` — filtros user/action/range, tabela
    paginada, botão "Export CSV" (link `?format=csv`).
  - `AdminFlagsPage` `/admin/flags` — lista de flags, toggle `PUT`, delete.
  - `AdminPlansPage` `/admin/plans` — CRUD de plans (SPEC-20 endpoints) +
    assign `PUT /admin/organizations/{id}/plan` via org picker.
  - `AdminUsagePage` `/admin/usage` — org picker + `/organizations/{id}/usage`
    (tokens/custo/runs/sandbox-s por período).
- **RF-003 — `AdminApiClient`** (Client service): wrappers tipados dos
  endpoints acima; reusa o `HttpClient` autenticado (cookie same-origin).
  Erros 401/403 → mensagem amigável + esconde a página.
- **RF-004 — Monaco**: vendored `monaco-editor@0.52` `min/vs` em
  `wwwroot/lib/monaco/` (via npm, committed assets — mesmo padrão do
  `lib/xterm`); `monacoInterop.js` com `createEditor/createDiffEditor/
  setValue/dispose`. `FilesTab` vira Monaco readonly (syntax highlight por
  extensão); `DiffTab` vira `DiffEditor` — original via
  `GET /api/v1/git/file?path=&ref=HEAD` (endpoint novo, conteúdo HEAD do repo)
  vs conteúdo atual do workspace. Fallback `<pre>` se JS interop falhar.
- **RF-005 — Tailwind v4 pipeline**: `package.json` no Client com
  `tailwindcss@^4` + `@tailwindcss/cli`; `Styles/app.tailwind.css` com
  `@import "tailwindcss";` + `@source "../**/*.razor"`; script
  `npm run css` → `wwwroot/tailwind.css` (**commitado** — CI não precisa de
  node); `index.html` inclui `tailwind.css` **depois** de `app.css` (camada
  de utilidades acima do CSS custom — zero regressão visual; migração de
  componentes é incremental e fora desta SPEC).
- **RF-006 — `GET /api/v1/git/file`**: query `path` + `ref` (default HEAD) no
  `GitEndpoints` — lê arquivo do repo do workspace na revisão (para o diff
  editor). 404 fora do repo/arquivo inexistente.

**Out of scope:**

- Reescrever componentes para utility-classes (incremental, fora do escopo).
- Monaco write-back (edição de arquivo salvando no workspace) — v1 readonly.
- Diff de imagens/binários; blame; minimap config UI.
- CI build step de CSS (artefato commitado — workflows intactos por guardrail).

## 3. Design

- `AdminApiClient` segue o padrão de `OpenHandsNetApiClient` (typed methods,
  `JsonNamingPolicy.SnakeCaseLower`).
- Nav: `MainLayout`/nav component consulta `AdminState` (carregado 1× no
  `OnInitialized` do app shell, cacheado).
- Monaco loader: JS module carrega `lib/monaco/min/vs/loader.js`,
  `require.config({paths:{vs:...}})`; Blazor chama via `IJSRuntime`.
- Tailwind: `npm ci && npm run css` documentado; `tailwind.css` gerado é
  commitado (diff reviewable) — desenvolvedor regenera ao mudar markup.

## 4. API Contract

| Endpoint | Notas |
|---|---|
| `GET /api/v1/git/file?path={p}&ref={r}` | novo — conteúdo do arquivo na rev (`path` confinado ao repo) |

Demais endpoints já existem (SPEC-08/14/20).

## 5. Critérios de Aceite

- [ ] Login como super-admin → nav mostra Admin; `/admin/users` lista, busca
  e deleta; `/admin/admins` grant/revoke com erro no último admin.
- [ ] `/admin/audit` filtra e exporta CSV; `/admin/flags` toggle persiste;
  `/admin/plans` cria/edita/atribui plano; `/admin/usage` mostra agregados.
- [ ] Não-admin → nav esconde Admin e `/admin/*` redireciona.
- [ ] FilesTab abre arquivo em Monaco com highlight; DiffTab mostra diff
  side-by-side real (HEAD vs workspace).
- [ ] `npm run css` gera `tailwind.css`; classes utilitárias funcionam num
  componente de exemplo (ex.: badge de status no AdminIndex).
- [ ] Support-bundle baixa o zip autenticado.

## 6. NFR

- Monaco vendored (sem CDN — funciona offline/airgapped).
- `tailwind.css` minificado no build (`--minify`).
- Zero mudança de `.github/workflows/`; zero mudança visual sem Tailwind
  classes novas (css custom intacto).

## 7. Task Plan

1. `AdminApiClient` + `AdminState` + gate de nav/rotas.
2. Páginas admin (index → users → admins → audit → flags → plans → usage).
3. `GET /git/file` endpoint + client method.
4. Monaco vendor + interop + FilesTab/DiffTab.
5. Tailwind pipeline (package.json, app.tailwind.css, npm script, index.html).
6. bUnit tests (gate admin, páginas renderizam com mock client) + endpoint
   test do `/git/file`.
7. Docs `docs/ui/admin.md` + `.env.exemplo` n/a + index.

## 8. Deliverables

```text
src/OpenHandsNET.Client/Services/AdminApiClient.cs
src/OpenHandsNET.Client/Services/AdminState.cs
src/OpenHandsNET.Client/Pages/Admin/{AdminIndexPage,AdminUsersPage,
  AdminSuperAdminsPage,AdminAuditPage,AdminFlagsPage,AdminPlansPage,
  AdminUsagePage}.razor
src/OpenHandsNET.Client/wwwroot/lib/monaco/            # vendored min/vs
src/OpenHandsNET.Client/wwwroot/js/monacoInterop.js
src/OpenHandsNET.Client/package.json + Styles/app.tailwind.css
src/OpenHandsNET.Client/wwwroot/tailwind.css           # gerado, commitado
src/OpenHandsNET.Client/Components/Conversation/{FilesTab,DiffTab}.razor
src/OpenHandsNET.Api/Endpoints/GitEndpoints.cs         # /git/file
tests/OpenHandsNET.ClientTests/AdminPagesTests.cs      # bUnit
```

## 9. Referências

- SPEC-05 (multi-pane UI + deferral Monaco/Tailwind), SPEC-08 RF-008/011
  (admin surface), SPEC-14 RF-004 (support-bundle), SPEC-20 (plans).
- https://microsoft.github.io/monaco-editor/ — editor/diff API.
- https://tailwindcss.com/docs/installation — v4 `@import "tailwindcss"`.

## 10. Guardrails

- Nada de CDN para Monaco (vendor local — airgap-safe).
- `app.css` existente não é reescrito nesta SPEC (camadas coexistem).
- Enforcement de segurança é server-side — gate do client é UX apenas.
- Sem alterar `.github/workflows/`.

## 11. DoD

- [ ] 7 páginas admin funcionais contra a API real; gate não-admin ok.
- [ ] Monaco renderiza file + diff side-by-side; fallback pre ok.
- [ ] `tailwind.css` commitado e utilidades aplicáveis.
- [ ] `dotnet build` + `dotnet test` verdes; CI do PR verde.

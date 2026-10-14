# Admin UI (SPEC-21)

A área administrativa do Client espelha os endpoints `/api/v1/admin/*`
para super-admins. **Gate é só UX** — o servidor sempre revalida a
permissão `manage_super_admins`; quem não é admin nem vê o ícone e é
redirecionado de `/admin/*` para `/`.

## Boot/gate

`AdminState.EnsureLoadedAsync()` chama `GET /admin/super-admins/me` uma
vez por sessão e cacheia. `MainLayout` esconde o item 🛠️ quando falso;
`AdminPageBase` (base de todas as páginas) redireciona não-admins e
traduz `AdminDeniedException` (401/403) em mensagem amigável.

## Páginas

| Rota | Página | Endpoints usados |
|---|---|---|
| `/admin` | Overview | `users/count`, `plans`, `feature-flags`, `audit?total` + link do support-bundle |
| `/admin/users` | Users | `GET/DELETE /admin/users`, `users/count` — busca `?q=`, pager 50/página |
| `/admin/admins` | Super admins | `GET/POST/DELETE /admin/super-admins` — grant por email; 409 "last admin" vira notice |
| `/admin/audit` | Audit | `GET /admin/audit?user&action&from&to&page` + `?format=csv` (download) |
| `/admin/flags` | Feature flags | `GET/PUT/DELETE /admin/feature-flags` — toggle inline |
| `/admin/plans` | Plans | `GET/POST/PATCH/DELETE /admin/plans` + `PUT /admin/organizations/{id}/plan` (picker de org) |
| `/admin/usage` | Usage | `GET /organizations` (picker) + `GET /organizations/{id}/usage` e `…/plan` |

Support-bundle: `<a href="/api/v1/support-bundle" download>` — cookie
same-origin autentica; o zip já chega redigido (secrets mascarados).

## AdminApiClient

`Services/AdminApiClient.cs` — wrappers tipados sobre o `HttpClient`
autenticado (cookie). Convenções: 401/403 lançam `AdminDeniedException`;
409/404 viram `false` + callback `conflict(detail)` nas operações
destrutivas. Todos os payloads seguem `snake_case` (policy global).

## Monaco (RF-004)

`wwwroot/lib/monaco/min/vs` vendored de `monaco-editor@0.52.2` (npm pack —
sem CDN, funciona airgapped). `js/monacoInterop.js` carrega o AMD loader
sob demanda e expõe `createEditor` / `createDiffEditor` / `setValue` /
`setDiff` / `dispose`; `Services/MonacoInterop.cs` é o wrapper tipado +
mapa extensão→linguagem.

- `FilesTab`: arquivo abre em editor readonly (`vs-dark`, sem minimap).
- `DiffTab`: chips por arquivo alterado; lado esquerdo = `HEAD` via
  `GET /{id}/git/file?path=&ref=HEAD`, lado direito = conteúdo atual do
  workspace via `GET /{id}/file`.
- Fallback: qualquer `JSException` volta a renderizar `<pre>`.

## Tailwind v4 (RF-005)

Pipeline **offline do CI** — o artefato `wwwroot/tailwind.css` é
commitado:

```bash
cd src/OpenHandsNET.Client
npm ci
npm run css        # → wwwroot/tailwind.css (minificado)
npm run css:watch  # desenvolvimento
```

`Styles/app.tailwind.css` faz `@import "tailwindcss"` +
`@source` dos `.razor`. `index.html` carrega `tailwind.css` **depois** de
`app.css` — utilidades vencem o custom quando usadas; markup atual não
usa classes Tailwind ainda (migração incremental).

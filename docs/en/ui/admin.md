# Admin UI (SPEC-21)

The Client's admin area mirrors the `/api/v1/admin/*` endpoints for
super-admins. **The gate is UX-only** — the server always re-validates
the `manage_super_admins` permission; non-admins don't see the icon and
are redirected from `/admin/*` to `/`.

## Boot/gate

`AdminState.EnsureLoadedAsync()` calls `GET /admin/super-admins/me` once
per session and caches it. `MainLayout` hides the item when false;
`AdminPageBase` (base of all admin pages) redirects non-admins and
translates `AdminDeniedException` (401/403) into a friendly message.

## Pages

| Route | Page | Endpoints used |
|---|---|---|
| `/admin` | Overview | `users/count`, `plans`, `feature-flags`, `audit?total` + support-bundle link |
| `/admin/users` | Users | `GET/DELETE /admin/users`, `users/count` — `?q=` search, 50/page pager |
| `/admin/admins` | Super admins | `GET/POST/DELETE /admin/super-admins` — grant by email; 409 "last admin" becomes a notice |
| `/admin/audit` | Audit | `GET /admin/audit?user&action&from&to&page` + `?format=csv` (download) |
| `/admin/flags` | Feature flags | `GET/PUT/DELETE /admin/feature-flags` — inline toggle |
| `/admin/plans` | Plans | `GET/POST/PATCH/DELETE /admin/plans` + `PUT /admin/organizations/{id}/plan` (org picker) |
| `/admin/usage` | Usage | `GET /organizations` (picker) + `GET /organizations/{id}/usage` and `…/plan` |

Support-bundle: `<a href="/api/v1/support-bundle" download>` — the
same-origin cookie authenticates; the zip arrives already redacted
(masked secrets).

## AdminApiClient

`Services/AdminApiClient.cs` — typed wrappers over the authenticated
(cookie) `HttpClient`. Conventions: 401/403 throw
`AdminDeniedException`; 409/404 become `false` + `conflict(detail)`
callback on destructive operations. All payloads follow `snake_case`
(global policy).

## Monaco (RF-004)

`wwwroot/lib/monaco/min/vs` vendored from `monaco-editor@0.52.2`
(npm pack — no CDN, works airgapped). `js/monacoInterop.js` loads the
AMD loader on demand and exposes `createEditor` / `createDiffEditor` /
`setValue` / `setDiff` / `dispose`; `Services/MonacoInterop.cs` is the
typed wrapper + extension→language map.

- `FilesTab`: file opens in a readonly editor (`vs-dark`, no minimap).
- `DiffTab`: per-changed-file chips; left side = `HEAD` via
  `GET /{id}/git/file?path=&ref=HEAD`, right side = current workspace
  content via `GET /{id}/file`.
- Fallback: any `JSException` reverts to `<pre>` rendering.

## Tailwind v4 (RF-005)

Pipeline **offline from CI** — the `wwwroot/tailwind.css` artifact is
committed:

```bash
cd src/OpenHandsNET.Client
npm ci
npm run css        # → wwwroot/tailwind.css (minified)
npm run css:watch  # development
```

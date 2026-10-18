# Service Users & Scoped Keys

SPEC-57 — identidades não-humanas (CI/CD, integrações) com chaves
`svc_*` escopadas e impersonation auditada.

## Conceitos

- **Service user** — identidade separada de `users` por org; não faz
  login, só consome API via Bearer `svc_*`. Tem role de org
  (`owner|admin|member`) que define o teto de permissões.
- **Scoped key** — `svc_*` carrega `scopes[]` (ex.:
  `conversations:write`, `sandboxes:read`, `sessions:impersonate`,
  `admin:service_users`). O endpoint declara o escopo exigido via
  `.RequireScope("…")`; `403 {"error":"insufficient_scope"}` genérico.
- **sk-* personal** — inalterada: `ActorKind=user`, sem restrição de
  escopo (compat total).
- **Impersonation** — `POST /api/v1/app-conversations` com
  `create_as_user_id` cria a sessão em nome de outro usuário da mesma
  org (requer `sessions:impersonate`). Usage/quota atribuída ao alvo;
  `conversations.created_via = service_user:{id}`.

## Admin endpoints

| Method | Path |
|---|---|
| GET/POST | `/api/v1/orgs/{org}/service-users` |
| PATCH/DELETE | `…/service-users/{id}` (rename/role/disable) |
| POST | `…/service-users/{id}/keys` `{name, scopes[], expires_at?}` |
| GET/DELETE | `…/service-users/{id}/keys[/{keyId}]` |
| GET | `/api/v1/keys/current` → `{actor_kind, scopes, role, org_id}` |

Tudo sob `perm:manage_api_keys` + scope `admin:service_users`.

## Segurança

- `svc_*` segue o mesmo formato das `sk-*`: prefixo visível, SHA-256
  at rest, plaintext exibido uma única vez.
- Desabilitar o service user revoga futuras resoluções na hora
  (`ValidateAsync` re-checa `DisabledAt`); deletar remove as chaves.
- Impersonation nunca eleva — o alvo continua sujeito às próprias
  permissões e quotas.
- Audit: `service_user.created`, `service_user.key_issued`,
  `session.impersonated`, `auth.scope_denied`.

## CLI

```bash
openhandsnet service-users list --org <org-id>
openhandsnet service-users create --name ci-bot --role member --org <org-id>
openhandsnet service-users key --id <svc-id> \
  --scopes "conversations:write sandboxes:read" --org <org-id>
# plaintext exibido uma única vez
```

## Scope sweep

Scopes declarados hoje: `automations:*`, `secrets:write`,
`admin:service_users`, `sessions:impersonate` (checagem imperativa no
create). `conversations:*`/`sandboxes:*` reservados no catálogo
`ApiScopes` — endpoints sem `RequireAuthorization` não ganham scope
(não forçar auth onde não havia).

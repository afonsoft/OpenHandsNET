# Identity — SCIM 2.0, IdP groups e usage tiers

SPEC-58 — provisioning via IdP (Okta/Entra) + mapeamento de grupos para
orgs/roles/tiers de quota.

## SCIM 2.0 (service provider)

Base: `/scim/v2` · auth `Bearer scim_*` (token por org, emitido em
`/admin/identity` ou `POST /api/v1/orgs/{org}/scim-tokens`) · content
type `application/scim+json`.

| Path | Operações |
|---|---|
| `/ServiceProviderConfig`, `/ResourceTypes`, `/Schemas` | discovery estático |
| `/Users` | `GET` (filter `userName eq "…"`), `POST` (provisiona: user + membership `source=idp`) |
| `/Users/{id}` | `GET`, `PATCH` (`active:false` → `DisabledAt`), `DELETE` (soft) |
| `/Groups` | `GET`, `POST` (cria `idp_group_mapping` role=member + sincroniza membros) |
| `/Groups/{id}` | `PUT` (displayName + replace members), `DELETE` |

Erros seguem `{schemas:[error], status, detail, scimType}` —
`400 invalidFilter/invalidSyntax`, `401`, `404`, `409 uniqueness`.

## IdP group mapping

`idp_group_mappings`: `idp_group` (valor do claim) → `role` +
`usage_tier_id` opcional. No `POST /api/v1/authenticate` os claims
`groups`/`group`/`…/identity/claims/groups` do token são aplicados:

- match → membership criada/atualizada com `source=idp` (idempotente);
- grupo removido no IdP → membership `source=idp` revogada no próximo
  login (`idp.membership_revoked`);
- membership manual nunca perde a role — mas recebe o `usage_tier_id`
  do mapping (tier é configuração de quota, não de acesso).

## Usage tiers

`usage_tiers` (`LimitsJson` no formato dos planos). Resolução do limite
efetivo do usuário, em ordem:

1. `user_usage_overrides` manual não-expirado
   (`POST/DELETE /api/v1/users/{id}/usage-override`, admin);
2. `usage_tier_id` gravado na membership (via mapping);
3. plano da org (subscription → `org.PlanId` → free).

Aplica-se em `max_conversations_per_day` (create), `max_concurrent_runs`,
`max_tokens_per_month`, `max_sandbox_seconds_per_month` (run start) —
a quota é sempre do **ator** (em impersonation, do alvo).

## Admin

- `/admin/identity` — mint/revoke `scim_*`, mappings grupo→role/tier,
  CRUD de tiers.
- Audit: `scim.user_provisioned`, `scim.user_deprovisioned`,
  `idp.group_matched`, `idp.membership_revoked`, `usage.tier_applied`.
- `scim_*` não chama `/api/v1` (escopo único); usuário provisionado via
  SCIM só acessa após primeiro login OIDC validado.

# SPEC-79: Org-scoped resources — `org_id` da rota deve escopo as queries

| Campo | Valor |
|---|---|
| Status | Approved |
| Gap key | GAP-security-org-scoped-resources |
| Type | Security |
| Fonte | gap-analysis 2026-10-20 — evidência em `ChatOpsEndpoints.cs`, `TicketEndpoints.cs`, `ChatConnectionService.cs`, `PermissionPolicy.cs` |

## User Story

Como mantenedor da API multi-tenant, quero que o `{org_id}` declarado
na rota seja usado para **escopoar** o recurso acessado — não apenas
para avaliar a permissão — para que um chamador autorizado em org A
não consiga ler/apagar recursos de org B conhecendo o `id` (GUID).

## Contexto / evidência

Rotas org-scoped declaram `{org_id:guid}` no `MapGroup`, mas vários
handlers **não recebem `org_id`** e os serviços consultam/apagam por
`id` puro:

1. `DELETE /api/v1/orgs/{org_id}/chat-connections/{id}` —
   handler só recebe `id` (`ChatOpsEndpoints.cs:112-116`);
   `ChatConnectionService.DeleteAsync(id)` faz
   `IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id)`
   **sem filtro de OrgId** (`ChatConnectionService.cs:71-80`).
2. `GET …/chat-connections/{id}/channels` — `ListChannelConfigsAsync(id)`
   idem (`ChatOpsEndpoints.cs:118-121`).
3. `DELETE /api/v1/orgs/{org_id}/ticket-connections/{id}` —
   `TicketConnectionService.DeleteAsync(id)` mesma forma
   (`TicketEndpoints.cs:78`, `TicketConnectionService.cs:77`).
4. `GET/POST …/ticket-connections/{id}/projects` — mesmo padrão.

A `PermissionPolicy` resolve `org_id` da rota
(`ctx.Request.RouteValues["org_id"]` — `PermissionPolicy.cs:101-102`,
`:146`) para avaliar `perm:manage_integrations` **na org alvo** — logo
o cross-org está bloqueado no nível da permissão, mas a checagem NÃO
valida que o `id` do recurso pertence ao `org_id` da rota. Exploração
exige `manage_integrations` em alguma org + GUID do recurso alvo —
severidade moderada, mas o invariante de autorização está quebrado.

Efeito colateral positivo: receber `org_id` nos handlers também corrige
4 dos 6 "Path parameter is not defined" do OpenAPI (SPEC-80).

## Requisitos funcionais

1. **RF-001 — Assinaturas**: handlers das rotas org-scoped recebem
   `Guid org_id` e repassam ao serviço (chat-connections delete/channels,
   ticket-connections delete/projects).
2. **RF-002 — Escopo na query**: `DeleteAsync(id, orgId, ct)` e demais
   métodos filtram `OrgId == orgId` (ou validam `r.OrgId == orgId` e
   retornam 404/403 — preferir **404** para não vazar existência).
3. **RF-003 — Consistência**: `GET …/{id}/channels` e
   `GET/POST …/{id}/projects` só respondem se `conn.OrgId == org_id`.
4. **RF-004 — Testes**: integration tests cobrindo cross-org:
   usuário autenticado em org A + `id` de org B → **404** em delete,
   channels e projects (não 200/204).

## Critérios de aceite

- [ ] Toda rota `/orgs/{org_id}/.../{id}` valida pertencimento do `id`.
- [ ] Teste cross-org falhava antes, passa depois (red→green).
- [ ] OpenAPI: os 4 paths orgs ganham o parâmetro `org_id` declarado.

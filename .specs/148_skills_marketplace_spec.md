# SPEC — 148 · Skills/Plugins Marketplace (registry → install → 3 scopes)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `skills-marketplace` |
| Type | `Feature / Agent extensibility + Admin` |
| Stack | `.NET 10 · skills catalog (SPEC-95) · MCP · bundles` |
| Repository | `afonsoft/sandcastle` |
| Status | `Approved` |
| Ticket | `GAP-MARKETPLACE` (docs/research/2026-11-competitive-gap-analysis.md §P2) |
| Depends on | `95` (agent_catalog + `*.agent.md` layered), `123` (skills/hooks), `59` (admin perms), `131` (builtin MCP merge) |
| Reference | Devin plugins+marketplace (3 escopos: enterprise/org/user); OH `iterate` public skill — conceitos apenas |
| Touches | `src/Sandcastle.{Domain,Application,Infrastructure,Api,Client}`, `cli/`, `tests/` |

## 1. User Story

**As a** org admin / usuário
**I want** navegar um catálogo de skills/plugins (registry versionado),
instalar com um clique no escopo certo (system/org/user) e ter os
assets materializados nos agentes
**So that** paridade com o marketplace do Devin e as skills públicas
do OpenHands — hoje o Sandcastle tem catálogo de skills embutido
(SPEC-95) mas nenhuma UX de descoberta/instalação nem scopes.

## 2. Scope (RF)

### RF-001 — Registry + manifest

- `skill_registry`: fonte padrão = arquivo/registry hospedado
  (`Marketplace:RegistryUrl`, default repo-local `marketplace.json`
  versionado com o app; orgs podem apontar URL própria — self-hosted).
- Manifest por entry: `{id, name, version, description, author,
  kind (skill|mcp-server|agent|hook|bundle), tags[], min_app_version,
  source (git/url/embedded), files[], permissions_required[],
  changelog_url}`. Bundle = lista de entries.
- Conteúdo: `skill` = `SKILL.md`+assets materializados em
  `.sandcastle/skills/{id}/`; `mcp-server` = config JSON merged no
  MCP settings do escopo; `agent` = `*.agent.md`; `hook` = ruleset
  file; `bundle` = recursivo.

### RF-002 — Scopes (paridade Devin)

- `system` (bundle padrão, só admin plataforma), `org` (admin org,
  aplica a todos os usuários da org), `user` (próprio usuário).
- Precedência user→org→system para conflitos (mesma cadeia de
  settings já existente p/ authorship SPEC-147).
- Entidade `installed_skills` (scope, scope_id, entry_id, version,
  enabled, installed_by, installed_at) — toggle enable/disable sem
  desinstalar.

### RF-003 — Install pipeline

- `POST /api/v1/marketplace/install {entry_id, version?, scope}` →
  resolve manifest → valida `min_app_version` + `permissions_required`
  (admin deve aceitar explicitamente quando o entry declara
  `perm:` extras) → baixa/materializa arquivos (git sparse ou tarball
  URL, checksum sha256 do manifest) → grava `installed_skills` →
  sync nos agentes (mesmo materialize-path do SPEC-95/123).
- `GET /api/v1/marketplace/entries` (tags/q/kind/scope-filter) +
  `GET /{id}` (readme render) + `GET /api/v1/marketplace/installed`
  + `DELETE /api/v1/marketplace/installed/{id}` + PATCH toggle.
- Update flow: `installed_skills.version` vs manifest → badge
  "update available" + `POST .../upgrade`.

### RF-004 — UI + CLI + seed

- Sidebar "Marketplace": grid de entries (kind badge, install
  button por scope permitido), página do entry (readme, perms,
  changelog), aba "Installed" (enable/disable/upgrade/remove).
- CLI: `sandcastle marketplace list|install|remove|upgrade`.
- Seed registry com ~10 entries curados: `iterate` (verify/fix loop
  prompt), `pr-review-rules` pack, `dotnet-conventions`,
  `blazor-frontend`, `sql-code-review`, `k8s-ops`, `write-specs`
  (meta-skill do nosso harness exportada), `git-workflow`,
  `security-hardening`, `onboarding-tour`.

### RF-005 — Fora de escopo

- Registry público com upload de terceiros (comunidade) · assinatura
  criptográfica de entries · pagamentos · ratings/comments ·
  version pinning por conversa — fase 2.

## 3. API Contract

| Method | Path | Notes |
|---|---|---|
| GET | `/api/v1/marketplace/entries` | `?q=&kind=&tag=` cursor |
| GET | `/api/v1/marketplace/entries/{id}` | detalhe+readme |
| GET | `/api/v1/marketplace/installed` | `?scope=` |
| POST | `/api/v1/marketplace/install` | `{entry_id, version?, scope}` |
| PATCH | `/api/v1/marketplace/installed/{id}` | `{enabled}` |
| DELETE | `/api/v1/marketplace/installed/{id}` | remove+desmaterializa |
| POST | `/api/v1/marketplace/upgrade/{id}` | bump versão |

Erros: `403` scope acima do papel · `409` já instalado · `422`
permissions_required não aceitas · `502` registry unreachable.

## 4. Data model

- `installed_skills` (id, scope, scope_id, entry_id, version, enabled,
  installed_by, installed_at, meta_json) — migration dupla.

## 5. NFR / Security

- Todo conteúdo de entry é **untrusted** (SPEC-91 envelope);
  SKILL.md materializado nunca executa nada fora do ciclo de prompt.
- sha256 manifest vs conteúdo baixado verificado; HTTPS obrigatório
  para registry remoto; `Marketplace:AllowedHosts` (CIDR/host list).
- `perm:marketplace.manage` (org+system scopes), `marketplace.install`
  (user scope). `permissions_required` auditado no audit log.
- Entries `mcp-server` não sobrescrevem servidores builtin sem flag.

## 6. Task Plan

1. `installed_skills` + perms + migration.
2. Registry client + manifest model + sha256 verify.
3. Materializers por kind (skill/agent/mcp/hook) + precedence chain.
4. Endpoints + UI grid/detail/installed.
5. CLI verbs + seed `marketplace.json` curado.
6. Testes: unit (manifest parse, precedence, verify), integração
   (install→materialize→agent vê skill), golden OpenAPI.

## 7. Deliverables

Catálogo instalável com 3 escopos, materialização real nos agentes e
update flow — marketplace MIT, self-hostable.

## 8. Referências

- `docs/research/2026-11-competitive-gap-analysis.md` §2.3 (Devin
  plugins), §4 P2.
- `docs/specs/95_*` (catálogo), `123_*` (skills/hooks), `59_*` (perms).

## 9. Guardrails

- Registry default = arquivo local versionado — app funciona offline.
- Nenhum entry instala código executável no host da API (só prompts/
  configs/skills — MCP servers já rodam no sandbox por definição).
- `permissions_required` nunca concedidas implicitamente.

## 10. DoD

- Install `iterate` skill no scope user → arquivo materializado +
  agent enxerga (teste integração); desinstalar remove arquivo.
- Scope precedence user>org>system testado; upgrade path funciona.
- Suite verde; migration dupla; golden; i18n ×8.

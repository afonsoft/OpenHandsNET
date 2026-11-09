# SPEC — 137 · Beta release housekeeping (SPEC-113 archive + dead code + quickstart + tag)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `beta-release-housekeeping` |
| Type | `Docs / Infra / Refactor (leve)` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed` |
| Ticket | #371 · Epic #365 |
| Scope | `.specs/` + `docs/specs/` + `src/` + `docs/` + `tests/` |

## 1. User Story

**As a** mantenedor preparando o `v0.1.0-beta.1`, **I want** fechar o
bookkeeping pendente (SPEC-113 arquivada, dead code removido, quickstart
verificado, checklist de release), **so that** o repositório público
esteja coeso e a tag represente exatamente o que funciona.

## 2. Contexto / escopo

Pendências mecânicas encontradas na auditoria — nenhuma é feature nova:

- **SPEC-113 (desktop surface) está entregue mas não arquivada**: o
  código existe (`runtime/sdks/desktop.sh`, `DesktopTab.razor`,
  `SandboxSurfaceProxy`, env `SANDBOX_DESKTOP=1` default-on), a SPEC
  continua `Approved` em `.specs/`. Lifecycle exige mover para
  `docs/specs/` com `Status: Completed` + evidência (check-spec-status
  guard falha no drift).
- **`MiscEndpoints` shim in-memory**: `MiscEndpoints.cs` mantém uma
  fila de pending messages em memória ("no MVP") duplicando a fila EF
  real (`pending_messages` table + drain no `AgentRunner.cs:1646` —
  SPEC-41/44). Comentário desatualizado + dois caminhos divergentes =
  confusão pública. Auditar consumidores e remover o shim ou
  convergi-lo no backend real.
- **Quickstart não verificado de ponta a ponta**: `docker compose up
  -d --build` + `Auth:Provider=none` + mínimo de env vars precisa rodar
  limpo numa env sem dev state (valores default, migração, primeiro
  boot, primeira conversa com mock/stub LLM se aplicável).
- **Checklist de release `v0.1.0-beta.1`**: versão (csproj/Directory.*
  se existir campo), CHANGELOG/RELEASE notes, tag git, imagem Docker
  etiquetada, README com quickstart + badges + link docs.
- **Security defaults review** para público: `Auth:Provider` default,
  rate-limit on, egress off-by-default documentado, `.env.exemplo`
  sem defaults inseguros, nenhum secret comitado (gitleaks já no CI).

Fora de escopo: publicação em registries (Docker Hub ghcr push real —
checklist apenas prepara), site/docs dedicado, rename do projeto
(SPEC própria, decisão do nome pendente).

## 3. Requisitos

- RF-001: `.specs/113_desktop_surface_spec.md` → `docs/specs/` com
  `Status: Completed` + evidência (PRs/commits); índice `.specs/README.md`
  e `docs/specs/README.md` atualizados; `check-spec-status` verde.
- RF-002: remover/convergir `MiscEndpoints` pending-messages shim —
  decidir por auditoria dos consumers (hub `pending_messages`, CLI,
  testes); resultado = um único caminho (EF) + comentário correto ou
  arquivo removido.
- RF-003: validação do quickstart em env limpa: `cp .env.exemplo .env`
  → `docker compose up -d --build` → app responde, migrações aplicam,
  shell carrega, `Auth:Provider=none` documentado; gaps achados viram
  fix nesta SPEC (doc ou código pequeno).
- RF-004: checklist `docs/release-beta.md` (ou seção no README):
  bump de versão, CHANGELOG `v0.1.0-beta.1`, `git tag`, build da imagem
  com tag, link para docs de env vars + auth modes + quickstart.
- RF-005: security defaults pass — confirmar/documentar:
  `Auth:Provider` default escolhido para beta (`none` com aviso claro
  "single-user local" ou `local` exigindo setup — **decisão: `none`**
  para quickstart friction-zero, com callout de segurança pro
  deployment exposto), rate-limit ativo, egress documentado,
  `.env.exemplo` auditado.
- RF-006: testes — os existentes verdes após remoção do shim (ajustar
  o que consumia o caminho in-memory); sem regressão nos endpoints de
  pending messages reais.
- RF-007: docs — `README.md` quickstart verificado, `docs/environment-
  variables*.md` com `Auth:Provider` modes + `SANDBOX_DESKTOP`, nota
  de status beta no topo do README.

## 4. API Contract

Nenhuma rota nova. Se `MiscEndpoints` expõe rotas públicas, a remoção
afeta o golden OpenAPI → regen + nota de breaking-change no CHANGELOG
(endpoint era interno/MVP; consumidores migram para `/pending_messages`
real).

## 5. Critérios de aceite

- [ ] `check-spec-status` e lifecycle guard verdes; SPEC-113 em
      `docs/specs/` com evidência.
- [ ] Zero referências ao caminho in-memory duplicado; um único backend
      de pending messages.
- [ ] `docker compose` em env limpa sobe e a primeira conversa roda
      (stub LLM aceitável).
- [ ] `docs/release-beta.md` com checklist completo para a tag.
- [ ] Build/testes verdes; golden regen se endpoints removidos.

## 6. Task Plan

1. Archive SPEC-113 + índices + evidence refs.
2. Auditoria `MiscEndpoints` consumers → remoção/convergência + testes.
3. Quickstart clean-env run + fixes de doc/config encontrados.
4. Security defaults pass + `.env.exemplo` audit.
5. `docs/release-beta.md` checklist + README beta note.

## 7. Deliverables

- `docs/specs/113_desktop_surface_spec.md` (movida) + índices.
- `MiscEndpoints.cs` removido/convergido + ajustes de testes.
- `README.md` atualizado, `docs/release-beta.md`, `.env.exemplo`
  revisado, `docs/environment-variables*.md` (auth modes + desktop env).

## 8. Referências

- `src/OpenHandsNET.Api/Endpoints/MiscEndpoints.cs:9` — comentário
  "fila em memória no MVP" desatualizado.
- `src/OpenHandsNET.Infrastructure/Agent/AgentRunner.cs:1646` — drain
  real da `pending_messages` EF.
- `runtime/sdks/desktop.sh`, `DesktopTab.razor`, `SandboxSurfaceProxy` —
  evidência SPEC-113 entregue.
- `.specs/README.md` — lifecycle (`check-spec-status`).

## 9. Guardrails

- Não remover `MiscEndpoints` sem auditar consumers (hub, CLI, testes) —
  convergir para o backend EF se algo depender do shape.
- Nenhuma mudança em `.github/workflows/` nesta SPEC.
- `.env.exemplo` sem secrets reais; defaults seguros ou explicitamente
  marcados "dev only".
- Golden OpenAPI regen apenas se rotas mudarem de fato.

## 10. DoD

- Bookkeeping limpo: SPEC-113 arquivada, shim resolvido, quickstart
  reproduzível do zero, checklist de release pronto, defaults de
  segurança documentados; CI verde.

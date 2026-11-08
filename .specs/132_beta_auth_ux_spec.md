# SPEC — 132 · Beta auth UX (login page + user menu + sign-out + 401 handling)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `beta-auth-ux` |
| Type | `Feature` |
| Stack | `.NET 10 · Blazor WASM` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | beta-readiness audit 2026-11-08 (visual gap #1) |
| Scope | `src/OpenHandsNET.Client/` + `tests/` |

## 1. User Story

**As a** usuário de um deployment com auth habilitada (cookie/OIDC),
**I want** uma página de login, um menu de usuário na sidebar e ação de
sign-out, **so that** eu consiga entrar/sair e gerenciar minha sessão
sem depender de links manuais — o mesmo fluxo básico de qualquer SaaS.

## 2. Contexto / escopo

O servidor já implementa a superfície completa de auth
(`src/OpenHandsNET.Api/Endpoints/AuthEndpoints.cs`):

- `Auth:Provider` com modos `none` (single-user local, `NullBrokerUserAdmin`
  cria a row local automaticamente), `local`/`cookie`, `oidc`.
- `POST /auth/logout` (cookie sign-out + audit), `GET /auth/onboarding_status`,
  `POST /auth/dev-login`, OIDC `/oauth/oidc/login` + callbacks, device flow
  RFC 8628, `POST /api/v1/authenticate` (`sk-*`).
- `FallbackPolicy` exige autenticação — hoje um usuário anônimo recebe 401/302
  sem nenhuma UI que explique ou resolva isso.

O **client não tem nenhuma superfície de auth**: sem rota `/auth`, sem
user-menu, sem sign-out, sem tratamento de 401 — o app só funciona
porque deployments atuais rodam `Auth:Provider=none`. OWUIC (referência
visual) tem `Auth.InitializeAsync` no `MainLayout` → redirect `/auth`
quando não autenticado + user menu com avatar/theme/settings/sign-out.

Decisões de design:

- **Modo `none` não mostra nada**: o client detecta o provider via um
  endpoint de status (usar `GET /auth/onboarding_status` existente ou
  `GET /api/v1/auth/session` minimal novo — decidir na implementação o
  que já retorna `provider` + `user`). Com `provider=none` o app se
  comporta como hoje (sem login, sem menu de conta ou com menu mínimo
  "Local user" — sem logout, pois não há sessão a encerrar).
- **`/auth` page** (`Pages/AuthPage.razor`, `AllowAnonymous`): renderiza
  o fluxo do provider — botão "Entrar com SSO" quando `oidc` (redirect
  para `/oauth/oidc/login?returnUrl=`), formulário e-mail/senha →
  `POST /api/v1/authenticate` quando `local`, e card "modo local" quando
  `none`. Visual segue os tokens `.card`/inputs do design system.
- **Auth state service** (`AuthStateService`): boot assíncrono que chama
  o endpoint de sessão; `MainLayout`/`AuthGuard` redireciona para
  `/auth?returnUrl=…` quando 401 e rota atual não é pública
  (`/auth`, `/share/{token}`, `/shared/{token}`, `/_framework/*`).
- **User menu** no footer da sidebar (padrão OWUIC `Sidebar.razor:330-374`):
  avatar com iniciais + nome → popover com switch de tema (move o
  `ThemeToggle` para dentro), item Settings (abre `DialogKind.Settings`),
  item Admin (só `is_super_admin`/`is_admin`), divider, "Sair" →
  `POST /auth/logout` → redirect `/auth`.
- **401 handling**: handler no `ConversationHubClient`/HttpClient que,
  ao receber 401 numa chamada autenticada, força navegação para `/auth`
  (evita estado zombie com API rejeitando tudo).

Fora de escopo: signup/registro self-service (usuários são provisionados
por admin/OIDC/SCIM), reset de senha, MFA, gestão de sessões, archived
conversations (item de menu prepara slot, feature é pós-beta — SPEC
futura).

## 3. Requisitos

- RF-001: `AuthStateService` com `InitializeAsync` (session probe),
  `IsAuthenticated`, `User` (nome/email/roles), `Provider`,
  `LogoutAsync()`; singleton scoped ao circuito WASM.
- RF-002: `Pages/AuthPage.razor` (`@page "/auth"`, `AllowAnonymous`):
  três renderings por provider; `returnUrl` respeitado pós-login
  (sanitizado para paths internos apenas — anti open-redirect);
  i18n em todos os 8 locales.
- RF-003: Guard de auth — `MainLayout` aguarda `InitializeAsync` antes
  de montar a shell; rotas não-públicas redirecionam para `/auth` se
  `!IsAuthenticated && Provider != none`.
- RF-004: `UserMenu.razor` no footer da sidebar — avatar iniciais,
  segmented theme switch (reusa lógica do `ThemeToggle`), Settings,
  Admin condicional, Sign out (só quando há sessão real);
  `aria-haspopup`/`aria-expanded`, focus-trap via `DialogService` ou
  pattern do menu de conversa existente.
- RF-005: 401 handler — intercepta respostas 401 do HttpClient principal
  (exclui endpoints `/auth/*` e rotas públicas) → navega para `/auth`
  com `returnUrl` atual.
- RF-006: `AuthStateService` disponibiliza `RequiresAuth` para a UI
  esconder ações que dependem de sessão quando `provider=none`
  (ex.: botão "sair" não aparece; menu vira "Local mode" read-only).
- RF-007: testes — unit (`AuthStateService` parse, returnUrl sanitize,
  401 handler), bUnit (`AuthPage` 3 providers, `UserMenu` render +
  logout chama endpoint), integration (fluxo cookie login→logout já
  coberto; adicionar assert de `onboarding_status` se usado).
- RF-008: i18n — chaves `auth.*`, `user_menu.*` nos 8 locales
  (check-i18n-parity + placeholders).

## 4. API Contract

| Método | Rota | Auth | Notas |
|---|---|---|---|
| GET | `/auth/onboarding_status` | público | já existe — reusar p/ provider+user |
| POST | `/api/v1/authenticate` | público | já existe — login `local` |
| POST | `/auth/logout` | sessão | já existe — sign-out |
| GET | `/oauth/oidc/login` | público | já existe — redirect SSO |
| GET | `/api/v1/auth/session` | público | **novo (opcional)** — `{provider, user:{name,email,roles}}` se o status atual não cobrir |

## 5. Critérios de aceite

- [ ] `Auth:Provider=none` → boot direto na shell, sem flash de `/auth`,
      menu mostra modo local.
- [ ] `Auth:Provider=local`/`oidc` sem sessão → qualquer rota interna
      redireciona para `/auth?returnUrl=…`; login bem-sucedido volta à
      rota original.
- [ ] "Sair" encerra a sessão e leva a `/auth`; APIs voltam a 401.
- [ ] `/share/{token}` continua público e funcional sem sessão.
- [ ] `dotnet format` clean, build 0 warnings, unit+bUnit+integration
      verdes, drift guards do client passando.

## 6. Task Plan

1. `AuthStateService` + session probe + guard no `MainLayout`.
2. `AuthPage` (3 providers) + returnUrl sanitize.
3. `UserMenu` + avatar + theme switch move + logout wiring.
4. 401 interceptor.
5. i18n ×8 + testes (unit/bUnit/integration) + drift guards.

## 7. Deliverables

- `Services/AuthStateService.cs`, `Pages/AuthPage.razor`,
  `Components/UserMenu.razor`, mudanças em `Layout/MainLayout.razor`,
  `Components/ThemeToggle.razor` (reuso), `wwwroot/i18n/*.json`,
  testes em `tests/OpenHandsNET.ClientTests/` + `IntegrationTests/`.

## 8. Referências

- `src/OpenHandsNET.Api/Endpoints/AuthEndpoints.cs:15-156` — superfície
  de auth existente (provider modes, logout, dev-login, OIDC, device).
- `src/OpenHandsNET.Client/Layout/MainLayout.razor` — footer atual com
  `ThemeToggle` (ponto de injeção do `UserMenu`).
- `~/repos/OpenWebUiCode/src/OpenWebUI.Client/Components/Sidebar.razor:330-374`
  — referência visual do user menu OWUIC.
- `Components/Icons.cs` — `logout`/`user` icons já existem, hoje órfãos.

## 9. Guardrails

- Não enfraquecer o `FallbackPolicy` nem criar rota bypass — o guard é
  client-side apenas; o servidor continua autoridade.
- `returnUrl` sanitizado: só paths relativos internos, nunca absolute
  URL/scheme (open-redirect).
- `/share/*`, `/shared/*`, `/_framework/*`, `/auth` permanecem públicos.
- `provider=none` não exibe nenhum prompt de login.
- Nunca logar tokens/senhas; inputs de senha com `autocomplete` correto.

## 10. DoD

- Login/logout funciona em `local` e `oidc` (este último verificado com
  IdP de teste ou stub); `none` transparente; user menu com theme switch
  e admin condicional; 401 leva a `/auth` com volta à rota; testes e
  drift guards verdes.

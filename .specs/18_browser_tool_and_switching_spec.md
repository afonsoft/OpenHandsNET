# SPEC — 18 · Browser Tool (Playwright) + Conversation Switching

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `browser-tool-and-switching` |
| Type | `Feature / Agent+Api` |
| Stack | `.NET 10 · Microsoft.Playwright · Minimal API` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` (autopilot — gap list SPEC-14→17 wrap-up) |
| Ticket | _(issue a criar)_ (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox_spec` (RF-005), `04_rest_and_signalr_api_spec`, `09_agent_engine_spec`, `14_enterprise_operations_spec` (profiles) |
| Touches | `BuiltinTools`, `ToolRegistry`, `AppConversationEndpoints`, `AppConversationService`, `ConversationRecord`, DI registrations |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** que o agente navegue na web com um browser real e que eu possa trocar
o sandbox profile e o modelo do agente de uma conversa existente
**So that** o loop ReAct cobre o mesmo repertório de tools do upstream e a
conversa não fica presa aos parâmetros escolhidos na criação.

**Problem context:**

- `BuiltinTools.Browser()` é stub — retorna
  `ToolResult.Error("browser tool is not implemented yet (SPEC-03 RF-005)")`
  (`BuiltinTools.cs:198-208`). SPEC-03 RF-005 já definiu a direção:
  `Microsoft.Playwright` **no worker** (in-process API), screenshot base64 →
  observação.
- O Client já renderiza screenshots: `ConversationStateService` lê
  `obs.screenshot_base64` (`ConversationStateService.cs:181`) e `BrowserTab.razor`
  monta o filmstrip — falta só quem produza o campo.
- `POST /{id}/switch_profile` e `POST /{id}/switch_acp_model` retornam **501**
  (`AppConversationEndpoints.cs:82-87`). SPEC-04 tabela prevê os contratos de
  erro (404/409/502 e 400/404/409/502/504).
- `ConversationRecord.SandboxProfile` já persiste (SPEC-14 slice 2) e
  `LlmModel` já flui create→runner — switching é atualização + invalidação.

## 2. Scope

**In scope:**

- **RF-001 — `IBrowserService` (worker-side, Playwright)**: singleton de
  browser compartilhado; um `IBrowserContext` por conversa (isolamento de
  cookies/storage). Ações: `goto`, `click`, `type`, `scroll`, `back`,
  `forward`, `screenshot`, `content` (innerText truncado a ~8 KB).
  `Browser:Enabled` (default `true`), `Browser:Headless` (default `true`),
  `Browser:NavigationTimeoutMs` (default `30000`),
  `Browser:MaxScreenshotBytes` (default `512 KB`, jpeg se exceder png? — png
  com clip `1280x800` fixo).
- **RF-002 — tool `browser` real**: schema `{action, url?, selector?, text?,
  direction?}` — `action` enum `[goto,click,type,scroll,back,forward,
  screenshot,content]`. Resposta: `{ok, url, title, screenshot_base64?,
  content?, error?}` — `screenshot_base64` presente em toda ação bem-sucedida
  (alimenta o filmstrip). Risky: `true` (navegação externa).
- **RF-003 — Lifecycle**: contexto por conversa criado lazy no primeiro uso;
  fechado quando a conversa é deletada (`AppConversationService.DeleteAsync`
  → `IBrowserService.CloseAsync(conversationId)`). Falha de browser ausente
  retorna erro acionável na observação (ver RF-004), nunca derruba o run.
- **RF-004 — Bootstrap de browsers**: `Browser:AutoInstall` (default `true` em
  dev / `false` quando `DOTNET_RUNNING_IN_CONTAINER`). No primeiro uso, se o
  chromium não estiver em `~/.cache/ms-playwright`, roda
  `Microsoft.Playwright.Program.Main(["install","chromium"])` uma única vez
  (guard `SemaphoreSlim` + status flag). Falha → tool error com instrução
  (`playwright.ps1 install chromium --with-deps`).
- **RF-005 — `POST /app-conversations/{id}/switch_profile`**: body
  `{sandbox_profile}` (string, `null`/`""` limpa). Valida contra
  `SandboxProfiles` (400 `invalid_profile`); conversa inexistente → 404; run
  ativo (`ExecutionStatus == RUNNING`) → 409; sandbox atual `Running` com
  profile diferente → **stop+remove** (workspace sobrevive — bind mount host)
  e próxima mensagem recria com o profile novo. Retorna `AppConversationDto`.
- **RF-006 — `POST /app-conversations/{id}/switch_acp_model`**: body `{model}`
  (string obrigatória). 400 sem model; 404 inexistente; 409 run ativo.
  Persiste `LlmModel` — o runner lê o valor do record a cada run, então a
  troca vale na próxima execução. Retorna `AppConversationDto`.

**Out of scope:**

- Browser **dentro** do container sandbox (upstream roda no runtime; a SPEC-03
  fixou worker-side — imagem runtime não ganha chromium).
- BrowserGym/accessibility-tree completa — v1 usa innerText/screenshot.
- Switch de profile quente (resize do container vivo) — troca vale na
  recriação; documentado no changelog do evento.
- ACP externo de verdade — `switch_acp_model` mantém o nome da rota por
  paridade, mas troca `LlmModel` do provider interno.

## 3. Design

```text
LLM tool_call "browser" ──► BuiltinTools.Browser(IBrowserService)
   └─► BrowserService.GetContextAsync(convId)
        ├─ playwright (lazy, IPlaywright.CreateAsync)
        ├─ chromium.LaunchAsync(headless)
        └─ NewContextAsync per convId → Page única reutilizada
   └─► ação → ObservationEvent.observation {..., screenshot_base64}
        └─► SignalR → ConversationStateService.Screenshots → BrowserTab
```

- `ToolCallContext` já expõe `Services` — o handler resolve
  `IBrowserService` via DI (igual `git_commit` resolve `IGitService`).
- Contexto por conversa guardado em `ConcurrentDictionary<Guid, IBrowserContext>`;
  race de criação protegido por lock por conv.
- switch_profile: `AppConversationService.SwitchProfileAsync(id, profile)`
  valida → persiste → se `SandboxId` atual `Running` chama
  `ISandboxService.StopAsync` + marca `ConversationRecord.SandboxId = null`
  para recriação lazy no próximo `send-message` (mesmo caminho do
  wake-on-message SPEC-16).

## 4. API Contract

| Endpoint | Body | Respostas |
|---|---|---|
| `POST /api/v1/app-conversations/{id}/switch_profile` | `{sandbox_profile: string?}` | 200 `AppConversationDto` · 400 `invalid_profile` · 404 · 409 `run_active` · 502 sandbox |
| `POST /api/v1/app-conversations/{id}/switch_acp_model` | `{model: string}` | 200 `AppConversationDto` · 400 · 404 · 409 `run_active` |

`AppConversationDto` já expõe `sandbox_profile` e `llm_model` — sem mudança de
schema. Evento `ConversationStateUpdateEvent(key="sandbox_profile"|"llm_model")`
emitido na troca para o Client refletir.

## 5. Critérios de Aceite

- [ ] `browser` tool `goto` retorna `screenshot_base64` + `title`/`url`;
  screenshot aparece no BrowserTab (evento carrega `screenshot_base64`).
- [ ] `click`/`type`/`scroll`/`back`/`forward`/`content` funcionam sobre a
  mesma página (contexto persistente por conversa).
- [ ] Browser ausente + `AutoInstall=false` → tool error com instrução, run
  segue vivo.
- [ ] `switch_profile` válido em conversa parada → 200 + DTO atualizado; com
  sandbox running → sandbox stop/removido, próximo start usa profile novo.
- [ ] `switch_profile` `profile="bogus"` → 400; run ativo → 409.
- [ ] `switch_acp_model` `{model:"x"}` → 200 + próximo run usa `x`; run ativo
  → 409; `{}` → 400.
- [ ] Dois browsers de conversas distintas não compartilham cookies.

## 6. NFR

- Segurança: Playwright sempre `headless` por default; navegação limitada por
  `NavigationTimeoutMs`; screenshots capadas.
- O package `Microsoft.Playwright` vai só em `Infrastructure` (DI-boundary),
  nunca em `Domain`/`Application`.
- Cleanup: `IBrowserService` implementa `IAsyncDisposable`; `app` shutdown
  fecha contexts → browser → playwright.

## 7. Task Plan

1. `BrowserOptions` + `IBrowserService` + `PlaywrightBrowserService`
   (lazy launch, per-conv context, auto-install) — unit tests com fake page.
2. `BuiltinTools.Browser` real + registro no `ToolRegistry` (manter assinatura
   com DI resolve no handler).
3. `switch_profile`/`switch_acp_model` no service + endpoints + DTOs.
4. `DeleteAsync` → `CloseAsync(conversationId)`; `ConversationStateUpdateEvent`.
5. Testes: unit (arg validation, service transitions, profile validation) +
   integration WAF (404/400/409) + browser e2e opt-in
   (`OHN_BROWSER_TESTS=1`, skip sem chromium).
6. `.env.exemplo` + `docs/` + README SPEC index.

## 8. Deliverables

```text
src/OpenHandsNET.Application/Agent/IBrowserService.cs
src/OpenHandsNET.Infrastructure/Agent/Browser/BrowserOptions.cs
src/OpenHandsNET.Infrastructure/Agent/Browser/PlaywrightBrowserService.cs
src/OpenHandsNET.Infrastructure/Agent/Tools/BuiltinTools.cs      # Browser() real
src/OpenHandsNET.Application/Conversations/ConversationContracts.cs  # Switch*
src/OpenHandsNET.Infrastructure/Conversations/AppConversationService.cs
src/OpenHandsNET.Api/Endpoints/AppConversationEndpoints.cs       # 501 → impl
src/OpenHandsNET.Api/DTOs/ApiDtos.cs                             # Switch*Request
tests/OpenHandsNET.UnitTests/Agent/BrowserToolTests.cs
tests/OpenHandsNET.UnitTests/Conversations/SwitchTests.cs
tests/OpenHandsNET.IntegrationTests/SwitchEndpointsTests.cs
tests/OpenHandsNET.IntegrationTests/BrowserToolE2ETests.cs       # opt-in
docs/tools/browser.md
```

## 9. Referências

- SPEC-03 RF-005 (`browser` deferrable → agora implementado), SPEC-04
  (contrato das rotas de switch), SPEC-14 (profile resolution chain),
  SPEC-16 (wake/recreate path).
- https://playwright.dev/dotnet/docs/intro — Microsoft.Playwright.
- Upstream `OpenHands` runtime `browsergym` actions — conceito apenas
  (licença MIT no core; não copiar código de `enterprise/`).

## 10. Guardrails

- Browser nunca roda dentro do sandbox container nesta versão (worker-side).
- `switch_*` nunca derruba um run ativo — 409 explícito.
- Playwright auto-install é single-flight e nunca bloqueia o host startup.

## 11. DoD

- [ ] `dotnet build -c Release` + `dotnet test` verdes.
- [ ] CI do PR verde (build+test, docker image, event contract, gitguardian).
- [ ] `browser` tool sai do stub; `switch_*` saem do 501.
- [ ] `.specs/README.md` index atualizado; issue fechada no merge.

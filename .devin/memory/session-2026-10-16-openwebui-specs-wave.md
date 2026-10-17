
## SPEC-45 (PR #111)
- ChatComposer compartilhado Home/ChatPane; @mention varre workspace via
  `find` no /exec (exclui .git/.ohn-checkpoints); /slash = builtins +
  skills da conversa (GET /{id}/skills) com $ARGUMENTS/$1..$N.
- Upload: POST /{id}/uploads multipart `file` ≤10MB → ISandboxExecutor
  .WriteFileBytesAsync (b64 chunked, MAX_ARG_STRLEN-safe); override em
  SandboxAgentExecutor via PUT files(b64)+decode; DockerSandboxExecutor
  cai pro chunked quando content >48KB (bug latente SPEC-42 corrigido).
- ReadFormAsync sem boundary → 500: endpoint envelopa em try/catch →
  400 problem-details.
- Fixtures bUnit antigos quebraram com ChatComposer (ToastService/Api/
  JSInterop.Loose) — registrar nos ctor de ConversationComponentTests
  e HomePageTests.
- CI flakes conhecidos: testes E2E Docker (sandbox_not_running / poll
  deadline) falham sob carga do runner e passam em rerun — SPEC-44 PR
  #110 precisou 2 reruns.

## SPEC-46 (i18n) — PR #112
- Worktree `ohn-spec46`, branch `feature/spec46-i18n` (baseada na main pós-45).
- `LocalizationService` port do open-webui: fallback active→en→pt→key, `{{name}}`,
  `ohn.locale`, `ohnI18n.setLang`, init antes do `RunAsync`, `Changed` no MainLayout.
- Sweep: 44 razor → chaves `area.element`; lookups dinâmicos (`L[label]`, `L[tip]`,
  `kind_{k}`). Settings ganha dropdown de idioma (RF-004 → `PUT prefs {locale}`).
- 277 chaves en/pt completas + 94 chaves de chrome nos 6 idiomas best-effort
  (resto cai no fallback en-US por design).
- Gotchas Razor: `value="@code"` vira diretiva @code (renomear var); `@inject`
  dentro de `@* *@` não compila; `attr="@L["k"]"` OK (expressão implícita c/ indexer).
- `L10nStub.Register(this)`: fixture bUnit serve JSONs reais via DiskHandler;
  asserts de texto migrados p/ pt-BR (default). Parity test cobre manifesto,
  keyset en=pt, sem chaves alienígenas e placeholders iguais.
- Suite local: 659/659. Issue #104 → in_pullrequest.

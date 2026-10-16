
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

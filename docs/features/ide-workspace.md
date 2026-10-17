# IDE Workspace (SPEC-42)

Aba `ide` da página de conversa — workbench completo sobre o sandbox.

## Layout

- **Explorer**: árvore de `/workspace` (lazy) com icones por extensão.
- **Editor**: Monaco (mesmo engine do VS Code) com syntax highlight,
  ETag optimistic lock no save (`PUT /files` → `409` em conflito com
  merge manual).
- **SCM**: painel git — status, diff inline, stage/commit (SPEC-12) +
  `CheckpointList` embutido (SPEC-43).
- **Search**: ripgrep no workspace com resultados file:line:col e
  jump-to-line no editor.
- **Tests**: exec one-shot via `/exec` mostrando stdout/stderr inline.

## Write path

Escrita textual via `PUT /app-conversations/{id}/files` (UTF-8);
binária via `WriteFileBytesAsync` — chunked base64 por `exec`
(default, docker-safe) ou `PUT files`+decode no sandbox-agent
(override HTTP — SPEC-45). Limite: `MAX_ARG_STRLEN` tratado por
chunking.

## Quick open

`Ctrl+P` → fuzzy match (`IdeQuickOpenFuzzy`) sobre a árvore
carregada.

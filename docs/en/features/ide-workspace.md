# IDE Workspace (SPEC-42)

The conversation page's `ide` tab — a full workbench over the sandbox.

## Layout

- **Explorer**: lazy `/workspace` tree with per-extension icons.
- **Editor**: Monaco (the VS Code engine) with syntax highlight and an
  ETag optimistic lock on save (`PUT /files` → `409` on conflict with
  manual merge).
- **SCM**: git pane — status, inline diff, stage/commit (SPEC-12) plus
  the embedded `CheckpointList` (SPEC-43).
- **Search**: workspace ripgrep with file:line:col results and
  jump-to-line in the editor.
- **Tests**: one-shot `/exec` run showing stdout/stderr inline.

## Write path

Text writes go through `PUT /app-conversations/{id}/files` (UTF-8);
binary writes use `WriteFileBytesAsync` — chunked base64 over `exec`
(default, docker-safe) or `PUT files`+decode on the sandbox-agent
(HTTP override — SPEC-45). `MAX_ARG_STRLEN` is handled by chunking.

## Quick open

`Ctrl+P` → fuzzy match (`IdeQuickOpenFuzzy`) over the loaded tree.

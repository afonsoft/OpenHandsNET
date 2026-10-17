# Composer (SPEC-45)

Caixa de entrada rica compartilhada entre Home e ChatPane.

## @menções

`@` abre fuzzy picker de arquivos do workspace — a menção injeta o
path na mensagem. Varredura via `find` no `/exec` (exclui `.git` e
`.ohn-checkpoints`).

## /slash

- Builtins: comandos do composer.
- Skills da conversa (`GET /{id}/skills` — repo + user federadas,
  SPEC-25): `/nome arg1 arg2` expande o `content_md` da skill com
  `$ARGUMENTS`/`$1..$N` antes do envio (`ComposerSlash.Expand`).

## Drafts + histórico

- Draft por conversa em `localStorage` (restaura ao trocar de thread).
- `↑`/`↓` no composer vazio navega o histórico de mensagens enviadas.

## Uploads

`POST /{id}/uploads` multipart `file` (≤10MB, extensão allowlist) →
grava no workspace via `WriteFileBytesAsync` e referencia o path na
mensagem. `409` sem sandbox running.

## Editar e reenviar

Botão editar em mensagem de usuário carrega o texto de volta no
composer; reenviar cria novo turno (sem reescrever histórico — event
store é append-only).

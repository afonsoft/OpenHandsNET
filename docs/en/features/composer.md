# Composer (SPEC-45)

Rich input box shared by Home and ChatPane.

## @mentions

`@` opens a workspace file fuzzy picker — the mention injects the
path into the message. Scanning via `find` on `/exec` (excludes `.git`
and `.ohn-checkpoints`).

## /slash

- Builtins: composer commands.
- Conversation skills (`GET /{id}/skills` — federated repo + user,
  SPEC-25): `/name arg1 arg2` expands the skill `content_md` with
  `$ARGUMENTS`/`$1..$N` before sending (`ComposerSlash.Expand`).

## Drafts + history

- Per-conversation draft in `localStorage` (restored on thread
  switch).
- `↑`/`↓` in an empty composer navigates sent-message history.

## Uploads

`POST /{id}/uploads` multipart `file` (≤10MB, extension allowlist) →
written to the workspace via `WriteFileBytesAsync` and referenced by
path in the message. `409` without a running sandbox.

## Edit and resend

The edit button on a user message loads its text back into the
composer; resending creates a new turn (history is never rewritten —
the event store is append-only).

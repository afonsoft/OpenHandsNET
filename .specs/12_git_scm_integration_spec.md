# SPEC — 12 · Git & Source-Control Integration

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `git-scm-integration` |
| Type | `Feature / API` |
| Stack | `.NET 10 · git CLI (in-sandbox) · EF Core` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Ticket | [#13](https://github.com/afonsoft/OpenHandsNET/issues/13) (Epic [#1](https://github.com/afonsoft/OpenHandsNET/issues/1)) |
| Depends on | `03_runtime_sandbox_spec`, `10_persistence_efcore_spec` |
| Consumed by | `04` (proxies file/git), `05` (diff/commits tabs), `09` (`git_commit` tool) |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** que `selected_repository` seja clonado no workspace, alterações do
agente apareçam como diff/commit, e credenciais Git sejam geridas com segurança
**So that** o trabalho do agente vire código versionado inspecionável — o que
transforma a conversa em entrega real.

**Problem context:** upstream: `selected_repository` clona no start
(`PREPARING_REPOSITORY`), `GET /{id}/git/changes` e `/git/diff` devolvem estado
do repo, o agente faz commits e (cloud) abre PRs via integrations
(GitHub/GitLab/Bitbucket/Azure DevOps). Esta SPEC cobre clone, status, diff,
commits e credenciais; PR/providers externos ficam para fase 3.

## 2. Scope

**In scope:**
- Clone no start-task (`PREPARING_REPOSITORY`): `git clone {url} /workspace/repo`
  executado **dentro** do container (git presente na imagem sandbox) ou pré-clone
  host→mount quando configurado (`Git__CloneMode=container|host`).
- `IGitService` (boundary `ISandboxExecutor`):
  - `GetStatusAsync` → `git status --porcelain=v2 --branch` parseado →
    `GitChanges{branch, staged[], modified[], untracked[], conflicts[]}`.
  - `GetDiffAsync(path?)` → `git diff HEAD` (ou `--cached`) parseado em
    `GitDiff{files[{path,status,hunks[{old_start,old_lines,new_start,new_lines,content}]}]}`.
  - `GetLogAsync(limit)` → commits (`hash,author,date,message`) → commits-tab.
  - `CommitAsync(message,files?)` → tool `git_commit` do agente (SPEC 09).
  - `CheckoutBranchAsync(name)` / `CreateBranchAsync` — branch de trabalho
    `openhands/{conversation_id_short}` criada no clone (config `Git__WorkBranch`).
- Credenciais: `secrets` `GIT_TOKEN`/`GIT_SSH_KEY` → env mascarado/`~/.git-credentials`
  efêmero no sandbox; `askpass` shim para HTTPS sem persistir token no disk.
- Endpoints proxies (paridade V1): `GET /{conversation_id}/git/changes`,
  `GET /{conversation_id}/git/diff`, `GET /{conversation_id}/files` (tree),
  `GET /{conversation_id}/file?path=`.
- `GitCredentialRecord` por usuário/org (SPEC 08 secrets store) + provider hint
  (`github.com`, `gitlab.com`, `dev.azure.com`, `bitbucket.org`, self-hosted host).

**Out of scope:** GitHub App/instalações, PR/MR creation, webhooks de repo,
GitLab/Bitbucket/Azure DevOps providers (todos fase 3 — `integrations/` upstream,
Polyform); submodules avançados; merge/rebase assistido; LFS.

## 3. Technical Context

**Upstream (evidência):**
- `agent_server/git_router.py`, `git_provider_service.py` (MIT).
- `sdk/git/` (helpers de clone/worktree — MIT).
- Start-task stage `PREPARING_REPOSITORY` (api-inventory §2).
- `integrations/` no enterprise (providers — Polyform, conceitos).

**Where:**
- `Application/Git/IGitService.cs` + DTOs (`GitChanges`, `GitDiff`, `GitFileStatus`)
- `Infrastructure/Git/GitService.cs` (via `ISandboxExecutor.ExecAsync`)
- `Infrastructure/Git/GitDiffParser.cs`, `GitStatusParser.cs` (porcelain v2)
- `Infrastructure/Git/GitCredentialProvider.cs` (secrets → askpass/env)
- `Infrastructure/Sandbox/WorkspaceProvisioner.cs` (integração do clone — SPEC 03)

## 4. Requirements

### RF-001: Clone
- `selected_repository` formatos: `owner/repo` (resolve via provider default),
  URL HTTPS completa, `file:///path` (dev). `default_branch` detectada via
  `git remote show origin` → checkout + cria `Git__WorkBranch`.
- Falha de clone → start-task `ERROR` com `error.detail` descritivo.
- Credencial aplicada só na operação (env `GIT_ASKPASS` shim ou header
  `Authorization: Basic` injetado via `git -c http.extraheader` — nunca salva
  em `.git/config` nem `.git-credentials` persistente).

### RF-002: changes/diff/log
- `GET /{id}/git/changes` → `GitChanges` (porcelain v2 parse); `…/git/diff` →
  unified diff parseado (server) **e** raw `?format=raw`.
- Sem repo → `404` `no_repository`; repo sem mudanças → `200` vazio.
- `git log -n 50 --format=…` → commits-tab; inclui commits do agente
  (`author=openhandsnet-agent`).

### RF-003: git_commit tool
- `git_commit{message, files?}`: `git add {files|.}` + `git commit -m` com
  `author.name/email` do agente configurável (`Git__AuthorName`).
- `risky` → confirmation mode (SPEC 09); resultado → `ObservationEvent` com hash.

### RF-004: File browsing
- `GET /{id}/files?path=` → tree lazy (`find`/`ls` parseado, symlinks resolvidos
  dentro do workspace, `..` rejeitado); `GET /{id}/file?path=` → conteúdo
  (limite 1MB; binary → `{encoding:base64,truncated}`).
- Path traversal `../`/absoluto fora de `/workspace` → `400`.

**Invariants:** git só via `git` CLI dentro do container (não LibGit2Sharp no
host — o repo mora no container); token nunca em `.git/config`, command args
visíveis no `ps`, nem logs; todas as operações auditáveis via eventos quando
originadas do agente.

## 5. API Contract

`GET /api/v1/app-conversations/{id}/git/changes` · `GET …/git/diff[?format=raw]` ·
`GET …/git/log` (extensão OpenHandsNET — upstream não tem, documentar como
divergência intencional) · `GET …/files?path=` · `GET …/file?path=` ·
`GET|PUT|DELETE /api/v1/git/credentials[/{host}]` (metadados; segredo em secrets).

## 6. Critérios de Aceite

- [ ] `selected_repository` válido → `/workspace/repo` com branch de trabalho antes de `READY`.
- [ ] Agente edita arquivo → `git/changes` lista como `modified`; `git/diff` devolve hunks reais.
- [ ] `git_commit` cria commit com autor do agente; `git/log` lista o commit.
- [ ] Token HTTPS não aparece em `git remote -v`, `.git/config`, `ps` ou logs (teste de redaction).
- [ ] `path=../../etc/passwd` → `400`; clone de repo inexistente → task `ERROR` legível.
- [ ] `file?path=` retorna conteúdo; >1MB → `truncated`.

## 7. Task Plan

1. Parsers `GitStatusParser`/`GitDiffParser` (porcelain v2, unified diff) (verify: unit tests fixtures).
2. `GitService` via `ISandboxExecutor` (status/diff/log/commit/checkout) (verify: sandbox test).
3. `WorkspaceProvisioner` clone + work branch + `GIT_ASKPASS` creds (verify: start-task E2E).
4. Endpoints proxies + `git/credentials` (verify: contract tests).
5. `git_commit` tool + `ObservationEvent` com hash (verify: loop test SPEC 09).

## 8. Deliverables — arquivos a criar/modificar

```text
src/OpenHandsNET.Application/Git/IGitService.cs
src/OpenHandsNET.Application/Git/{GitChanges,GitDiff,GitDiffFile,GitHunk,
  GitFileStatus,GitLogEntry,GitCredentialRecord}.cs
src/OpenHandsNET.Infrastructure/Git/GitService.cs
src/OpenHandsNET.Infrastructure/Git/{GitStatusParser,GitDiffParser,GitLogParser}.cs
src/OpenHandsNET.Infrastructure/Git/GitCredentialProvider.cs   # askpass shim + extraheader
src/OpenHandsNET.Infrastructure/Git/GitAskPassShim.cs          # script gerado p/ sandbox
src/OpenHandsNET.Infrastructure/Sandbox/WorkspaceProvisioner.cs # clone step (SPEC 03)
src/OpenHandsNET.Infrastructure/Agent/Tools/GitCommitTool.cs    # (SPEC 09 — integração)
src/OpenHandsNET.Api/Endpoints/GitEndpoints.cs                  # proxies + credentials
tests/OpenHandsNET.UnitTests/Git/{GitStatusParserTests,GitDiffParserTests}.cs
tests/OpenHandsNET.IntegrationTests/Git/{GitServiceTests,CloneFlowTests}.cs
```

## 9. Referências e links

### Internas (repo)
- `migration-plan/research/api-inventory.md` §2 (`PREPARING_REPOSITORY`, file/git proxies).
- `.specs/03_runtime_sandbox_spec.md` — workspace/`ISandboxExecutor`.
- `.specs/08_enterprise_security_rbac.md` — secrets store (credenciais).
- `.specs/09_agent_engine_spec.md` — `git_commit` tool.

### Upstream — código-fonte
- Git router/provider (MIT): https://github.com/OpenHands/software-agent-sdk/blob/acca181/openhands-agent-server/openhands/agent_server/git_router.py
  e `git_provider_service.py`
- SDK git helpers: https://github.com/OpenHands/software-agent-sdk/tree/acca181/openhands-sdk/openhands/sdk/git
- Integrações enterprise (Polyform — conceitos): https://github.com/OpenHands/enterprise/tree/693589e/integrations/
- Canvas `commits-tab`/`files-tab` (MIT): https://github.com/OpenHands/OpenHands/blob/d7e14e2/src/routes/commits-tab.tsx

### git / padrões
- `git status --porcelain=v2`: https://git-scm.com/docs/git-status#_porcelain_format_version_2
- `git diff` unified format: https://git-scm.com/docs/diff-format
- `GIT_ASKPASS`: https://git-scm.com/docs/gitcredentials
- `http.extraheader`: https://git-scm.com/docs/git-config#Documentation/git-config.txt-httpextraHeader
- LibGit2Sharp (NÃO usar — workspace no container; documentado para constar): https://github.com/libgit2/libgit2sharp

## 10. Guardrails de execução (Devin)

- **Nunca** persistir token em disco no sandbox (`GIT_ASKPASS`/`extraheader` efêmeros).
- Sanitizar URLs com credenciais embutidas antes de log/eventos (`SecretRedactor`).
- Path traversal: rejeitar `..`, absoluto, symlink saindo de `/workspace`.
- `git_commit` é tool `risky` (confirmation mode); `git push` **não existe** no MVP
  (push = fase 3 com provider).
- Operações git respeitam sandbox RUNNING; `PAUSED` → `409` (paridade send-message).
- Clone mode `host` documentado como menos isolado (só dev/air-gapped).

## 11. Definition of Done

- [ ] Clone + branch + changes/diff/log funcionando no sandbox real.
- [ ] `git_commit` tool integrada; endpoints + credentials ok.
- [ ] Redaction de credenciais verificada por teste dedicado.
- [ ] `dotnet test` verde; SPEC `Done`; issue fechada pelo PR.

# SPEC — 03 · Runtime & Sandbox

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `runtime-sandbox` |
| Type | `Feature / Infra` |
| Stack | `.NET 10 · Docker.DotNet · Microsoft.Playwright` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Approved` |
| Depends on | `01_architecture_overview` |

## 1. User Story

**As a** usuário do OpenHandsNET
**I want** que cada conversa execute o agente num ambiente isolado e reproduzível
**So that** comandos, edições e navegação web aconteçam sem risco ao host — a base
da autonomia segura que define o OpenHands.

**Problem context:** upstream tem 2 níveis — **Sandbox** (container/pool gerido
pelo app_server: `POST /sandboxes`, pause/resume, `SandboxStatus`) e
**Agent Server** (FastAPI dentro do sandbox que executa ações: bash via tmux,
file ops, browser). No OpenHandsNET, o agente-worker é .NET e roda in-process
(MVP) ou num container dedicado; a orquestração Docker usa `Docker.DotNet`.

## 2. Scope

**In scope:**
- `SandboxSpec` (imagem, recursos, env, mounts, timeout) + CRUD via API (SPEC 04).
- `ISandboxService`: `StartAsync`, `PauseAsync`, `ResumeAsync`, `DeleteAsync`,
  `GetAsync`, `SearchAsync`, `GetStatusAsync`.
- `DockerSandboxService` via `Docker.DotNet`: pull/create/start/stop/remove,
  bind mount do workspace (`/workspace`), resource limits (CPU/mem), labels
  `openhandsnet.conversation_id`.
- Execução de ações do agente: bash session persistente (stdin/stdout multiplex),
  file read/write/list, git ops — executadas pelo **agent-worker** (SPEC 04 loop).
- Estados: `STARTING|RUNNING|PAUSED|ERROR|MISSING` (paridade upstream).
- Secrets do sandbox injetados como env vars mascaradas (SPEC 08).

**Out of scope:** Kubernetes/warm-pool/Runtime API (tier cloud, fase 3);
gVisor/Kata; sandbox remoto multi-host (fase 3); VNC/VS Code server no sandbox (fase 3).

## 3. Technical Context

**Upstream (evidência):**
- `app_server/sandbox/sandbox_router.py`: `GET /sandboxes/search`, `GET /sandboxes`,
  `POST /sandboxes`, `POST /{id}/pause`, `POST /{id}/resume`, `DELETE /{id}`,
  `GET /{id}/settings/secrets[/{name}]`; `sandbox_spec_router` CRUD.
- `OpenHands-Cloud/docs/ARCHITECTURE.md`: Runtime API mantém pool de *warm runtimes*.
- Agent-server bash é tmux-based (`TMUX_TMPDIR` per-process); o .NET usa
  `docker exec` com sessão persistente + pseudo-TTY ou processo bash dedicado.

**Where:** `Application/Sandbox/` (contratos), `Infrastructure/Sandbox/`
(Docker.DotNet impl, bash session, workspace mount), `Domain/Sandbox/` (entities
`Sandbox`, `SandboxSpec`, `SandboxStatus`).

## 4. Requirements

### RF-001: Entidades
```csharp
public enum SandboxStatus { Starting, Running, Paused, Error, Missing }
public sealed record SandboxSpec(Guid Id, string Name, string Image,
    int MemoryMb = 4096, double CpuLimit = 2.0, int TimeoutMinutes = 60,
    IReadOnlyDictionary<string,string>? Env = null, bool MountWorkspace = true);
public sealed record Sandbox(Guid Id, Guid SpecId, string ContainerId,
    SandboxStatus Status, Uri? AgentUrl, DateTimeOffset CreatedAt, string? LastError);
```

### RF-002: Lifecycle Docker
- `StartAsync(spec)`: pull se ausente → create (labels, limits, binds) → start →
  aguarda health (processo agent-worker ou probe) → `Running`.
- `PauseAsync`/`ResumeAsync`: `docker pause/unpause` ou stop/start preservando
  container+workdir (decisão: usar pause/unpause nativo; `Paused` não libera workspace).
- `DeleteAsync`: stop+remove idempotente; orphan sweep no boot por label.
- Timeout: `TimeoutMinutes` gera `SandboxTimeoutEvent` → status `Paused` (MVP) — alinha com "rate limit pauses" upstream.

### RF-003: Execução de ações (agent-worker boundary)
```csharp
public interface ISandboxExecutor
{
    Task<BashSession> OpenBashSessionAsync(Sandbox s, CancellationToken ct); // persistente
    Task<CommandResult> ExecAsync(Sandbox s, string cmd, TimeSpan timeout, CancellationToken ct);
    Task<string> ReadFileAsync(Sandbox s, string path, CancellationToken ct);
    Task WriteFileAsync(Sandbox s, string path, string content, CancellationToken ct);
    Task<IReadOnlyList<FileEntry>> ListFilesAsync(Sandbox s, string path, CancellationToken ct);
    Task<GitDiff> GetGitDiffAsync(Sandbox s, CancellationToken ct);
}
```
- Bash session: `docker exec -i bash` com streams stdin/stdout/stderr; delimitação
  de comandos via marker + `exit_code` capturado (equivalente ao
  `CmdOutputObservation` upstream).
- File ops via `docker exec` (`cat`, `test`, `find`) ou archive API (`GetArchiveFromContainer`).

### RF-004: Workspace
- `MountWorkspace=true` → bind `data/workspaces/{conversationId}` → `/workspace` (rw).
- `selected_repository` → clone dentro do workspace no start (via `git` no container ou pré-clone host→mount).

### RF-005: Browser (Playwright) — deferrable no MVP
- `Microsoft.Playwright` dentro do worker para `Browse*` actions; screenshots
  base64 → `BrowserObservation` (habilita pane Browser do UI, SPEC 05).

**Invariants:** um sandbox ativo por conversa (MVP); nada executa no host fora do
container; comandos respeitam `confirmation_mode` da conversa; secrets só como
env (nunca em args de comando/ logs).

## 5. API Contract

(honrado em SPEC 04 — paridade upstream)
`GET|POST /api/v1/sandboxes[/search]`, `POST /api/v1/sandboxes/{id}/pause|resume`,
`DELETE /api/v1/sandboxes/{id}`, `GET /api/v1/sandboxes/{id}/settings/secrets[/{name}]`,
`GET|POST|PATCH|DELETE /api/v1/sandbox-specs`.

## 6. Critérios de Aceite

- [ ] `POST /sandboxes` com spec Docker cria container, monta workspace e retorna `RUNNING` (Testcontainers).
- [ ] `ExecAsync("echo hi")` retorna `exit_code=0` + stdout; comando que falha retorna code≠0 sem exceção.
- [ ] Bash session preserva estado (`cd /tmp && pwd` → `/tmp`).
- [ ] Pause/resume congela e retoma execução; delete remove container (verificável via `docker ps`).
- [ ] Órfãos de crash anterior são limpos no boot (label sweep).
- [ ] Clone de `selected_repository` aparece em `/workspace` antes do primeiro evento do agente.

## 7. Task Plan

1. Entities + `DockerSandboxService` create/start/stop/remove + labels (verify: Testcontainers).
2. `SandboxExecutor` exec+bash session+file ops (verify: integration tests).
3. Pause/resume/delete + orphan sweep + timeout watcher (verify: lifecycle tests).
4. Workspace mount + repo clone (verify: e2e start-conversation path).
5. Playwright no worker (verify: browse action → screenshot observation).

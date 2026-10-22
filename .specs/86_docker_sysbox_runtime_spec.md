# SPEC — 86 · Docker provider: `Sandbox:DockerRuntime` (sysbox-runc)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `docker-runtime-sysbox` |
| Type | `Implementation + Documentation` |
| Stack | `.NET 10 · Docker.DotNet` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Draft` |
| Ticket | `gap-analysis 2026-10-22` · `GAP-implementation-docker-runtime-sysbox` |
| Depends on | `14` RF-002 (DinD sidecar), `37`/`49` (sysbox em k8s) — entregues |
| Reference | `docs.openhands.dev/enterprise/docker-in-sandbox` (conceitos apenas) |
| Touches | `src/OpenHandsNET.Sandboxing/{WorkspaceProvisioner,DockerSandboxEngine}.cs`, `.env.exemplo`, `docs/deployment/dind.md` (+en), testes unitários |

## 1. User Story

**As a** operador self-hosted em single-host Docker (sem Kubernetes)
**I want** poder rodar os sandboxes sob `sysbox-runc` como container
runtime
**So that** Docker-in-sandbox deixa de depender do sidecar `privileged`
— paridade com a postura upstream (user-namespace remapping, sem
privileged, sem socket do host) também fora de k8s.

**Contexto:** upstream docker-in-sandbox exige o runtime hardened
(Sysbox) — sem `privileged`, sem host socket, root no sandbox ≠ root no
host. No repo: `KubernetesSandboxEngine` já seta `RuntimeClassName =
sysbox-runc` (`KubernetesSandboxEngine.cs:103`), mas o provider
`local`/docker nunca seta `HostConfig.Runtime` (`DockerSandboxEngine.cs:95`)
— logo `runtime=sysbox-runc` não é selecionável fora de k8s, e o único
DinD disponível em compose é o sidecar privileged
(`docs/deployment/dind.md` já o classifica como "root-equivalente").

## 2. Escopo

In-scope:

- `SandboxOptions.DockerRuntime` (`Sandbox:DockerRuntime`, default vazio
  = runtime padrão do daemon).
- `DockerSandboxEngine`: `hostConfig.Runtime = options.DockerRuntime`
  quando set (aplica ao sandbox principal; DinD sidecar continua
  privileged por ora — sysbox no sidecar é investigação separada).
- Docs `dind.md`: matriz de isolamento (default / sysbox / dind sidecar /
  socket mount), nota **kernel ≥ 6.3** (requisito upstream do runtime
  hardened) e instrução de instalação do sysbox no host
  (`sysbox-deploy` sem k8s). Nota: com `DockerRuntime=sysbox-runc` o
  agente pode rodar `docker` dentro do sandbox **sem** `EnableDinD` — o
  daemon é filho do próprio sandbox, não sidecar.
- `.env.exemplo`: `OPENHANDSNET_Sandbox__DockerRuntime=`.
- Mirror en.

Out-of-scope: trocar default (opt-in), instalar sysbox
automaticamente, DinD sidecar rootless, rootless docker daemon
(já documentado em `security.md`).

## 3. Requisitos

1. Option string livre (`runc`, `sysbox-runc`, `sysbox-runc-fusermount`
   …) — validar não-vazio; engine passa direto ao Docker (daemon valida).
2. Sem runtime setado → comportamento idêntico ao atual (null no
   HostConfig).
3. Doc explicita: sysbox-runc no host + `DockerRuntime=sysbox-runc` +
   kernel ≥6.3 recomendado; sandbox segue sem `privileged`.
4. Teste unitário: `DockerRuntime` setado → `HostConfig.Runtime`
   propagado no `CreateContainerParameters` (mock IDockerClient ou
   extração do HostConfig builder — seguir padrão de teste existente).

## 4. Task Plan

1. `SandboxOptions.DockerRuntime` + binding.
2. `DockerSandboxEngine` propaga `Runtime` (sandbox principal).
3. `.env.exemplo` + `dind.md` (+en) — matriz de isolamento + kernel note.
4. Teste unitário da propagação.

## 5. Deliverables

- Option + propagação + docs + 1 teste.

## 6. Referências

- Upstream: `docs.openhands.dev/enterprise/docker-in-sandbox`.
- Repo: `DockerSandboxEngine.cs:95-139`, `KubernetesSandboxEngine.cs:103`,
  `WorkspaceProvisioner.cs` (`SandboxOptions`), `docs/deployment/dind.md`.

## 7. Guardrails

- Opt-in; nunca alterar default. Nada de `privileged` no sandbox
  principal. Doc marca sysbox como postura preferida vs sidecar.

## 8. DoD

- `Sandbox:DockerRuntime=sysbox-runc` produz container com runtime
  sysbox (verificável via `docker inspect -f '{{.HostConfig.Runtime}}'`);
  doc atualizada; build verde.

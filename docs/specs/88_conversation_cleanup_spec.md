# SPEC — 88 · Conversation cleanup — remove sandbox container + workspace

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `conversation-cleanup` |
| Type | `Infrastructure / bugfix` |
| Stack | `.NET 10 · Docker.DotNet · k8s SDK · runtime-api` |
| Repository | `afonsoft/OpenHandsNET` |
| Status | `Completed (PR #228, f09d8eb)` |
| Ticket | requisito direto do mantenedor (2026-10-22) |
| Depends on | `14` RF-006 (workspace lifecycle), `35` (runtime split), `37`/`49` (k8s) |
| Reference | `docs.openhands.dev/enterprise/architecture` — runtimes teardown (conceitos) |
| Touches | `ISandboxEngine`, `DockerSandboxEngine`, `KubernetesSandboxEngine`, `HttpSandboxEngine`, `SandboxService`, `RuntimeApi/Program.cs`, `tests`, docs k8s |

## 1. User Story

**As a** operador
**I want** que excluir/arquivar uma conversa libere **todos** os recursos
do sandbox — container **e** workspace
**So that** espaço não vaza com conversas deletadas — hoje o provider
docker já remove container + dir do workspace, mas o provider k8s
remove só o pod e o ConfigMap de sleep, **retendo a PVC `ohn-ws-*`**
para sempre.

**Contexto:** `AppConversationService.DeleteAsync` (soft-delete = status
`Archived`, histórico preservado) já chama `SandboxService.DeleteAsync` →
`engine.RemoveAsync(ContainerId)` + `provisioner.RemoveWorkspace(convId)`
(host dir). O gap: `KubernetesSandboxEngine.RemoveAsync` não apaga a
PVC — o cluster acumula volumes órfãos. Agravante: conversa deletada com
sandbox **dormindo** (pod já removido pelo sleep, PVC retida) não tem
como derivar o convId do pod.

## 2. Escopo

In-scope:

- `ISandboxEngine.RemoveAsync(containerId, workspaceConversationId = null, ct)`.
- `KubernetesSandboxEngine.RemoveAsync`: apaga pod + ConfigMap (como
  hoje) e **apaga a PVC `ohn-ws-{convId:N}`** quando:
  1. `workspaceConversationId` explícito (caminho conversa deletada —
      cobre sandbox dormindo); ou
  2. fallback: label `openhandsnet.conversation_id` do pod (caminho
      orphan sweep, sem convId).
- `HttpSandboxEngine`/`RuntimeApi`: propaga o convId no DELETE
  (`/runtime/v1/sandboxes/{id}?workspace=<guid>`) p/ modo split.
- `SandboxService.DeleteAsync` passa `record.ConversationId`.
- Helpers puras extraídas + testes unitários (nome da PVC, extração de
  label).
- Docs `kubernetes.md` (pt+en): PVC removida junto com a conversa.

Out-of-scope: retenção configurável de PVC (policy), GC assíncrono de
PVC órfã sem pod, docker volumes nomeados.

## 3. Requisitos

1. Delete/archive de conversa (docker e k8s) → container **e** workspace
   removidos; histórico no banco preservado (comportamento atual mantido).
2. `RemoveAsync` idempotente (404s engolidos) — PVC já inexistente não
   falha.
3. Warm pool (`ohn-pool-*`, sem workspace) e sweep continuam funcionando
   sem convId (fallback de label no k8s).
4. Nome da PVC idêntico ao create (`ohn-ws-{convId:N}`).

## 4. Task Plan

1. Interface + 3 engines + runtime-api + SandboxService.
2. Helpers `WorkspacePvcName` / `TryGetConversationId` + testes.
3. Docs k8s (pt+en).

## 5. Deliverables

- Cleanup completo por provider + testes + docs.

## 6. Referências

- `AppConversationService.DeleteAsync` (370–392), `SandboxService.DeleteAsync`
  (304–321), `KubernetesSandboxEngine.RemoveAsync` (497–520),
  `EngineLabels.ConversationId`.

## 7. Guardrails

- Sempre idempotente; nada de delete de PVC sem convId conhecido (não
  inventar nome); histórico da conversa intocado.

## 8. DoD

- Conversa deletada no k8s → pod, ConfigMap e PVC `ohn-ws-*` removidos;
  testes unitários verdes; build ok.
# SPEC-53: Enterprise Sandbox Hardening, Live Handover & Session Time-Travel

## 1. Visão Geral & Contexto

Com a entrega das SPECs 01 a 52, o **OpenHandsNET** alcançou paridade estrutural com o núcleo do ecossistema OpenHands e conceitos avançados do Devin Web. Contudo, na execução de agentes autônomos de longa duração em escala corporativa (Enterprise), três gargalos operacionais e de segurança persistem:

1. **Docker-in-Sandbox sem isolamento real:** Agentes que necessitam compilar imagens (`docker build`) ou subir pilhas de serviço (`docker compose up`) dependem de montagem arriscada do socket do host ou de contornos instáveis.
2. **Descompasso no Handover Humano-Agente (Live Takeover):** Quando o usuário interage via terminal Web ou VNC, o agente não registra formalmente a intervenção humana, gerando alucinações e perda de plano mental.
3. **Ausência de Reversão de Estado Completo (Time-Travel):** O rollback atual é limitado à árvore do Git, deixando arquivos temporários, pacotes instalados (`npm`, `apt`) e processos do sandbox fora de sincronia.
4. **Saturação de Contexto por Saídas Excessivas (Output Bloat):** Saídas de builds e testes consomem dezenas de milhares de tokens desnecessariamente.

A **SPEC-53** especifica a implementação de isolamento via containers rootless/Sysbox, handover colaborativo bidirecional, checkpoints atômicos via snapshot de volume/overlayfs, compilação de contexto semântico e diagnóstico automatizado enterprise.

---

## 2. Objetivos e Requisitos

### 2.1 Requisitos Funcionais (FR)
- **FR-01 (DinS Isolado):** Prover suporte a execução de containers Docker/Podman aninhados dentro do sandbox sem privilégios de root e sem expor `/var/run/docker.sock` do host.
- **FR-02 (Live Takeover & Handover):** Detectar comandos inseridos pelo operador humano no xterm.js ou interações no VNC, suspendendo o agente e gerando eventos `human_intervened_observation`.
- **FR-03 (Time-Travel Sandbox Snapshot):** Permitir a criação de checkpoints de filesystem/estado do sandbox com rollback atômico em conjunto com o histórico de eventos.
- **FR-04 (EventStream Auto-Compactor):** Compactar e truncar saídas volumosas de terminal (> 2.000 tokens) mantendo apenas cabeçalho, cauda e sumário analítico para a LLM, salvando a saída completa em storage de artefatos.
- **FR-05 (Enterprise Support Bundle):** Exportar pacote `.tar.gz` sanitizado contendo métricas, logs OTel, conectividade e estado do banco via API e CLI.

### 2.2 Requisitos Não-Funcionais (NFR)
- **NFR-01 (Segurança / Zero-Trust):** Bloqueio de escape de container no sandbox aninhado usando cgroups v2, namespaces não mapeados para o root do host e seccomp profile restrito.
- **NFR-02 (Latência de Snapshot):** Criação de snapshots locais em tempo inferior a 350ms através de Copy-on-Write (CoW).
- **NFR-03 (Compatibilidade .NET):** Implementação nativa em C# 14 / .NET 10 integrando com Docker.DotNet, k8s client e SignalR.

---

## 3. Arquitetura e Modelagem de Dados

### 3.1 Novos Eventos do EventStream V1

```csharp
namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Evento gerado quando o operador assume o controle do terminal ou browser.
/// </summary>
public record HumanIntervenedObservation(
    string Id,
    string ConversationId,
    string Source, // "web_terminal" | "vnc_canvas"
    string Summary,
    string? CommandExecuted,
    DateTimeOffset Timestamp
) : EventBase("human_intervened_observation", Id, ConversationId, Timestamp);

/// <summary>
/// Evento de criação de checkpoint físico do sandbox.
/// </summary>
public record SandboxSnapshotCreatedEvent(
    string Id,
    string ConversationId,
    string SnapshotId,
    string BaseCommitSha,
    long SizeBytes,
    DateTimeOffset Timestamp
) : EventBase("sandbox_snapshot_created", Id, ConversationId, Timestamp);
```

### 3.2 Contrato do Provedor de Sandbox com Snapshots

```csharp
namespace OpenHandsNET.Application.Common.Interfaces;

public interface ISandboxSnapshotProvider
{
    ValueTask<string> CreateSnapshotAsync(string conversationId, string label, CancellationToken ct = default);
    ValueTask RestoreSnapshotAsync(string conversationId, string snapshotId, CancellationToken ct = default);
    ValueTask DeleteSnapshotAsync(string conversationId, string snapshotId, CancellationToken ct = default);
    IAsyncEnumerable<SandboxSnapshotInfo> ListSnapshotsAsync(string conversationId, CancellationToken ct = default);
}

public record SandboxSnapshotInfo(string SnapshotId, string Label, DateTimeOffset CreatedAt, long SizeBytes);
```

---

## 4. Design de Componentes

### 4.1 Isolamento DinS (Docker-in-Sandbox)
- **Estratégia Local/Docker:** Sandbox provisionado com engine Rootless Podman pré-configurada ou integração com `sysbox-runc`.
- **Estratégia Kubernetes (SPEC-49):** Pods de sandbox executando com `runtimeClassName: sysbox-runc` permitindo instanciar pods/containers aninhados com isolamento de namespace de usuário (`userns`).
- **Bloqueio:** Interceptação proativa de tentativas de montagem de `/var/run/docker.sock` pelo `SecurityAnalyzer`.

### 4.2 Handover Bidirecional (Live Takeover)
- Ao focar no terminal xterm.js ou interagir com o canvas VNC:
  1. O cliente Blazor envia SignalR hub invocation `AcquireHumanControl(conversationId)`.
  2. O loop do agente muda o estado de `RUNNING` para `PAUSED_BY_USER`.
  3. Ao soltar a sessão ou clicar em "Devolver para o Agente", o backend sintetiza o `HumanIntervenedObservation` com o diff do buffer e o histórico de comandos digitados.
  4. O agente é retomado (`RESUMED`) e reavalia seu plano considerando os novos fatos do ambiente.

### 4.3 Output Folding & Compaction
- Observações de `CmdOutputObservation` e `ActionExecutionResult` passam por pipeline de sanitização:
  - Limite configurável: `MaxInlineOutputChars` (default: 4.000 caracteres / ~1.000 tokens).
  - Saídas superiores são salvas no `IArtifactStorage` (local/S3).
  - O payload da LLM recebe:
    ```text
    [OUTPUT TRUNCATED - Full log stored at artifact://outputs/cmd-123.log]
    --- HEAD (First 20 lines) ---
    ...
    --- TAIL (Last 30 lines) ---
    ...
    ```

### 4.4 Diagnostic Support Bundle
- **Endpoint:** `GET /api/v1/system/diagnostics/support-bundle` (requer papel `SuperAdmin` ou permissão `system:diagnostics`).
- **CLI:** `openhandsnet doctor --bundle -o ./bundle.tar.gz`.
- **Conteúdo Coletado:**
  1. `system-info.json`: Versão do .NET, CPU, Memória, OS e storage livre.
  2. `docker-diagnostics.json`: Status do Docker daemon / K8s, latência da API, containers ativos do pool.
  3. `database-health.json`: Latência de queries EF Core, status de migrations, tamanho do SQLite/Postgres.
  4. `recent-logs.log`: Últimas 5.000 entradas do Serilog com hashes e mascaramento de segredos via `ISecretMasker`.

---

## 5. Plano de Implementação em Fases

| Fase | Escopo | Entregáveis |
| :--- | :--- | :--- |
| **Fase 1** | Event Compactor & Output Folding | `OutputCompactorService`, middleware no `AgentLoop`, testes unitários de truncagem e preservação de erro. |
| **Fase 2** | Live Takeover (Handover SignalR) | Hub methods `AcquireHumanControl` / `ReleaseHumanControl`, componente Blazor xterm.js handover, evento `HumanIntervenedObservation`. |
| **Fase 3** | Time-Travel & Snapshot Provider | `DockerVolumeSnapshotProvider` (Docker checkpoint/commit temporário), integração de rollback com Git branch reset. |
| **Fase 4** | DinS (Docker-in-Sandbox) Hardening | Imagem de runtime com Rootless Podman/Docker, perfis sysbox e validação no `SecurityAnalyzer`. |
| **Fase 5** | Support Bundle API & CLI | Endpoint `/api/v1/system/diagnostics/support-bundle`, comando CLI `openhandsnet doctor --bundle`. |

---

## 6. Critérios de Aceite (Definition of Done)

- [ ] Comandos longos de terminal têm seus outputs compactados sem estourar o limite de contexto da LLM.
- [ ] O operador humano consegue digitar no terminal ou navegar no VNC durante a execução de uma tarefa, pausando o agente com feedback visual e retomando a tarefa sem corrupção do plano.
- [ ] Criação e restauração de snapshots de sandbox revertem com sucesso alterações de arquivos em menos de 1 segundo em ambiente local.
- [ ] Execução de `docker build` ou `podman run` dentro do sandbox completa com sucesso sem acesso root ao host.
- [ ] O comando `openhandsnet doctor --bundle` gera um arquivo `.tar.gz` íntegro e sem credenciais/tokens expostos.

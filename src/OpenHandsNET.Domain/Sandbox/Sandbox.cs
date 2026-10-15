namespace OpenHandsNET.Domain.Sandbox;

/// <summary>
/// A running (or tracked) sandbox container bound to a conversation.
/// <see cref="ContainerId"/> is the Docker id; <see cref="AgentUrl"/> points at
/// an agent-server inside the sandbox when it exists (phase 2+).
/// </summary>
/// <param name="Id">Sandbox id.</param>
/// <param name="SpecId">Spec usada no create (<see cref="Guid.Empty"/> = default).</param>
/// <param name="ConversationId">Conversa dona do sandbox.</param>
/// <param name="ContainerId">Docker container id.</param>
/// <param name="Status">Estado reconciliado do lifecycle.</param>
/// <param name="AgentUrl">URL do agente in-container, quando existe.</param>
/// <param name="CreatedAt">Criação.</param>
/// <param name="ExpiresAt">Lease — watcher pausa quando expira.</param>
/// <param name="LastError">Último erro de reconcile.</param>
/// <param name="Pool"><c>cold</c> | <c>warm</c>.</param>
/// <param name="PoolKey">SPEC-17: chave do pool que originou o container (imagem).</param>
/// <param name="BlueprintStatus">SPEC-17: <c>ok|failed|none</c> do blueprint.</param>
/// <param name="BlueprintLogTail">SPEC-17: cauda (4 KB) do log do blueprint.</param>
/// <param name="ServiceUrls">SPEC-38: <c>service_urls</c> públicas conforme RoutingMode.</param>
public sealed record Sandbox(
    Guid Id,
    Guid SpecId,
    Guid? ConversationId,
    string? ContainerId,
    SandboxStatus Status,
    Uri? AgentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? LastError,
    string Pool = "cold",
    string? PoolKey = null,
    string BlueprintStatus = "none",
    string? BlueprintLogTail = null,
    IReadOnlyDictionary<int, string>? ServiceUrls = null);

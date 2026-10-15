namespace OpenHandsNET.Application.Sandbox;

/// <summary>
/// SPEC-35 RF-001/002: engine-level sandbox contract — pure container
/// lifecycle keyed by labels/ids, no EF. <see cref="ISandboxService"/>
/// (records, specs, credentials) delegates container ops here; two impls:
/// <c>DockerSandboxEngine</c> (local docker) and <c>HttpSandboxEngine</c>
/// (remote <c>openhandsnet-runtime-api</c>).
/// </summary>
public interface ISandboxEngine
{
    /// <summary>
    /// True when this engine delegates to a remote runtime-api over HTTP —
    /// the Api skips hosting the warm-pool filler and docker-exec fallback
    /// (remote sandboxes require agent images).
    /// </summary>
    bool IsRemote { get; }

    /// <summary>Creates + starts a sandbox container (image pull included).</summary>
    Task<EngineSandboxInfo> CreateAndStartAsync(
        EngineCreateRequest request, CancellationToken ct);

    /// <summary>Inspects one container; null when gone.</summary>
    Task<EngineSandboxInfo?> InspectAsync(string containerId, CancellationToken ct);

    /// <summary>Managed non-pool containers owned by this instance.</summary>
    Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(CancellationToken ct);

    /// <summary>Free warm-pool slots (running <c>ohn-pool-*</c> containers).</summary>
    Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(CancellationToken ct);

    /// <summary>
    /// Claims a free warm-pool container of <paramref name="request"/> image:
    /// renames it to the sandbox name and moves its pre-mounted workspace
    /// dir to the conversation path. Null = pool miss (caller cold-starts)
    /// or pool disabled (e.g. egress allowlist).
    /// </summary>
    Task<EngineWarmClaim?> ClaimWarmAsync(WarmClaimRequest request, CancellationToken ct);

    /// <summary>docker pause (memory-preserving).</summary>
    Task PauseAsync(string containerId, CancellationToken ct);

    /// <summary>docker unpause.</summary>
    Task UnpauseAsync(string containerId, CancellationToken ct);

    /// <summary>docker start (stopped container).</summary>
    Task StartAsync(string containerId, CancellationToken ct);

    /// <summary>docker stop.</summary>
    Task StopAsync(string containerId, CancellationToken ct);

    /// <summary>docker rm -f (idempotent).</summary>
    Task RemoveAsync(string containerId, CancellationToken ct);
}

/// <summary>
/// Fully-resolved container request — the service layer (Api) already
/// applied spec/profile/blueprint/credential resolution; the engine owns
/// egress network/policy, DinD sidecar, workspace bind prep and agent-port
/// publishing (all engine-host local concerns).
/// </summary>
public sealed record EngineCreateRequest(
    string Name,
    string Image,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Binds,
    IReadOnlyDictionary<string, string> Labels,
    long MemoryBytes,
    long NanoCpus,
    long? PidsLimit,
    IReadOnlyList<string>? Cmd,
    string? WorkingDir,
    // IsAgentImage: agent entrypoint → publishes/resolves port 8000.
    bool IsAgentImage,
    // WorkspaceConversationId: host workspace bind owner (engine prepares).
    Guid? WorkspaceConversationId,
    // WarmWorkspace: pool container — disposable warm-path dir, renamed to
    // the conversation path on claim (rename preserves the bind target).
    bool WarmWorkspace = false);

/// <summary>Container snapshot returned by engine ops.</summary>
public sealed record EngineSandboxInfo(
    string ContainerId,
    // State: docker state (running|paused|created|exited|dead).
    string State,
    string? Error,
    IReadOnlyDictionary<string, string> Labels,
    // AgentUrl: reachable agent base URL when the image carries one.
    Uri? AgentUrl);

/// <summary>Warm-pool claim input.</summary>
public sealed record WarmClaimRequest(
    string Image,
    string SandboxName,
    // WorkspaceConversationId: workspace dir owner (rename keeps bind target).
    Guid? WorkspaceConversationId);

/// <summary>Claimed warm container.</summary>
public sealed record EngineWarmClaim(
    string ContainerId,
    // InitKey: init_key label of dormant agent containers (SPEC-17 RF-007).
    string? InitKey,
    Uri? AgentUrl);

/// <summary>
/// Engine-level failure — <see cref="NotFound"/> covers docker 404s and
/// runtime-api 404s uniformly for the service layer.
/// </summary>
public sealed class SandboxEngineException : Exception
{
    public SandboxEngineException(string message, bool notFound = false,
        Exception? inner = null) : base(message, inner)
    {
        NotFound = notFound;
    }

    /// <summary>Container/image vanished.</summary>
    public bool NotFound { get; }
}

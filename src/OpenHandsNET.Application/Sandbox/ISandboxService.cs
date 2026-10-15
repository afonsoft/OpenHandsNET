using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Application.Sandbox;

/// <summary>
/// Sandbox lifecycle: create/start from spec, pause/resume, delete, status.
/// Persisted in EF (survives restarts; orphan sweep reconciles with Docker).
/// </summary>
public interface ISandboxService
{
    /// <summary>Creates + starts a sandbox from a spec; binds a conversation.</summary>
    Task<Domain.Sandbox.Sandbox> StartAsync(
        Guid specId, Guid? conversationId, CancellationToken ct);

    /// <summary>Pauses the sandbox (workspace preserved).</summary>
    Task<Domain.Sandbox.Sandbox> PauseAsync(Guid sandboxId, CancellationToken ct);

    /// <summary>
    /// Stops a paused sandbox (SPEC-16 RF-003) — frees RAM while keeping the
    /// container fs and workspace; resume performs <c>docker start</c>.
    /// </summary>
    Task<Domain.Sandbox.Sandbox> StopAsync(Guid sandboxId, CancellationToken ct);

    /// <summary>Resumes a paused or stopped sandbox.</summary>
    Task<Domain.Sandbox.Sandbox> ResumeAsync(Guid sandboxId, CancellationToken ct);

    /// <summary>Stops + removes the container (idempotent).</summary>
    Task DeleteAsync(Guid sandboxId, CancellationToken ct);

    /// <summary>Gets one sandbox record (container state reconciled lazily).</summary>
    Task<Domain.Sandbox.Sandbox?> GetAsync(Guid sandboxId, CancellationToken ct);

    /// <summary>Lists sandboxes (optionally filtered).</summary>
    Task<IReadOnlyList<Domain.Sandbox.Sandbox>> SearchAsync(
        SandboxStatus? status, Guid? conversationId, CancellationToken ct);

    /// <summary>CRUD for specs.</summary>
    Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec, CancellationToken ct);

    /// <summary>Gets a spec by id (or the built-in default).</summary>
    Task<SandboxSpec?> GetSpecAsync(Guid specId, CancellationToken ct);

    /// <summary>
    /// SPEC-35: removes managed engine containers whose records are
    /// gone/stale (boot sweep). Works for both providers.
    /// </summary>
    Task SweepOrphansAsync(CancellationToken ct);
}

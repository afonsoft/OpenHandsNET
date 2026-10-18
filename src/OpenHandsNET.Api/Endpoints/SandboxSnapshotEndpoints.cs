using Microsoft.AspNetCore.Mvc;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-53 RF-003: Endpoints de gerenciamento de snapshots físicos do sandbox
/// e reversão atômica de estado (Time-Travel).
/// </summary>
public static class SandboxSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapSandboxSnapshotEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/app-conversations/{id:guid}/sandbox-snapshots")
            .WithTags("SandboxSnapshots");

        group.MapGet("/", ListSnapshotsAsync);
        group.MapPost("/", CreateSnapshotAsync);
        group.MapPost("/{snapshotId}/restore", RestoreSnapshotAsync);
        group.MapDelete("/{snapshotId}", DeleteSnapshotAsync);

        return app;
    }

    private static async Task<IResult> ListSnapshotsAsync(
        Guid id,
        [FromServices] ISandboxSnapshotProvider provider,
        CancellationToken ct)
    {
        var snapshots = await provider.ListSnapshotsAsync(id, ct);
        return Results.Ok(snapshots);
    }

    public record CreateSnapshotRequest(string? Label);

    private static async Task<IResult> CreateSnapshotAsync(
        Guid id,
        [FromBody] CreateSnapshotRequest? request,
        [FromServices] ISandboxSnapshotProvider provider,
        CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(request?.Label) ? "manual-snapshot" : request.Label;
        var snapshot = await provider.CreateSnapshotAsync(id, label, ct);
        return Results.Created($"/api/v1/app-conversations/{id}/sandbox-snapshots/{snapshot.SnapshotId}", snapshot);
    }

    private static async Task<IResult> RestoreSnapshotAsync(
        Guid id,
        string snapshotId,
        [FromServices] ISandboxSnapshotProvider provider,
        CancellationToken ct)
    {
        try
        {
            await provider.RestoreSnapshotAsync(id, snapshotId, ct);
            return Results.Ok(new { message = $"Snapshot {snapshotId} restored successfully" });
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteSnapshotAsync(
        Guid id,
        string snapshotId,
        [FromServices] ISandboxSnapshotProvider provider,
        CancellationToken ct)
    {
        await provider.DeleteSnapshotAsync(id, snapshotId, ct);
        return Results.NoContent();
    }
}

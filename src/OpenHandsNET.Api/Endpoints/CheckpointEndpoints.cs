using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Checkpoints;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Events;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-43: checkpoints do workdir — list/detail/snapshot manual/revert.
/// Revert tem guarda de run-ativa (409 <c>run_active</c>) e drift
/// (409 <c>{conflicts}</c> sem <c>force</c>); pós-revert emite
/// <c>files_changed</c> + snapshot <c>pre-revert</c> automático.
/// </summary>
public static partial class CheckpointEndpoints
{
    /// <summary>Maps the checkpoints endpoints.</summary>
    public static IEndpointRouteBuilder MapCheckpointEndpoints(
        this IEndpointRouteBuilder app)
    {
        var conv = app.MapGroup("/api/v1/app-conversations")
            .WithTags("Checkpoints");

        conv.MapGet("/{id:guid}/checkpoints", ListAsync).ProducesProblem(503);
        conv.MapPost("/{id:guid}/checkpoints", SnapshotAsync).ProducesProblem(503);
        conv.MapGet("/{id:guid}/checkpoints/{hash}", GetAsync).ProducesProblem(503);
        conv.MapPost("/{id:guid}/checkpoints/{hash}/revert", RevertAsync).ProducesProblem(503);
        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid id, IAppConversationService svc, ICheckpointService ckpts,
        HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOrProblem(id, svc, ckpts, http, ct);
        if (sb.Error is not null) { return sb.Error; }
        return Results.Ok(await ckpts.ListAsync(sb.Sandbox!, ct));
    }

    private static async Task<IResult> GetAsync(
        Guid id, string hash, IAppConversationService svc,
        ICheckpointService ckpts, HttpContext http, CancellationToken ct)
    {
        if (!HashRe().IsMatch(hash))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "invalid hash");
        }

        var sb = await SandboxOrProblem(id, svc, ckpts, http, ct);
        if (sb.Error is not null) { return sb.Error; }

        var detail = await ckpts.GetAsync(sb.Sandbox!, hash, ct);
        return detail is null
            ? OpenHandsNetProblemFactory.Create(
                http, 404, $"checkpoint not found: {hash}")
            : Results.Ok(detail);
    }

    private static async Task<IResult> SnapshotAsync(
        Guid id, SnapshotRequest? body, IAppConversationService svc,
        ICheckpointService ckpts, HttpContext http, CancellationToken ct)
    {
        var sb = await SandboxOrProblem(id, svc, ckpts, http, ct);
        if (sb.Error is not null) { return sb.Error; }

        var item = await ckpts.SnapshotAsync(id, sb.Sandbox!,
            body?.Label ?? "manual", ct);
        return Results.Ok(item);
    }

    private static async Task<IResult> RevertAsync(
        Guid id, string hash, RevertRequest? body,
        IAppConversationService svc, ICheckpointService ckpts,
        IEventService events, HttpContext http, CancellationToken ct)
    {
        if (!HashRe().IsMatch(hash))
        {
            return OpenHandsNetProblemFactory.Create(
                http, 400, "invalid hash");
        }

        var conv = await svc.GetAsync(id, ct);
        if (conv is null)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 404, "conversation not found");
        }

        // Guarda 1: run ativa — nunca reverte enquanto o agente escreve.
        if (conv.ExecutionStatus
            == Domain.Conversations.ConversationExecutionStatus.Running)
        {
            return OpenHandsNetProblemFactory.Create(
                http, 409, "run_active");
        }

        var sb = await SandboxOrProblem(id, svc, ckpts, http, ct);
        if (sb.Error is not null) { return sb.Error; }

        // Guarda 2: drift — tracked modificados após o checkpoint.
        var (conflicts, untracked) =
            await ckpts.DriftAsync(sb.Sandbox!, hash, ct);
        if (conflicts.Count > 0 && body?.Force is not true)
        {
            return Results.Conflict(new
            {
                error = "drift",
                conflicts,
                untracked,
            });
        }

        // Snapshot pre-revert torna o revert reversível.
        var pre = await ckpts.SnapshotAsync(
            id, sb.Sandbox!, $"pre-revert {hash[..7]}", ct);
        await ckpts.RevertAsync(id, sb.Sandbox!, hash, ct);

        // Avisa UI/IDE que o workdir mudou.
        await events.AppendAsync(id, new FilesChangedEvent
        {
            SourceTool = "checkpoint_revert",
            Paths = conflicts.Select(p =>
                new FileChange(p, "modify")).ToList(),
        }, CancellationToken.None);

        return Results.Ok(new CheckpointRevertResult
        {
            Reverted = true,
            Conflicts = conflicts,
            Untracked = untracked,
            PreRevertHash = pre.Hash,
        });
    }

    /// <summary>
    /// Guarda comum: conversa + sandbox RUNNING + repo git.
    /// 503 <c>sandbox_not_running</c> (sleeping — client oferece wake) e
    /// 404 <c>no_repository</c> desabilitam a feature.
    /// </summary>
    private static async Task<(Domain.Sandbox.Sandbox? Sandbox,
        IResult? Error)> SandboxOrProblem(Guid id,
            IAppConversationService svc, ICheckpointService ckpts,
            HttpContext http, CancellationToken ct)
    {
        var conv = await svc.GetAsync(id, ct);
        if (conv?.SandboxId is null)
        {
            return (null, OpenHandsNetProblemFactory.Create(
                http, 503, OpenHandsNetProblemFactory.SandboxNotRunning));
        }

        var sandboxes = http.RequestServices
            .GetRequiredService<ISandboxService>();
        var sb = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        if (sb?.Status is not Domain.Sandbox.SandboxStatus.Running)
        {
            return (null, OpenHandsNetProblemFactory.Create(
                http, 503, OpenHandsNetProblemFactory.SandboxNotRunning));
        }

        if (!await ckpts.IsAvailableAsync(sb, ct))
        {
            return (null, OpenHandsNetProblemFactory.Create(
                http, 404, "no_repository"));
        }

        return (sb, null);
    }

    private sealed record SnapshotRequest(string? Label);
    private sealed record RevertRequest(bool Force);

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashRe();
}
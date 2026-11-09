using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Checkpoints;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;

namespace Sandcastle.Api.Endpoints;

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

        conv.MapGet("/{id:guid}/checkpoints", ListAsync).ProducesProblem(404).ProducesProblem(409);
        conv.MapPost("/{id:guid}/checkpoints", SnapshotAsync).ProducesProblem(404).ProducesProblem(409);
        conv.MapGet("/{id:guid}/checkpoints/{hash}", GetAsync).ProducesProblem(404).ProducesProblem(409);
        conv.MapPost("/{id:guid}/checkpoints/{hash}/revert", RevertAsync).ProducesProblem(404).ProducesProblem(409);
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
            return SandcastleProblemFactory.Create(
                http, 400, "invalid hash");
        }

        var sb = await SandboxOrProblem(id, svc, ckpts, http, ct);
        if (sb.Error is not null) { return sb.Error; }

        var detail = await ckpts.GetAsync(sb.Sandbox!, hash, ct);
        return detail is null
            ? SandcastleProblemFactory.Create(
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
        [AsParameters] RevertParams p)
    {
        if (!HashRe().IsMatch(p.Hash))
        {
            return SandcastleProblemFactory.Create(
                p.Http, 400, "invalid p.Hash");
        }

        var conv = await p.Svc.GetAsync(p.Id, p.Ct);
        if (conv is null)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 404, "conversation not found");
        }

        // Guarda 1: run ativa — nunca reverte enquanto o agente escreve.
        if (conv.ExecutionStatus
            == Domain.Conversations.ConversationExecutionStatus.Running)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 409, "run_active");
        }

        var sb = await SandboxOrProblem(p.Id, p.Svc, p.Ckpts, p.Http, p.Ct);
        if (sb.Error is not null) { return sb.Error; }

        // Guarda 2: drift — tracked modificados após o checkpoint.
        var (conflicts, untracked) =
            await p.Ckpts.DriftAsync(sb.Sandbox!, p.Hash, p.Ct);
        if (conflicts.Count > 0 && p.Body?.Force is not true)
        {
            return Results.Conflict(new
            {
                error = "drift",
                conflicts,
                untracked,
            });
        }

        // Snapshot pre-revert torna o revert reversível.
        var pre = await p.Ckpts.SnapshotAsync(
            p.Id, sb.Sandbox!, $"pre-revert {p.Hash[..7]}", p.Ct);
        await p.Ckpts.RevertAsync(p.Id, sb.Sandbox!, p.Hash, p.Ct);

        // Avisa UI/IDE que o workdir mudou.
        await p.Events.AppendAsync(p.Id, new FilesChangedEvent
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
    /// 404 sem conversa, 409 <c>sandbox_not_running</c> (sleeping —
    /// client oferece wake) e 404 <c>no_repository</c> desabilitam a
    /// feature.
    /// </summary>
    private static async Task<(Domain.Sandbox.Sandbox? Sandbox,
        IResult? Error)> SandboxOrProblem(Guid id,
            IAppConversationService svc, ICheckpointService ckpts,
            HttpContext http, CancellationToken ct)
    {
        var conv = await svc.GetAsync(id, ct);
        if (conv is null)
        {
            return (null, SandcastleProblemFactory.Create(
                http, 404, "conversation not found"));
        }

        if (conv.SandboxId is null)
        {
            return (null, SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning));
        }

        var sandboxes = http.RequestServices
            .GetRequiredService<ISandboxService>();
        var sb = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        if (sb?.Status is not Domain.Sandbox.SandboxStatus.Running)
        {
            return (null, SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning));
        }

        if (!await ckpts.IsAvailableAsync(sb, ct))
        {
            return (null, SandcastleProblemFactory.Create(
                http, 404, "no_repository"));
        }

        return (sb, null);
    }

    private sealed record SnapshotRequest(string? Label);
    private sealed record RevertRequest(bool Force);

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashRe();

    /// <summary>Binding do revert de checkpoint (SPEC-104 — AsParameters).</summary>
    private sealed class RevertParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromRoute] public string Hash { get; init; } = null!;
        [FromBody] public RevertRequest? Body { get; init; }
        [FromServices] public IAppConversationService Svc { get; init; } = null!;
        [FromServices] public ICheckpointService Ckpts { get; init; } = null!;
        [FromServices] public IEventService Events { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }
}
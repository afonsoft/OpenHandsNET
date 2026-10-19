using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Git;
using OpenHandsNET.Application.Identity;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Application.Scm;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Scm;

/// <summary>
/// <see cref="IScmPullRequestService"/> (SPEC-52 RF-003): push do work
/// branch do sandbox → <see cref="IScmProvider.CreatePullRequestAsync"/> →
/// persiste <c>pull_request_*</c> na conversa + evento
/// <c>pull_request_created</c> no stream.
/// </summary>
public sealed class ScmPullRequestService(
    AppDbContext db,
    IScmConnectionService connections,
    IScmProviderRegistry providers,
    ISandboxService sandboxes,
    IGitService git,
    IEventService events,
    IAuditService audit,
    ILogger<ScmPullRequestService> logger) : IScmPullRequestService
{
    /// <inheritdoc />
    public async Task<ScmPrResult> CreateAsync(
        Guid conversationId, ScmPrCreateRequest request,
        string? actorUserId, CancellationToken ct)
    {
        var conv = await db.Conversations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conv is null)
        {
            return ScmPrResult.Fail(ScmPrError.NotFound);
        }
        if (conv.PullRequestNumber is not null)
        {
            return ScmPrResult.Fail(ScmPrError.AlreadyExists);
        }
        if (conv.OrgId is null || conv.SelectedRepository is null
            || conv.SandboxId is null)
        {
            return ScmPrResult.Fail(ScmPrError.NoConnection);
        }

        var repository = conv.SelectedRepository;
        var connection = await connections.FindForRepositoryAsync(
            conv.OrgId.Value, ScmKind.GitHub, repository, ct);
        if (connection is null)
        {
            return ScmPrResult.Fail(ScmPrError.NoConnection);
        }
        var provider = providers.Get(connection.Kind);
        if (provider is null)
        {
            return ScmPrResult.Fail(ScmPrError.ProviderError);
        }

        var sandbox = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        if (sandbox is null)
        {
            return ScmPrResult.Fail(ScmPrError.ProviderError);
        }

        try
        {
            var head = request.Head;
            if (string.IsNullOrWhiteSpace(head))
            {
                head = await git.GetCurrentBranchAsync(sandbox, ct)
                    ?? "HEAD";
            }

            var push = await git.PushAsync(
                sandbox, head, head, forceWithLease: false,
                conv.CreatedByUserId, ct);
            if (!push.Success)
            {
                logger.LogWarning("scm pr push failed (conv {Id}): {Err}",
                    conversationId, push.Error);
                return ScmPrResult.Fail(ScmPrError.ProviderError);
            }

            var pr = await provider.CreatePullRequestAsync(
                connection, repository,
                request with { Head = head }, ct);

            conv.PullRequestUrl = pr.Url;
            conv.PullRequestNumber = pr.Number;
            conv.PullRequestState = pr.State;
            conv.PullRequestHeadSha = push.HeadSha ?? pr.HeadSha;
            conv.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await events.AppendAsync(conversationId,
                new PullRequestCreatedEvent
                {
                    Url = pr.Url,
                    Number = pr.Number,
                    Repository = repository,
                    ScmKind = connection.Kind.ToWire(),
                }, ct);
            await audit.RecordAsync("scm.pr_created",
                actorUserId: actorUserId, orgId: conv.OrgId,
                targetType: "app_conversation",
                targetId: conversationId.ToString(),
                metadata: new { pr = pr.Url, repository },
                cancellationToken: ct);
            return ScmPrResult.Ok(pr);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "scm pr create failed (conv {Id})",
                conversationId);
            return ScmPrResult.Fail(ScmPrError.ProviderError);
        }
    }
}

/// <summary>
/// <see cref="IScmStatusReporter"/> (SPEC-52 RF-005): posta
/// <c>openhandsnet/run</c> no head SHA registrado — melhor esforço,
/// exceções só logadas.
/// </summary>
public sealed class ScmStatusReporter(
    AppDbContext db,
    IScmConnectionService connections,
    IScmProviderRegistry providers,
    ILogger<ScmStatusReporter> logger) : IScmStatusReporter
{
    /// <summary>Status context publicado nos commits.</summary>
    public const string StatusContext = "openhandsnet/run";

    /// <inheritdoc />
    public async Task ReportRunFinishedAsync(
        Guid conversationId, bool success, CancellationToken ct)
    {
        try
        {
            var conv = await db.Conversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
            if (conv?.OrgId is null || conv.PullRequestNumber is null
                || conv.SelectedRepository is null
                || conv.PullRequestHeadSha is null)
            {
                return;
            }

            var connection = await connections.FindForRepositoryAsync(
                conv.OrgId.Value, ScmKind.GitHub,
                conv.SelectedRepository, ct);
            var provider = connection is null
                ? null : providers.Get(connection.Kind);
            if (provider is null || connection is null)
            {
                return;
            }

            await provider.SetCommitStatusAsync(
                connection, conv.SelectedRepository,
                conv.PullRequestHeadSha,
                success ? "success" : "failure",
                StatusContext,
                targetUrl: $"/conversations/{conversationId}",
                description: success
                    ? "OpenHandsNET run finished" : "OpenHandsNET run failed",
                ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "scm commit status report skipped (conv {Id})",
                conversationId);
        }
    }
}
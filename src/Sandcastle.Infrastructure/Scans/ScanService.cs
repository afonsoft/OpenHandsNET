using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Scans;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// <see cref="IScanService"/> — SPEC-143. Creation builds the coordinator
/// conversation (agent_type <c>scan-coordinator</c>, read-only permission
/// profile so every delegated child inherits the deny-list), persists the
/// scan row and enqueues orchestration. Reads are org-scoped via
/// <c>IgnoreQueryFilters</c> + explicit <c>OrgId</c> predicates (the
/// global filter is user-context-bound; services run outside it).
/// </summary>
public sealed class ScanService(
    AppDbContext db,
    IAppConversationService conversations,
    IScanDispatcher dispatcher,
    IOptions<ScanOptions> options,
    ILogger<ScanService> logger,
    IAuditService? audit = null,
    IScmConnectionService? connections = null,
    IScmProviderRegistry? providers = null)
    : IScanService
{
    private static readonly HashSet<string> TerminalConv =
        new(StringComparer.Ordinal)
        {
            "Stopped", "Finished", "Error", "Archived",
            "COMPLETED", "FAILED", "STOPPED", "CANCELLED",
        };

    /// <inheritdoc />
    public async Task<(ScanCreated? Created, string? Error)> CreateAsync(
        CreateScanRequest request, Guid ownerUserId, Guid? orgId,
        string trigger, CancellationToken ct)
    {
        // --- validation (400 surface) ---
        var goal = request.Goal?.Trim();
        if (string.IsNullOrEmpty(goal))
        {
            return (null, "goal is required");
        }
        if (goal.Length > 4000)
        {
            return (null, "goal too long (max 4000 chars)");
        }

        var profile = ScanProfiles.Normalize(request.Profile);
        if (profile is null)
        {
            return (null,
                $"invalid profile '{request.Profile}' (security|" +
                "performance|dead-code|coverage-gaps|a11y|custom)");
        }
        var effort = ScanEfforts.Normalize(request.Effort);
        if (effort is null)
        {
            return (null,
                $"invalid effort '{request.Effort}' (low|medium|high)");
        }

        IReadOnlyList<string> repos;
        try
        {
            repos = RepositorySelection.Normalize(
                null, request.Repositories, options.Value.MaxRepos);
        }
        catch (RepositorySelectionException e)
        {
            return (null, e.Message);
        }
        if (repos.Count == 0)
        {
            return (null, "at least one repository is required");
        }

        // --- coordinator conversation (shell — worker drives the
        // fan-out via IDelegationService; Run:false = sem agent loop).
        // PermissionsJson read-only → herdado por TODOS os filhos
        // (DelegationService nunca relaxa regras do pai).
        var coordinatorTask = await conversations.CreateForUserAsync(
            ownerUserId, orgId,
            new CreateConversationRequest(
                InitialMessage: new InitialMessage(
                    "user",
                    [$"Code scan ({profile}, {effort}): {goal}"],
                    Run: false),
                SelectedRepository: repos[0],
                AgentType: ScanAgentTypes.Coordinator,
                LlmModel: null,
                SandboxSpecId: null,
                Permissions: ScanPermissions.ReadOnly),
            ct);

        var now = DateTimeOffset.UtcNow;
        var scan = new ScanRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = ownerUserId.ToString(),
            RepositoriesJson = JsonSerializer.Serialize(repos),
            Goal = goal,
            Profile = profile,
            Effort = effort,
            Status = ScanStatuses.Queued,
            Trigger = trigger,
            CoordinatorConversationId = coordinatorTask.AppConversationId,
            CreatedAt = now,
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(ct);

        await AuditAsync("scan.created", ownerUserId, orgId, scan.Id,
            new
            {
                profile,
                effort,
                repos = repos.Count,
                trigger,
                coordinator = coordinatorTask.AppConversationId,
            }, ct);

        await dispatcher.EnqueueAsync(scan.Id, ct);
        return (new ScanCreated(
            scan.Id,
            coordinatorTask.AppConversationId ?? Guid.Empty,
            scan.Status), null);
    }

    /// <inheritdoc />
    public async Task<ScanSummary?> GetAsync(
        Guid orgId, Guid scanId, CancellationToken ct)
    {
        var scan = await db.Scans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.Id == scanId && s.OrgId == orgId, ct);
        if (scan is null)
        {
            return null;
        }

        var counts = await SeverityCountsAsync([scanId], ct);
        var total = counts.GetValueOrDefault(scanId);
        return ToSummary(scan, total.Total, total.BySeverity);
    }

    /// <inheritdoc />
    public async Task<ScanPage> ListAsync(
        Guid orgId, string? status, string? cursor, int limit,
        CancellationToken ct)
    {
        var q = db.Scans.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            q = q.Where(s => s.Status == status);
        }
        if (CursorPaginator.TryDecode(cursor, out var ticks, out var raw)
            && Guid.TryParse(raw, out _))
        {
            // Sqlite persiste Guid como TEXT uppercase; PG serializa uuid
            // lowercase — normalizar os dois lados via LOWER() mantém o
            // tie-break consistente com o ORDER BY id (transformação
            // monótona dentro do domínio de storage de cada provider).
            var id = raw.ToLowerInvariant();
            q = q.Where(s => s.CreatedAtTicks < ticks
                || (s.CreatedAtTicks == ticks
                    && string.Compare(s.Id.ToString().ToLower(), id) < 0));
        }

        var pageSize = Math.Clamp(limit, 1, 100);
        var rows = await q
            .OrderByDescending(s => s.CreatedAtTicks)
            .ThenByDescending(s => s.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = CursorPaginator.Encode(
                last.CreatedAtTicks, last.Id.ToString());
        }

        var counts = await SeverityCountsAsync(
            rows.Select(r => r.Id).ToList(), ct);
        return new ScanPage(
            rows.Select(r =>
            {
                var c = counts.GetValueOrDefault(r.Id);
                return ToSummary(r, c.Total, c.BySeverity);
            }).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<ScanFindingPage> SearchFindingsAsync(
        Guid orgId, ScanFindingQuery query, CancellationToken ct)
    {
        var q = db.ScanFindings.IgnoreQueryFilters()
            .Where(f => f.OrgId == orgId);
        if (query.ScanId is { } sid)
        {
            q = q.Where(f => f.ScanId == sid);
        }
        if (!string.IsNullOrWhiteSpace(query.Repository))
        {
            q = q.Where(f => f.Repository == query.Repository);
        }
        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            q = q.Where(f => f.Severity == query.Severity);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            q = q.Where(f => f.Status == query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            q = q.Where(f => f.Kind == query.Kind);
        }
        if (!string.IsNullOrWhiteSpace(query.RemediationState))
        {
            q = q.Where(f =>
                f.RemediationState == query.RemediationState);
        }
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var needle = query.Q.Trim();
            q = q.Where(f =>
                f.Title != null && f.Title.Contains(needle)
                || f.Description.Contains(needle)
                || f.Path.Contains(needle));
        }
        if (CursorPaginator.TryDecode(
                query.After, out var ticks, out var raw)
            && Guid.TryParse(raw, out _))
        {
            var id = raw.ToLowerInvariant();
            q = q.Where(f => f.CreatedAtTicks < ticks
                || (f.CreatedAtTicks == ticks
                    && string.Compare(f.Id.ToString().ToLower(), id) < 0));
        }

        // SPEC-143: page size cap 200 (Devin parity first≤200).
        var pageSize = Math.Clamp(query.First ?? 50, 1, 200);
        var rows = await q
            .OrderByDescending(f => f.CreatedAtTicks)
            .ThenByDescending(f => f.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = CursorPaginator.Encode(
                last.CreatedAtTicks, last.Id.ToString());
        }
        return new ScanFindingPage(
            rows.Select(ToInfo).ToList(), next);
    }

    /// <inheritdoc />
    public async Task<ScanFindingInfo?> GetFindingAsync(
        Guid orgId, Guid findingId, CancellationToken ct)
    {
        var f = await db.ScanFindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == findingId && x.OrgId == orgId, ct);
        return f is null ? null : ToInfo(f);
    }

    /// <inheritdoc />
    public async Task<ScanFindingInfo?> UpdateFindingStatusAsync(
        Guid orgId, Guid findingId, string status,
        CancellationToken ct)
    {
        var normalized = status.Trim().ToLowerInvariant();
        if (!ScanFindingStatuses.All.Contains(normalized))
        {
            throw new ArgumentException(
                $"invalid status '{status}' (open|reviewed|dismissed)");
        }

        var f = await db.ScanFindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == findingId && x.OrgId == orgId, ct);
        if (f is null)
        {
            return null;
        }

        f.Status = normalized;
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync($"scan_finding.{normalized}", null, orgId,
            findingId, new { scan = f.ScanId }, ct);
        return ToInfo(f);
    }

    /// <inheritdoc />
    public async Task<ScanFixResult> FixAsync(
        Guid orgId, Guid findingId, Guid actorUserId,
        CancellationToken ct)
    {
        var f = await db.ScanFindings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == findingId && x.OrgId == orgId, ct);
        if (f is null)
        {
            return ScanFixResult.Fail(findingId, "not_found");
        }
        // SPEC-143: fix só dispara por ação explícita e uma vez —
        // remediation_state != unassigned = 409 no edge.
        if (f.RemediationState != ScanRemediationStates.Unassigned)
        {
            return ScanFixResult.Fail(findingId, "fix_in_progress");
        }

        // Marca antes de criar a conversa — bulk paralela não duplica.
        f.RemediationState = ScanRemediationStates.Assigned;
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var task = await conversations.CreateForUserAsync(
            actorUserId, orgId,
            new CreateConversationRequest(
                InitialMessage: new InitialMessage(
                    "user", [BuildFixPrompt(f)], Run: true),
                SelectedRepository: f.Repository,
                AgentType: ScanAgentTypes.Fix,
                LlmModel: null,
                SandboxSpecId: null),
            ct);

        f.ConversationId = task.AppConversationId;
        f.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await AuditAsync("scan_finding.fix_started", actorUserId, orgId,
            f.Id, new
            {
                scan = f.ScanId,
                f.Repository,
                f.Path,
                conversation = task.AppConversationId,
            }, ct);
        return ScanFixResult.Ok(
            findingId, task.AppConversationId ?? Guid.Empty);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScanFixResult>> FixBulkAsync(
        Guid orgId, IReadOnlyList<Guid> ids, Guid actorUserId,
        CancellationToken ct)
    {
        var results = new List<ScanFixResult>(ids.Count);
        foreach (var id in ids.Distinct().Take(50))
        {
            results.Add(await FixAsync(orgId, id, actorUserId, ct));
        }
        return results;
    }

    /// <inheritdoc />
    public async Task ReconcileRemediationsAsync(CancellationToken ct)
    {
        // Poll DB-first: assigned→pr_open via pull_request_* do fix conv.
        var assigned = await db.ScanFindings.IgnoreQueryFilters()
            .Where(f => f.RemediationState
                    == ScanRemediationStates.Assigned
                && f.ConversationId != null)
            .Take(200).ToListAsync(ct);
        var convIds = assigned.Select(f => f.ConversationId!.Value)
            .ToList();
        var convs = await db.Conversations.IgnoreQueryFilters()
            .Where(c => convIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var changed = false;
        foreach (var f in assigned)
        {
            if (!convs.TryGetValue(f.ConversationId!.Value, out var conv))
            {
                continue;
            }

            if (conv.PullRequestUrl is not null)
            {
                f.RemediationState = ScanRemediationStates.PrOpen;
                f.PrUrl = conv.PullRequestUrl;
                f.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
                await AuditAsync("scan_finding.pr_open", null, f.OrgId,
                    f.Id, new { f.ScanId, pr = f.PrUrl }, ct);
            }
            else if (TerminalConv.Contains(conv.ExecutionStatus))
            {
                // sessão morreu sem PR → volta pra unassigned (re-fix
                // continua sendo ação explícita do usuário).
                f.RemediationState = ScanRemediationStates.Unassigned;
                f.ConversationId = null;
                f.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        // pr_open → merged|unassigned via poll do provider (o webhook
        // não sincroniza pull_request_state da conversa).
        var open = await db.ScanFindings.IgnoreQueryFilters()
            .Where(f => f.RemediationState
                    == ScanRemediationStates.PrOpen
                && f.ConversationId != null)
            .Take(200).ToListAsync(ct);
        foreach (var f in open)
        {
            var conv = await db.Conversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    c => c.Id == f.ConversationId, ct);
            if (conv is null)
            {
                continue;
            }

            var prState = conv.PullRequestState;
            if (string.Equals(prState, "open",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Closed (ou state desconhecido) → confirma merged via
            // provider (GetPullRequestAsync.Merged — SPEC-143).
            var merged = await IsMergedAsync(conv, ct);
            f.RemediationState = merged
                ? ScanRemediationStates.Merged
                : ScanRemediationStates.Unassigned;
            if (!merged)
            {
                f.ConversationId = null;
                f.PrUrl = null;
            }
            f.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
            if (merged)
            {
                await AuditAsync("scan_finding.merged", null, f.OrgId,
                    f.Id, new { f.ScanId, pr = f.PrUrl }, ct);
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Confirma merge do PR do fix conv via provider (closed ≠ merged no
    /// GitHub). Falhas de poll não movem o estado.
    /// </summary>
    private async Task<bool> IsMergedAsync(
        Conversations.ConversationRecord conv, CancellationToken ct)
    {
        if (connections is null || providers is null
            || conv.OrgId is null || conv.SelectedRepository is null
            || conv.PullRequestNumber is null)
        {
            return false;
        }

        try
        {
            var connection = await connections
                .FindForRepositoryAsync(conv.OrgId.Value, ScmKind.GitHub,
                    conv.SelectedRepository, ct);
            if (connection is null)
            {
                return false;
            }
            var provider = providers.Get(connection.Kind);
            var pr = provider is null
                ? null
                : await provider.GetPullRequestAsync(connection,
                    conv.SelectedRepository,
                    conv.PullRequestNumber.Value, ct);
            return pr?.Merged == true;
        }
        catch (Exception e)
        {
            logger.LogDebug(e,
                "scan reconcile: pr poll failed conv {Id}", conv.Id);
            return false;
        }
    }

    private async Task AuditAsync(string action, Guid? actor,
        Guid? orgId, Guid targetId, object? metadata,
        CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: actor?.ToString(), orgId: orgId,
                targetType: "scan", targetId: targetId.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "scan audit {Action} skipped", action);
        }
    }

    private async Task<Dictionary<Guid, (int Total,
        Dictionary<string, int> BySeverity)>> SeverityCountsAsync(
        List<Guid> scanIds, CancellationToken ct)
    {
        if (scanIds.Count == 0)
        {
            return [];
        }

        var rows = await db.ScanFindings.IgnoreQueryFilters()
            .Where(f => scanIds.Contains(f.ScanId))
            .GroupBy(f => new { f.ScanId, f.Severity })
            .Select(g => new
            { g.Key.ScanId, g.Key.Severity, N = g.Count() })
            .ToListAsync(ct);

        var result = new Dictionary<Guid,
            (int, Dictionary<string, int>)>();
        foreach (var r in rows)
        {
            if (!result.TryGetValue(r.ScanId, out var t))
            {
                t = (0, new Dictionary<string, int>(
                    StringComparer.Ordinal));
            }
            t.Item2[r.Severity] = r.N;
            result[r.ScanId] = (t.Item1 + r.N, t.Item2);
        }
        return result;
    }

    private static ScanSummary ToSummary(ScanRecord s, int total,
        IReadOnlyDictionary<string, int> bySeverity) => new(
        s.Id, s.OrgId, DeserializeRepos(s.RepositoriesJson), s.Goal,
        s.Profile, s.Effort, s.Status, s.Trigger,
        s.CoordinatorConversationId, total, bySeverity,
        s.Error, s.CreatedAt, s.FinishedAt);

    internal static IReadOnlyList<string> DeserializeRepos(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ScanFindingInfo ToInfo(ScanFindingRecord f) => new(
        f.Id, f.ScanId, f.OrgId, f.Repository, f.Path,
        f.StartLine, f.EndLine, f.Kind, f.Severity, f.Confidence,
        f.Title, f.Description, f.RemediationHint, f.Status,
        f.RemediationState, f.ConversationId, f.PrUrl, f.Payoff,
        f.CreatedAt, f.UpdatedAt);

    /// <summary>
    /// Prompt do fix conv — contexto completo do finding + instrução de
    /// implementar e abrir PR. Nunca auto-dispara (só via POST /fix).
    /// </summary>
    internal static string BuildFixPrompt(ScanFindingRecord f)
    {
        var lines = f.StartLine is { } sl
            ? f.EndLine is { } el ? $" lines {sl}-{el}" : $" line {sl}"
            : "";
        var hint = string.IsNullOrWhiteSpace(f.RemediationHint)
            ? "" : $"\nRemediation hint: {f.RemediationHint}";
        return
            $"Fix this {f.Severity} {f.Kind} finding from a code scan "
            + $"in `{f.Repository}`.\n\n"
            + $"File: `{f.Path}`{lines}\n"
            + $"Title: {f.Title ?? "(untitled)"}\n"
            + $"Description: {f.Description}{hint}\n\n"
            + "Implement the fix, run the relevant tests, commit on a "
            + "feature branch and open a pull request.";
    }
}
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Delegation;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Indexing;
using Sandcastle.Application.Scans;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// <see cref="IScanOrchestrator"/> — SPEC-143 RF-002 (o "Security Swarm"
/// real que substitui o stub <c>SwarmOrchestrator</c>). Fluxo por scan:
/// <list type="number">
/// <item>queued → running; particiona cada repo num checkout
///   (<see cref="IRepoContentProvider"/>) via <see cref="ScanPartitioner"/>
///   — falha de TODOS os repos marca <c>failed</c>, falha parcial segue
///   com os que clonaram.</item>
/// <item>fan-out de filhas <c>scan-worker</c> via
///   <see cref="IDelegationService"/> em ondas de ≤
///   <c>Scan:MaxSiblings</c> ativas (o cap do pai conta só siblings
///   vivos). Filhas herdam o deny-list read-only do coordinator —
///   nenhuma mutação/commit/push durante o scan.</item>
/// <item>poll até terminal/timeout → extrai a saída da última mensagem
///   do agente → <see cref="ScanFindingParser"/> (schema
///   <c>scan_finding_v1</c>) → <see cref="SecretRedactor"/> →
///   <see cref="ScanDeduplicator"/> (+ LLM dedup opcional, role
///   <c>analysis</c>) → persist findings + stats_json.</item>
/// </list>
/// </summary>
public sealed class ScanOrchestrator(
    AppDbContext db,
    IDelegationService delegation,
    IRepoContentProvider content,
    IAgentLlmProvider llm,
    IModelRouter router,
    IOptions<ScanOptions> options,
    ILogger<ScanOrchestrator> logger,
    IAuditService? audit = null) : IScanOrchestrator
{
    /// <summary>Terminal child statuses (enum names + delegate literals).</summary>
    private static readonly HashSet<string> Terminal =
        new(StringComparer.Ordinal)
        {
            nameof(Domain.Conversations.ConversationExecutionStatus.Stopped),
            nameof(Domain.Conversations.ConversationExecutionStatus.Finished),
            nameof(Domain.Conversations.ConversationExecutionStatus.Error),
            nameof(Domain.Conversations.ConversationExecutionStatus.Archived),
            "COMPLETED", "FAILED", "STOPPED", "CANCELLED",
        };

    private sealed record ChildTask(ScanPartition Partition, string Prompt);

    private sealed record ChildRef(
        Guid ConversationId, ScanPartition Partition,
        DateTimeOffset Deadline);

    /// <inheritdoc />
    public async Task RunAsync(Guid scanId, CancellationToken ct)
    {
        var scan = await db.Scans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == scanId, ct);
        if (scan is null || ScanStatuses.Terminal.Contains(scan.Status))
        {
            return;
        }

        var o = options.Value;
        scan.Status = ScanStatuses.Running;
        scan.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var stats = new Dictionary<string, object?>
        {
            ["partitions"] = 0,
            ["children"] = 0,
            ["children_failed"] = 0,
            ["repos_scanned"] = 0,
            ["repos_failed"] = 0,
            ["findings_raw"] = 0,
            ["findings_merged"] = 0,
            ["findings_persisted"] = 0,
            ["parse_errors"] = new List<string>(),
        };
        var errors = (List<string>)stats["parse_errors"]!;

        try
        {
            var repos = ScanService.DeserializeRepos(scan.RepositoriesJson);
            if (scan.CoordinatorConversationId is not { } coordinatorId
                || repos.Count == 0)
            {
                await FailAsync(scan, "missing coordinator or repositories",
                    stats, ct);
                return;
            }

            var cap = Math.Max(1, o.ChildrenCapFor(scan.Effort));
            var perRepo = Math.Max(1, cap / repos.Count);

            // 1) partição — um clone shallow por repo (leia + libera).
            var tasks = new List<ChildTask>();
            var scannedRepos = new List<string>();
            foreach (var repo in repos)
            {
                try
                {
                    var partitions = await PartitionRepoAsync(
                        repo, scan.OrgId, perRepo, o, ct);
                    tasks.AddRange(partitions.Select(p =>
                        new ChildTask(p, BuildWorkerPrompt(scan, p))));
                    scannedRepos.Add(repo);
                }
                catch (Exception e)
                    when (e is not OperationCanceledException)
                {
                    stats["repos_failed"] =
                        (int)stats["repos_failed"]! + 1;
                    errors.Add($"{repo}: {e.Message}");
                    logger.LogWarning(e,
                        "scan {ScanId}: partition failed for {Repo}",
                        scanId, repo);
                }
            }

            stats["repos_scanned"] = scannedRepos.Count;
            stats["partitions"] = tasks.Count;
            if (tasks.Count == 0)
            {
                await FailAsync(scan,
                    "no repository could be partitioned", stats, ct);
                return;
            }

            // Hard cap global (≥1 filha por repo mesmo quando cap < repos).
            tasks = tasks.Take(Math.Max(cap, repos.Count)).ToList();

            // 2) fan-out em ondas + coleta.
            var (findings, children, failed) = await FanOutAsync(
                scan, coordinatorId, tasks, o, errors, ct);
            stats["children"] = children;
            stats["children_failed"] = failed;
            stats["findings_raw"] = findings.Count;

            // 3) dedup (heurístico + LLM opcional) → redact → persist.
            var (kept, merged) = ScanDeduplicator.Deduplicate(findings);
            stats["findings_merged"] = merged;
            if (o.LlmDedup && kept.Count > 1)
            {
                kept = await LlmDedupAsync(scan, kept, o, errors, ct);
            }

            var persisted = 0;
            foreach (var f in kept.Take(Math.Max(1, o.MaxFindings)))
            {
                db.ScanFindings.Add(new ScanFindingRecord
                {
                    Id = Guid.NewGuid(),
                    OrgId = scan.OrgId,
                    ScanId = scan.Id,
                    Repository = f.Repository,
                    Path = SecretRedactor.Redact(f.Path),
                    StartLine = f.StartLine,
                    EndLine = f.EndLine,
                    Kind = f.Kind,
                    Severity = f.Severity,
                    Confidence = f.Confidence,
                    Title = f.Title is null ? null
                        : SecretRedactor.Redact(f.Title),
                    Description = SecretRedactor.Redact(f.Description),
                    RemediationHint = f.RemediationHint is null ? null
                        : SecretRedactor.Redact(f.RemediationHint),
                    Status = ScanFindingStatuses.Open,
                    RemediationState = ScanRemediationStates.Unassigned,
                    Payoff = f.Payoff,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                persisted++;
            }

            stats["findings_persisted"] = persisted;
            scan.Status = ScanStatuses.Completed;
            scan.FinishedAt = DateTimeOffset.UtcNow;
            scan.StatsJson = JsonSerializer.Serialize(stats);
            await db.SaveChangesAsync(ct);
            await AuditAsync("scan.completed", scan, stats, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "scan {ScanId} orchestration failed", scanId);
            try
            {
                await FailAsync(scan, e.Message, stats, CancellationToken.None);
            }
            catch (Exception inner)
            {
                logger.LogWarning(inner,
                    "scan {ScanId} failed-status write failed", scanId);
            }
        }
    }

    /// <summary>
    /// Partitions one repo: shallow-clone via the content provider, run
    /// <see cref="ScanPartitioner"/> inside the lease, dispose the workdir.
    /// </summary>
    private async Task<IReadOnlyList<ScanPartition>> PartitionRepoAsync(
        string repo, Guid? orgId, int target, ScanOptions o,
        CancellationToken ct)
    {
        await using var lease = await content.AcquireAsync(
            new RepoContentRequest(repo, "", orgId), ct);
        if (lease is null)
        {
            throw new InvalidOperationException("no content provider");
        }

        return ScanPartitioner.Partition(
            lease.RootPath, repo, target, o.MaxDirsPerPartition);
    }

    /// <summary>
    /// Wave fan-out: delegates up to <see cref="ScanOptions.MaxSiblings"/>
    /// active children, polls until terminal/per-child timeout, collects
    /// parsed findings. Returns (findings, spawned, failed).
    /// </summary>
    private async Task<(List<ScanFindingInput> Findings, int Spawned,
        int Failed)> FanOutAsync(
        ScanRecord scan, Guid coordinatorId, List<ChildTask> pending,
        ScanOptions o, List<string> errors, CancellationToken ct)
    {
        var maxSiblings = Math.Clamp(o.MaxSiblings, 1, 8);
        var poll = TimeSpan.FromSeconds(
            Math.Clamp(o.PollIntervalSeconds, 2, 120));
        var childTimeout = TimeSpan.FromMinutes(
            Math.Max(1, o.ChildTimeoutMinutes));

        var active = new List<ChildRef>();
        var done = new List<(ChildRef Ref, string Status)>();
        var spawned = 0;
        var queue = new Queue<ChildTask>(pending);

        while (queue.Count > 0 || active.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Abre vagas da wave — o cap do pai conta só siblings ativos.
            while (queue.Count > 0 && active.Count < maxSiblings)
            {
                var task = queue.Dequeue();
                try
                {
                    var res = await delegation.DelegateAsync(
                        coordinatorId,
                        new DelegateTaskRequest(
                            task.Prompt,
                            SelectedRepository: task.Partition.Repository,
                            AgentType: ScanAgentTypes.Worker),
                        ct);
                    active.Add(new ChildRef(
                        res.ChildConversationId, task.Partition,
                        DateTimeOffset.UtcNow + childTimeout));
                    spawned++;
                }
                catch (InvalidOperationException e)
                {
                    // Cap cheio numa race — reenfileira e vota no poll.
                    queue.Enqueue(task);
                    logger.LogDebug(e,
                        "scan {ScanId}: delegate gated, retry next wave",
                        scan.Id);
                    break;
                }
                catch (Exception e)
                    when (e is not OperationCanceledException)
                {
                    errors.Add(
                        $"{task.Partition.Repository}:{task.Partition.Label}"
                        + $" delegate: {e.Message}");
                    logger.LogWarning(e,
                        "scan {ScanId}: delegate failed", scan.Id);
                }
            }

            if (active.Count == 0)
            {
                break;
            }

            await Task.Delay(poll, ct);

            var ids = active.Select(a => a.ConversationId).ToList();
            var statuses = await db.Conversations.IgnoreQueryFilters()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.ExecutionStatus })
                .ToDictionaryAsync(x => x.Id, x => x.ExecutionStatus, ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var a in active.ToList())
            {
                var terminal = statuses.TryGetValue(a.ConversationId,
                    out var st) && Terminal.Contains(st);
                if (terminal || now >= a.Deadline)
                {
                    done.Add((a, terminal ? st! : "timeout"));
                    active.Remove(a);
                }
            }
        }

        // 3) coleta — saída final de cada filha → scan_finding_v1.
        var findings = new List<ScanFindingInput>();
        var failed = 0;
        foreach (var (child, status) in done)
        {
            try
            {
                var parsed = await CollectChildAsync(
                    scan, child, ct);
                findings.AddRange(parsed);
            }
            catch (Exception e)
                when (e is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{child.Partition.Repository}:"
                    + $"{child.Partition.Label} collect({status}):"
                    + $" {e.Message}");
            }
        }

        return (findings, spawned, failed);
    }

    /// <summary>
    /// Extrai findings da filha: última mensagem(s) do agente (até 8,
    /// mais novas primeiro) → primeiro payload que parseia
    /// <c>scan_finding_v1</c>. Filha sem bloco válido = erro de coleta.
    /// </summary>
    private async Task<IReadOnlyList<ScanFindingInput>> CollectChildAsync(
        ScanRecord scan, ChildRef child, CancellationToken ct)
    {
        var payloads = await db.Events.IgnoreQueryFilters()
            .Where(e => e.ConversationId == child.ConversationId
                && e.Kind == "MessageEvent" && e.Source == "agent"
                && e.TruncatedAtTicks == null)
            .OrderByDescending(e => e.TimestampTicks)
            .Take(8)
            .Select(e => e.Payload)
            .ToListAsync(ct);

        string? lastError = null;
        foreach (var payload in payloads)
        {
            var text = ExtractText(payload);
            if (text is null)
            {
                continue;
            }

            var (findings, error) = ScanFindingParser.Parse(text);
            if (error is null)
            {
                return findings.Select(f => f with
                {
                    Repository = child.Partition.Repository,
                }).ToList();
            }

            lastError = error;
        }

        return [];
    }

    /// <summary>Texto do <c>llm_message.content</c> de um MessageEvent.</summary>
    internal static string? ExtractText(string payload)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<MessageEvent>(
                payload, EventJson.Options);
            if (msg is null)
            {
                return null;
            }

            if (!msg.LlmMessage.TryGetProperty(
                    "content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(t.GetString());
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// LLM dedup opcional (role <c>analysis</c>): pede grupos de índices
    /// duplicados e colapsa cada grupo no de maior severidade. Falha de
    /// parse/provider → mantém o resultado heurístico.
    /// </summary>
    private async Task<IReadOnlyList<ScanFindingInput>> LlmDedupAsync(
        ScanRecord scan, IReadOnlyList<ScanFindingInput> kept,
        ScanOptions o, List<string> errors, CancellationToken ct)
    {
        try
        {
            // SPEC-149: dedup usa a role analysis com o org do scan.
            var model = (await router.ResolveAsync(LlmRole.Analysis,
                new LlmRouteContext(OrgId: scan.OrgId,
                    PreferredModel: o.Model), ct))?.Model
                ?? throw new LlmNoRouteException(
                    "analysis", "no eligible candidate");
            var sb = new StringBuilder();
            for (var i = 0; i < kept.Count; i++)
            {
                var f = kept[i];
                sb.Append(i).Append(") ").Append(f.Kind)
                    .Append('/').Append(f.Severity).Append(' ')
                    .Append(f.Path);
                if (f.StartLine is { } sl)
                {
                    sb.Append(':').Append(sl);
                }
                sb.Append(" — ")
                    .Append(f.Title ?? f.Description[..Math.Min(
                        120, f.Description.Length)])
                    .Append('\n');
            }

            var system = EmbeddedPrompts.Get("scan-dedup");
            var text = new StringBuilder();
            await foreach (var chunk in llm.ChatStreamAsync(
                [
                    new LlmMessage("system", system),
                    LlmMessage.User(sb.ToString()),
                ], [],
                new LlmOptions(Model: model, Temperature: 0,
                    MaxTokens: 2000),
                ct))
            {
                if (chunk is TextDelta d)
                {
                    text.Append(d.Text);
                }
            }

            return ApplyDupGroups(kept, text.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors.Add($"llm_dedup: {e.Message}");
            logger.LogDebug(e, "scan {ScanId} llm dedup skipped", scan.Id);
            return kept;
        }
    }

    /// <summary>
    /// Parses <c>{"duplicates":[[i,j,…],…]}</c> da resposta do dedup e
    /// remove os não-vencedores de cada grupo (max severity).
    /// </summary>
    internal static IReadOnlyList<ScanFindingInput> ApplyDupGroups(
        IReadOnlyList<ScanFindingInput> kept, string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return kept;
        }

        List<List<int>>? groups;
        try
        {
            var doc = JsonDocument.Parse(
                response[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty(
                    "duplicates", out var arr))
            {
                return kept;
            }
            groups = JsonSerializer.Deserialize<List<List<int>>>(
                arr.GetRawText());
        }
        catch (JsonException)
        {
            return kept;
        }

        if (groups is null || groups.Count == 0)
        {
            return kept;
        }

        var drop = new HashSet<int>();
        foreach (var g in groups)
        {
            var valid = g.Where(i => i >= 0 && i < kept.Count)
                .Distinct().ToList();
            if (valid.Count < 2)
            {
                continue;
            }
            var winner = valid
                .OrderBy(i => ScanSeverities.Rank(kept[i].Severity))
                .ThenByDescending(i =>
                    kept[i].Description.Length)
                .First();
            foreach (var i in valid.Where(i => i != winner))
            {
                drop.Add(i);
            }
        }

        return drop.Count == 0
            ? kept
            : kept.Where((_, i) => !drop.Contains(i)).ToList();
    }

    /// <summary>Prompt da filha scan-worker (embedded template).</summary>
    internal static string BuildWorkerPrompt(
        ScanRecord scan, ScanPartition p)
    {
        var tpl = EmbeddedPrompts.Get("scan-worker");
        var dirs = p.Directories.Count == 0
            ? "(whole repository)"
            : string.Join('\n',
                p.Directories.Select(d => $"- {d}"));
        return tpl
            .Replace("{goal}", scan.Goal, StringComparison.Ordinal)
            .Replace("{profile}", scan.Profile, StringComparison.Ordinal)
            .Replace("{repository}", p.Repository, StringComparison.Ordinal)
            .Replace("{directories}", dirs, StringComparison.Ordinal)
            .Replace("{kind}", ScanFindingKinds.ForProfile(scan.Profile),
                StringComparison.Ordinal);
    }

    private async Task FailAsync(ScanRecord scan, string error,
        Dictionary<string, object?> stats, CancellationToken ct)
    {
        scan.Status = ScanStatuses.Failed;
        scan.Error = error.Length > 1900 ? error[..1900] : error;
        scan.FinishedAt = DateTimeOffset.UtcNow;
        scan.StatsJson = JsonSerializer.Serialize(stats);
        await db.SaveChangesAsync(ct);
        await AuditAsync("scan.failed", scan, stats, ct);
    }

    private async Task AuditAsync(string action, ScanRecord scan,
        object metadata, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action,
                actorUserId: null, orgId: scan.OrgId,
                targetType: "scan", targetId: scan.Id.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "scan audit {Action} skipped", action);
        }
    }
}
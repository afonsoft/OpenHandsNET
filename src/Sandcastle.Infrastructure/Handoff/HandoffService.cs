using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Identity;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Git;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Security;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Infrastructure.Handoff;

/// <summary>
/// SPEC-150 — handoff local↔cloud: criação da conversa a partir de um
/// patch de working tree (POST /conversations/handoff) e package
/// on-demand do diff do sandbox (GET …/handoff-package) com cache TTL
/// e snapshot para sandbox morto.
/// </summary>
public sealed partial class HandoffService(
    AppDbContext db,
    IAppConversationService conversations,
    ISandboxService sandboxes,
    ISandboxExecutor executor,
    IEventService events,
    IOptions<HandoffOptions> options,
    IOptions<GitOptions> gitOptions,
    ICurrentUserAccessor? currentUser = null,
    ILogger<HandoffService>? logger = null) : IHandoffService
{
    private const int MaxSummaryChars = 4000;
    private const int MaxUntrackedFiles = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc />
    public async Task<HandoffCreateResult> CreateAsync(
        HandoffRequest request, CancellationToken ct)
    {
        // ---- validação (400 no endpoint) --------------------------------
        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            return HandoffCreateResult.Fail(
                HandoffCreateError.RepositoryRequired,
                "repository is required");
        }

        if (string.IsNullOrWhiteSpace(request.BaseSha)
            || !ShaPattern().IsMatch(request.BaseSha.Trim()))
        {
            return HandoffCreateResult.Fail(
                HandoffCreateError.InvalidBaseSha,
                "base_sha must be a hex commit sha (7–64 chars)");
        }

        if (!HandoffDiffUtil.LooksLikeUnifiedDiff(request.DiffPatch))
        {
            return HandoffCreateResult.Fail(
                HandoffCreateError.InvalidPatch,
                "diff_patch must be a unified diff");
        }

        var maxBytes = options.Value.MaxDiffBytes;
        if (System.Text.Encoding.UTF8.GetByteCount(request.DiffPatch)
            > maxBytes)
        {
            return HandoffCreateResult.Fail(
                HandoffCreateError.PatchTooLarge,
                $"diff_patch exceeds Handoff:MaxDiffKb "
                + $"({options.Value.MaxDiffKb} KB)");
        }

        // SPEC-150 RF-003: patch é untrusted — sanitiza antes de persistir.
        var patch = SecretRedactor.Redact(request.DiffPatch);
        var meta = new HandoffMeta
        {
            Origin = "local",
            BaseSha = request.BaseSha.Trim(),
            BaseBranch = request.BaseBranch?.Trim(),
            CliVersion = ExtractCliVersion(request.Metadata),
            DiffPatch = patch,
            Metadata = request.Metadata,
        };

        var initial = new InitialMessage("user",
            [BuildPrompt(request, patch)], Run: true);
        var task = await conversations.CreateAsync(
            new CreateConversationRequest(
                initial, request.Repository.Trim(),
                AgentType: null, LlmModel: null, SandboxSpecId: null,
                HandoffMetaJson: JsonSerializer.Serialize(meta, Json)), ct);
        return HandoffCreateResult.Ok(task);
    }

    /// <summary>
    /// Prompt inicial: aviso de handoff (o diff já está no tree, não no
    /// prompt) + mensagem do usuário no envelope SPEC-91.
    /// </summary>
    internal static string BuildPrompt(HandoffRequest request, string patch)
    {
        var (files, adds, dels) = HandoffDiffUtil.Numstat(patch);
        var sb = new System.Text.StringBuilder();
        sb.Append("You are continuing work handed off from a local working "
            + "tree (`sandcastle handoff`).\n");
        sb.Append("Repository: ").Append(request.Repository.Trim()).Append('\n');
        sb.Append("Base: ").Append(request.BaseSha.Trim());
        if (request.BaseBranch is { Length: > 0 } b)
        {
            sb.Append(" (").Append(b.Trim()).Append(')');
        }

        sb.Append('\n');
        sb.Append("The user's uncommitted local changes were applied to the "
            + "workspace on top of that base (").Append(files)
            .Append(" files, +").Append(adds).Append(" -").Append(dels)
            .Append("). Inspect `git status`/`git diff` and any `*.rej` "
            + "files for hunks that failed to apply, then continue.\n");
        sb.Append(UntrustedInput.Wrap(
            string.IsNullOrWhiteSpace(request.Message)
                ? "Continue the task from the applied changes."
                : request.Message,
            "cli-handoff", "message"));
        return sb.ToString();
    }

    private static string? ExtractCliVersion(JsonElement? metadata)
    {
        if (metadata is { ValueKind: JsonValueKind.Object } m
            && m.TryGetProperty("cli_version", out var v)
            && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<HandoffPackageResult> GetPackageAsync(
        Guid conversationId, bool regenerate, CancellationToken ct)
    {
        var record = await db.Conversations
            .FindAsync([conversationId], ct);
        if (record is null)
        {
            return new HandoffPackageResult(
                null, HandoffPackageError.NotFound, false);
        }

        if (!await CanAccessAsync(record, ct))
        {
            return new HandoffPackageResult(
                null, HandoffPackageError.Forbidden, false);
        }

        var cached = Deserialize<HandoffPackage>(record.HandoffPackageJson);
        var sandbox = record.SandboxId is { } sid
            ? await sandboxes.GetAsync(sid, ct) : null;
        if (sandbox?.Status != SandboxStatus.Running)
        {
            // Sandbox morto → snapshot é tudo que resta (TTL não se aplica).
            return cached is not null
                ? new HandoffPackageResult(cached, null, true)
                : new HandoffPackageResult(
                    null, HandoffPackageError.NoLiveSandbox, false);
        }

        // Sandbox vivo: HEAD novo ou regenerate → regenera; senão cache.
        var repo = gitOptions.Value.RepoPath;
        var head = await HeadShaAsync(sandbox, repo, ct);
        var fresh = cached is not null
            && cached.HeadSha == head
            && DateTimeOffset.UtcNow - cached.GeneratedAt
                < TimeSpan.FromMinutes(
                    Math.Max(1, options.Value.PackageTtlMin));
        if (!regenerate && fresh)
        {
            return new HandoffPackageResult(cached, null, true);
        }

        var generated = await GenerateAsync(
            record, sandbox, repo, head, ct);
        if (generated.Error is not null)
        {
            return generated;
        }

        record.HandoffPackageJson =
            JsonSerializer.Serialize(generated.Package, Json);
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return generated;
    }

    /// <summary>
    /// Dono da conversa, papel de org com <c>view_org_conversations</c>
    /// ou superadmin; conversas sem dono (OSS single-user) ficam abertas
    /// a qualquer usuário autenticado.
    /// </summary>
    private async Task<bool> CanAccessAsync(
        ConversationRecord record, CancellationToken ct)
    {
        var user = currentUser?.User;
        if (user is null)
        {
            return record.CreatedByUserId is null && record.OrgId is null;
        }

        if (user.IsSuperAdmin)
        {
            return true;
        }

        if (record.CreatedByUserId is { } owner
            && string.Equals(owner, user.UserId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (record.OrgId is { } orgId)
        {
            var role = user.ActorKind == ActorKind.ServiceUser
                ? user.Role
                : await db.OrgMembers
                    .Where(m => m.OrgId == orgId && m.UserId == user.UserId)
                    .Select(m => m.Role)
                    .FirstOrDefaultAsync(ct);
            return RolePermissions.ForOrgRole(role)
                .Contains(Permission.ViewOrgConversations);
        }

        return record.CreatedByUserId is null;
    }

    /// <summary>Gera o package do worktree vivo (diff vs base).</summary>
    private async Task<HandoffPackageResult> GenerateAsync(
        ConversationRecord record, DomainSandbox sandbox,
        string repo, string? head, CancellationToken ct)
    {
        var meta = Deserialize<HandoffMeta>(record.HandoffMetaJson);
        var baseSha = meta?.BaseSha ?? await ResolveBaseAsync(
            sandbox, repo, ct);
        var diff = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' diff '{Escape(baseSha ?? "HEAD")}'",
            TimeSpan.FromMinutes(1), ct);
        if (diff.ExitCode != 0)
        {
            logger?.LogWarning("handoff-package: git diff falhou: {Err}",
                diff.StdErr.Trim());
            return new HandoffPackageResult(
                null, HandoffPackageError.NoLiveSandbox, false);
        }

        var patch = diff.StdOut
            + await UntrackedPatchAsync(sandbox, repo, ct);
        var package = new HandoffPackage
        {
            Repo = record.SelectedRepository ?? "",
            HeadBranch = await BranchAsync(sandbox, repo, ct),
            HeadSha = head,
            BaseSha = baseSha,
            DiffPatch = SecretRedactor.Redact(patch),
            Summary = await SummaryAsync(record, ct),
            BinaryFiles = HandoffDiffUtil.BinaryFilesOf(patch),
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        if (System.Text.Encoding.UTF8.GetByteCount(patch)
            > options.Value.MaxDiffBytes)
        {
            return new HandoffPackageResult(
                null, HandoffPackageError.PackageTooLarge, false);
        }

        return new HandoffPackageResult(package, null, false);
    }

    /// <summary>
    /// Untracked respeitando .gitignore + blocklist de secrets (dura) —
    /// sintetizados como new-file patch; binários viram placeholder.
    /// </summary>
    private async Task<string> UntrackedPatchAsync(
        DomainSandbox sandbox, string repo, CancellationToken ct)
    {
        var ls = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' ls-files --others --exclude-standard",
            TimeSpan.FromSeconds(30), ct);
        if (ls.ExitCode != 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var path in ls.StdOut.Split('\n',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Take(MaxUntrackedFiles))
        {
            if (HandoffDiffUtil.IsSecretPath(path))
            {
                continue; // blocklist dura — nunca no patch (SPEC-150 §9).
            }

            string content;
            try
            {
                content = await executor.ReadFileAsync(
                    sandbox, $"{repo}/{path}", ct);
            }
            catch (Exception)
            {
                continue;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(content)
                > HandoffDiffUtil.MaxUntrackedBytes)
            {
                content = "\0"; // grande demais → placeholder binário.
            }

            sb.Append(HandoffDiffUtil.NewFilePatch(path, content));
        }

        return sb.ToString();
    }

    /// <summary>Base do diff: handoff base_sha ou merge-base/root.</summary>
    private async Task<string?> ResolveBaseAsync(
        DomainSandbox sandbox, string repo, CancellationToken ct)
    {
        var mb = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' merge-base HEAD origin/HEAD 2>/dev/null || " +
            $"git -C '{repo}' rev-list --max-parents=0 HEAD | head -1",
            TimeSpan.FromSeconds(30), ct);
        var sha = mb.StdOut.Trim().Split('\n').LastOrDefault()?.Trim();
        return sha is { Length: >= 7 } ? sha : "HEAD";
    }

    private async Task<string?> HeadShaAsync(
        DomainSandbox sandbox, string repo, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' rev-parse HEAD",
            TimeSpan.FromSeconds(15), ct);
        return r.ExitCode == 0 ? r.StdOut.Trim() : null;
    }

    private async Task<string?> BranchAsync(
        DomainSandbox sandbox, string repo, CancellationToken ct)
    {
        var r = await executor.ExecAsync(sandbox,
            $"git -C '{repo}' rev-parse --abbrev-ref HEAD",
            TimeSpan.FromSeconds(15), ct);
        var name = r.StdOut.Trim();
        return r.ExitCode == 0 && name is not "" and not "HEAD" ? name : null;
    }

    /// <summary>Último work_summary do agente; fallback título.</summary>
    private async Task<string?> SummaryAsync(
        ConversationRecord record, CancellationToken ct)
    {
        try
        {
            var page = await events.SearchAsync(record.Id, new EventQuery(
                KindEq: "work_summary",
                Sort: EventSortOrder.TimestampDesc, Limit: 1), ct);
            var summary = page.Items.OfType<WorkSummaryEvent>()
                .Select(e => e.Summary).FirstOrDefault();
            return HandoffDiffUtil.Cap(
                summary ?? record.Title ?? "", MaxSummaryChars);
        }
        catch (Exception)
        {
            return record.Title;
        }
    }

    internal static T? Deserialize<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, Json);

    /// <summary>Serialização canonica dos payloads handoff_*.</summary>
    internal static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Json);

    private static string Escape(string s) => s.Replace("'", "'\\''");

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex ShaPattern();
}
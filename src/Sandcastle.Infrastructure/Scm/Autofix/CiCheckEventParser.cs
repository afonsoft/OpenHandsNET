using System.Text.Json;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Autofix;

/// <summary>
/// SPEC-141 RF-001 — normaliza os 4 event types de CI do GitHub
/// (<c>check_run</c>, <c>check_suite</c>, <c>workflow_run</c>,
/// <c>status</c>) para <see cref="CiCheckEvent"/>. Puro e testável —
/// o payload nunca é executado, só extraído.
/// </summary>
public static class CiCheckEventParser
{
    /// <summary>
    /// Conclusões consideradas falha — <c>error</c> entra pelo evento
    /// <c>status</c> (commit statuses API, que não tem conclusion).
    /// </summary>
    public static readonly IReadOnlySet<string> FailureConclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "failure", "timed_out", "action_required", "cancelled", "error" };

    /// <summary>
    /// Extrai o evento normalizado — null quando o payload não é do
    /// tipo/ação esperada ou faltam campos mínimos (repo, sha, name).
    /// </summary>
    public static CiCheckEvent? Parse(string eventType, JsonElement root)
    {
        var repository = RepositoryOf(root);
        if (repository is null)
        {
            return null;
        }
        return eventType switch
        {
            "check_run" => ParseCheckRun(root, repository),
            "check_suite" => ParseCheckSuite(root, repository),
            "workflow_run" => ParseWorkflowRun(root, repository),
            "status" => ParseStatus(root, repository),
            _ => null,
        };
    }

    /// <summary>Extrai "owner/repo" do payload.</summary>
    internal static string? RepositoryOf(JsonElement root) =>
        root.TryGetProperty("repository", out var repo)
            && repo.TryGetProperty("full_name", out var fn)
                ? fn.GetString() : null;

    private static CiCheckEvent? ParseCheckRun(
        JsonElement root, string repository)
    {
        if (!root.TryGetProperty("check_run", out var cr)
            || !ActionIsCompleted(root))
        {
            return null;
        }
        var name = Str(cr, "name");
        var headSha = Str(cr, "head_sha");
        if (name is null || headSha is null)
        {
            return null;
        }
        var (prNumber, branch, headRepo) = FirstPullRef(cr);
        return new CiCheckEvent(
            "check_run", repository, headSha, branch, prNumber,
            name, Str(cr, "conclusion") ?? "",
            Str(cr, "details_url") ?? Str(cr, "html_url"),
            cr.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
            null,
            NestedStr(cr, "app", "slug"),
            ActorLogin(root),
            headRepo);
    }

    private static CiCheckEvent? ParseCheckSuite(
        JsonElement root, string repository)
    {
        if (!root.TryGetProperty("check_suite", out var cs)
            || !ActionIsCompleted(root))
        {
            return null;
        }
        var headSha = Str(cs, "head_sha");
        if (headSha is null)
        {
            return null;
        }
        var (prNumber, branch, headRepo) = FirstPullRef(cs);
        var appSlug = NestedStr(cs, "app", "slug");
        branch ??= Str(cs, "head_branch");
        return new CiCheckEvent(
            "check_suite", repository, headSha, branch, prNumber,
            appSlug is null ? "check_suite" : $"check_suite:{appSlug}",
            Str(cs, "conclusion") ?? "",
            Str(cs, "url"),
            null,
            cs.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
            appSlug,
            ActorLogin(root),
            headRepo);
    }

    private static CiCheckEvent? ParseWorkflowRun(
        JsonElement root, string repository)
    {
        if (!root.TryGetProperty("workflow_run", out var wr)
            || !ActionIsCompleted(root))
        {
            return null;
        }
        var name = Str(wr, "name");
        var headSha = Str(wr, "head_sha");
        if (name is null || headSha is null)
        {
            return null;
        }
        var (prNumber, branch, headRepo) = FirstPullRef(wr);
        branch ??= Str(wr, "head_branch");
        return new CiCheckEvent(
            "workflow_run", repository, headSha, branch, prNumber,
            $"workflow:{name}",
            Str(wr, "conclusion") ?? "",
            Str(wr, "html_url"),
            null,
            wr.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
            NestedStr(wr, "app", "slug"),
            NestedStr(wr, "actor", "login") ?? ActorLogin(root),
            headRepo);
    }

    private static CiCheckEvent? ParseStatus(
        JsonElement root, string repository)
    {
        var state = Str(root, "state");
        var sha = Str(root, "sha");
        var context = Str(root, "context");
        if (state is null || sha is null || context is null)
        {
            return null;
        }
        string? branch = null;
        if (root.TryGetProperty("branches", out var bs)
            && bs.ValueKind == JsonValueKind.Array)
        {
            branch = bs.EnumerateArray()
                .Select(b => Str(b, "name"))
                .FirstOrDefault(n => n is not null);
        }
        return new CiCheckEvent(
            "status", repository, sha, branch, null,
            context, state,
            Str(root, "target_url") ?? Str(root, "description"),
            null, null, null,
            ActorLogin(root),
            null);
    }

    /// <summary>action == "completed" (ausente em <c>status</c> — nunca chamado lá).</summary>
    private static bool ActionIsCompleted(JsonElement root) =>
        root.TryGetProperty("action", out var a)
            && a.GetString() == "completed";

    /// <summary><c>sender.login</c> do envelope (quem gerou a entrega).</summary>
    private static string? ActorLogin(JsonElement root) =>
        NestedStr(root, "sender", "login");

    /// <summary>
    /// Primeiro <c>pull_requests[]</c> do objeto → (number, head.ref,
    /// head.repo.full_name).
    /// </summary>
    private static (int? Number, string? Branch, string? HeadRepo)
        FirstPullRef(JsonElement parent)
    {
        if (!parent.TryGetProperty("pull_requests", out var prs)
            || prs.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }
        foreach (var pr in prs.EnumerateArray())
        {
            int? number = pr.TryGetProperty("number", out var n)
                && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32() : null;
            string? branch = null, headRepo = null;
            if (pr.TryGetProperty("head", out var h))
            {
                branch = Str(h, "ref");
                headRepo = NestedStr(h, "repo", "full_name");
            }
            if (number is not null)
            {
                return (number, branch, headRepo);
            }
        }
        return (null, null, null);
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

    private static string? NestedStr(
        JsonElement el, string parent, string prop) =>
        el.TryGetProperty(parent, out var p)
            && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
}
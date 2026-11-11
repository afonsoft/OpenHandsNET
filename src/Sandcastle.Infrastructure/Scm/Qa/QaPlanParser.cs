using System.Text.Json;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Qa;

/// <summary>
/// SPEC-142 RF-002 — parse do plano de QA emitido pelo LLM: extrai o
/// JSON <c>{"classification":…,"start":…,"steps":[…]}</c> da resposta,
/// normaliza classificação/ações e saneia campos (cap de passos,
/// seletores/comandos não-vazios). Passos inválidos são descartados —
/// nunca inventa passos.
/// </summary>
public static class QaPlanParser
{
    /// <summary>Ações browser reconhecidas pelo driver.</summary>
    public static readonly IReadOnlySet<string> BrowserActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "goto", "click", "type", "scroll", "back", "forward",
          "wait", "screenshot" };

    /// <summary>Extrai e normaliza o plano; null = sem plano utilizável.</summary>
    public static QaPlan? Parse(string? llmText, int maxSteps)
    {
        if (string.IsNullOrWhiteSpace(llmText))
        {
            return null;
        }
        var start = llmText.IndexOf('{');
        var end = llmText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(llmText[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var classification = NormalizeClassification(
                Str(root, "classification") ?? Str(root, "kind"));
            var startPlan = ReadStart(root);
            var steps = ReadSteps(root, maxSteps);
            var repro = ReadReproduction(root);
            if (steps.Count == 0)
            {
                return null;
            }
            return new QaPlan(
                classification, Str(root, "summary"),
                startPlan, steps, repro);
        }
    }

    /// <summary><c>true</c> quando o passo é exercível pelo pipeline.</summary>
    public static bool IsValidStep(QaPlanStep s)
    {
        switch (s.Kind)
        {
            case "exec":
                return !string.IsNullOrWhiteSpace(s.Command);
            case "browser":
                if (s.Action is null || !BrowserActions.Contains(s.Action))
                {
                    return false;
                }
                return s.Action.ToLowerInvariant() switch
                {
                    "goto" => !string.IsNullOrWhiteSpace(s.Url),
                    "click" => !string.IsNullOrWhiteSpace(s.Selector),
                    "type" => !string.IsNullOrWhiteSpace(s.Selector)
                        && s.Text is not null,
                    _ => true,
                };
            default:
                return false;
        }
    }

    private static IReadOnlyList<QaPlanStep> ReadSteps(
        JsonElement root, int maxSteps)
    {
        var steps = new List<QaPlanStep>();
        if (!root.TryGetProperty("steps", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return steps;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (steps.Count >= maxSteps)
            {
                break;
            }
            var s = ReadStep(item);
            if (s is not null && IsValidStep(s))
            {
                steps.Add(s);
            }
        }
        return steps;
    }

    private static QaPlanStep? ReadStep(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var kind = Str(e, "kind")?.ToLowerInvariant()
            ?? (Str(e, "command") is not null ? "exec"
                : Str(e, "action") is not null ? "browser" : null);
        if (kind is null)
        {
            return null;
        }
        int? timeout = e.TryGetProperty("timeout_seconds", out var ts)
            && ts.ValueKind == JsonValueKind.Number
            && ts.TryGetInt32(out var n)
                ? Math.Clamp(n, 1, 600) : null;
        return new QaPlanStep(
            kind is "exec" or "browser" ? kind : "exec",
            Str(e, "name"), Str(e, "action")?.ToLowerInvariant(),
            Str(e, "command"), Str(e, "url"), Str(e, "selector"),
            Str(e, "text"), Str(e, "expect"), timeout);
    }

    private static QaPlanStart? ReadStart(JsonElement root)
    {
        if (!root.TryGetProperty("start", out var s)
            || s.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var command = Str(s, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        var port = s.TryGetProperty("port", out var p)
            && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var n)
                ? Math.Clamp(n, 1, 65535) : 8080;
        return new QaPlanStart(
            command.Trim(), port, Str(s, "path") ?? "/");
    }

    private static QaPlanReproduction? ReadReproduction(JsonElement root)
    {
        if (!root.TryGetProperty("reproduction", out var r)
            || r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var command = Str(r, "command");
        return string.IsNullOrWhiteSpace(command)
            ? null
            : new QaPlanReproduction(command.Trim(), Str(r, "indicator"));
    }

    internal static string NormalizeClassification(string? c) =>
        c?.Trim().ToLowerInvariant() switch
        {
            "feature" or "feat" or "enhancement" => QaClassifications.Feature,
            "bugfix" or "bug" or "fix" or "hotfix" =>
                QaClassifications.Bugfix,
            "refactor" or "refactoring" or "cleanup" =>
                QaClassifications.Refactor,
            "config" or "chore" or "docs" or "ci" or "infra" =>
                QaClassifications.Config,
            _ => QaClassifications.Unknown,
        };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
}
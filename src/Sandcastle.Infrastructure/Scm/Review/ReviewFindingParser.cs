using System.Text.Json;

using Sandcastle.Application.Scm;

namespace Sandcastle.Infrastructure.Scm.Review;

/// <summary>Finding estruturado emitido pelo LLM (pré-validação).</summary>
public sealed record ParsedFinding(
    string Path,
    int? Line,
    string Severity,
    string Kind,
    string? Title,
    string Body,
    double? Confidence,
    string? SuggestedPatch);

/// <summary>
/// SPEC-140 RF-002 — parse + validação dos findings do LLM: extrai o
/// JSON <c>{"findings":[…]}</c> da resposta, normaliza severity/kind e
/// descarta findings cujo <c>path</c>/<c>line</c> não exista no diff
/// (o modelo só vê o diff — âncoras inválidas são alucinação).
/// </summary>
public static class ReviewFindingParser
{
    /// <summary>Extrai e valida findings; retorna (válidos, descartados).</summary>
    public static (IReadOnlyList<ParsedFinding> Valid, int Dropped) Parse(
        string llmText, DiffIndex diff)
    {
        var valid = new List<ParsedFinding>();
        var dropped = 0;
        foreach (var f in Extract(llmText))
        {
            if (Keep(f, diff))
            {
                valid.Add(f);
            }
            else
            {
                dropped++;
            }
        }
        return (valid, dropped);
    }

    /// <summary>
    /// Fatia o primeiro objeto <c>{…}</c> balanceado da resposta (o modelo
    /// pode emitir prosa/fences ao redor) e itera <c>findings[]</c>.
    /// </summary>
    internal static IEnumerable<ParsedFinding> Extract(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("findings", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in arr.EnumerateArray())
            {
                var parsed = Read(item);
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
        }
    }

    private static ParsedFinding? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var path = Str(e, "path") ?? Str(e, "file");
        var body = Str(e, "body") ?? Str(e, "comment")
            ?? Str(e, "description");
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        int? line = null;
        if (e.TryGetProperty("line", out var l))
        {
            line = l.ValueKind switch
            {
                JsonValueKind.Number when l.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(
                    l.GetString(), out var n) => n,
                _ => null,
            };
        }

        double? confidence = null;
        if (e.TryGetProperty("confidence", out var c)
            && c.ValueKind == JsonValueKind.Number
            && c.TryGetDouble(out var d))
        {
            confidence = Math.Clamp(d, 0, 1);
        }

        return new ParsedFinding(
            path.Trim(),
            line,
            NormalizeSeverity(Str(e, "severity")),
            NormalizeKind(Str(e, "kind") ?? Str(e, "category")),
            Str(e, "title"),
            body.Trim(),
            confidence,
            Str(e, "suggested_patch") ?? Str(e, "suggestion"));
    }

    /// <summary>
    /// Regra de descarte (SPEC-140 RF-002): path fora do diff dropa;
    /// line presente mas fora do lado novo do patch também dropa;
    /// line ausente mantém como finding file-level (não vai inline).
    /// </summary>
    private static bool Keep(ParsedFinding f, DiffIndex diff)
    {
        if (!diff.Paths.Contains(f.Path))
        {
            return false;
        }
        if (f.Line is { } line
            && !diff.IsAnchorable(f.Path, line))
        {
            return false;
        }
        return true;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

    private static string NormalizeSeverity(string? s) =>
        s?.Trim().ToLowerInvariant() switch
        {
            "critical" or "blocker" or "high" or "major" =>
                ReviewSeverities.Critical,
            "important" or "medium" or "moderate" =>
                ReviewSeverities.Important,
            "nit" or "nitpick" or "trivial" => ReviewSeverities.Nit,
            _ => ReviewSeverities.Suggestion,
        };

    private static string NormalizeKind(string? k) =>
        k?.Trim().ToLowerInvariant() switch
        {
            "bug" or "defect" or "correctness" => ReviewFindingKinds.Bug,
            "security" or "vuln" or "vulnerability" =>
                ReviewFindingKinds.Security,
            "perf" or "performance" => ReviewFindingKinds.Perf,
            "style" or "readability" or "naming" =>
                ReviewFindingKinds.Style,
            "note" or "info" or "fyi" => ReviewFindingKinds.Note,
            _ => ReviewFindingKinds.Investigate,
        };
}
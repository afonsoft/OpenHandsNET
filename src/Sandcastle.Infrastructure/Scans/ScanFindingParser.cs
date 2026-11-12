using System.Text.Json;

using Sandcastle.Application.Scans;
using Sandcastle.Infrastructure.Security;

namespace Sandcastle.Infrastructure.Scans;

/// <summary>
/// SPEC-143 RF-002: parses a scan child's final assistant output into
/// <see cref="ScanFindingInput"/> rows. Contract: the last
/// <c>```scan_findings</c> fenced block (or trailing raw JSON) must be an
/// object <c>{"schema":"scan_finding_v1","findings":[…]}</c>. Lenient:
/// unknown fields ignored, invalid rows skipped, severity/kind
/// normalized, text trimmed. Untrusted content is treated as data.
/// </summary>
public static class ScanFindingParser
{
    private const string Fence = "```scan_findings";

    /// <summary>Schema token validated in the payload.</summary>
    public const string Schema = "scan_finding_v1";

    private static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Extracts findings from <paramref name="output"/> — the child's final
    /// assistant text. Returns the parsed rows (may be empty) and a
    /// diagnostic string when the payload was missing/invalid.
    /// </summary>
    public static (IReadOnlyList<ScanFindingInput> Findings, string? Error)
        Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ([], "empty_output");
        }

        var payload = ExtractPayload(output);
        if (payload is null)
        {
            return ([], "no_scan_findings_block");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return ([], "invalid_json");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("findings", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return ([], "missing_findings_array");
            }

            if (root.TryGetProperty("schema", out var s)
                && s.ValueKind == JsonValueKind.String
                && !string.Equals(
                    s.GetString(), Schema, StringComparison.Ordinal))
            {
                return ([], "unexpected_schema");
            }

            var list = new List<ScanFindingInput>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                var f = TryParse(el);
                if (f is not null)
                {
                    list.Add(f);
                }
            }

            return (list, null);
        }
    }

    /// <summary>Builds the canonical payload the parser accepts.</summary>
    public static string Serialize(
        IEnumerable<ScanFindingInput> findings) =>
        JsonSerializer.Serialize(new
        {
            schema = Schema,
            findings = findings.Select(f => new
            {
                path = f.Path,
                start_line = f.StartLine,
                end_line = f.EndLine,
                kind = f.Kind,
                severity = f.Severity,
                confidence = f.Confidence,
                title = f.Title,
                description = f.Description,
                remediation_hint = f.RemediationHint,
                payoff = f.Payoff,
            }).ToList(),
        }, Json);

    private static string? ExtractPayload(string output)
    {
        // Last fenced block wins (children may echo examples earlier).
        var fenceIdx = output.LastIndexOf(Fence, StringComparison.Ordinal);
        if (fenceIdx >= 0)
        {
            var start = output.IndexOf('\n', fenceIdx);
            if (start < 0)
            {
                return null;
            }

            var end = output.IndexOf(
                "```", start + 1, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            return output.Substring(start + 1, end - start - 1).Trim();
        }

        // Fallback: trailing raw JSON object containing "scan_finding_v1".
        // LastIndexOf('{') puro acha o objeto mais interno (a finding),
        // não o envelope — anda para trás até um sufixo que (a) cite o
        // schema e (b) parseie como JSON completo.
        for (var brace = output.LastIndexOf('{');
            brace >= 0;
            brace = output.LastIndexOf('{', brace - 1))
        {
            var candidate = output[brace..];
            if (!candidate.Contains(Schema, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var probe = JsonDocument.Parse(candidate);
                return candidate.Trim();
            }
            catch (JsonException)
            {
                // sufixo inválido (prosa/truncado) — tenta o '{' anterior
            }
        }

        return null;
    }

    private static ScanFindingInput? TryParse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = GetStr(el, "path");
        var desc = GetStr(el, "description") ?? GetStr(el, "desc");
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(desc))
        {
            return null;
        }

        var kind = ScanFindingKinds.Normalize(GetStr(el, "kind"))
            ?? ScanFindingKinds.Custom;
        var severity = ScanSeverities.NormalizeOrMedium(
            GetStr(el, "severity"));

        return new ScanFindingInput(
            Repository: string.Empty, // filled by the orchestrator
            Path: Clean(path, 1000)!,
            StartLine: GetInt(el, "start_line") ?? GetInt(el, "line"),
            EndLine: GetInt(el, "end_line"),
            Kind: kind,
            Severity: severity,
            Confidence: GetDbl(el, "confidence"),
            Title: TrimOrNull(GetStr(el, "title"), 300),
            Description: Clean(desc, 4000)!,
            RemediationHint: TrimOrNull(
                GetStr(el, "remediation_hint")
                ?? GetStr(el, "remediation"), 2000),
            Payoff: ScanPayoffs.Normalize(GetStr(el, "payoff")));
    }

    /// <summary>Strips control chars (prompt-injection hygiene) + caps.</summary>
    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var chars = s.Trim().Where(c => !char.IsControl(c) || c == '\n')
            .ToArray();
        return UntrustedInput.Cap(new string(chars), max);
    }

    private static string? TrimOrNull(string? s, int max) =>
        Clean(s, max);

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind
            == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i)
            ? i
            : null;

    private static double? GetDbl(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d)
            ? d
            : null;
}
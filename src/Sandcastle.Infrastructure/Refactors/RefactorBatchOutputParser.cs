using System.Text.Json;

using Sandcastle.Application.Refactors;

namespace Sandcastle.Infrastructure.Refactors;

/// <summary>
/// SPEC-144 RF-002: parses the batch child's trailing fenced block —
/// <c>```refactor_batch\n{"schema":"refactor_batch_v1",…}```</c> — from
/// its final agent message (same convention as
/// <c>scan_finding_v1</c>). Tolerant to prose before/inside the fence
/// and to a bare JSON object when the fence is missing.
/// </summary>
public static class RefactorBatchOutputParser
{
    /// <summary>
    /// Extracts the first valid <c>refactor_batch_v1</c> payload.
    /// Returns (output, null) on success, (null, error) otherwise.
    /// </summary>
    public static (RefactorBatchOutput? Output, string? Error) Parse(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "empty output");
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (null, "no json object found");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException e)
        {
            return (null, $"invalid json: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var s)
                || s.GetString() != "refactor_batch_v1")
            {
                return (null, "missing schema refactor_batch_v1");
            }

            var passed = root.TryGetProperty("verify_passed", out var vp)
                && vp.ValueKind == JsonValueKind.True;
            int? attempts = null;
            if (root.TryGetProperty("verify_attempts", out var va)
                && va.ValueKind == JsonValueKind.Number
                && va.TryGetInt32(out var n))
            {
                attempts = n;
            }

            string? tail = null;
            if (root.TryGetProperty("log_tail", out var lt)
                && lt.ValueKind == JsonValueKind.String)
            {
                tail = lt.GetString();
                if (tail is { Length: > 4000 })
                {
                    tail = tail[^4000..];
                }
            }

            string? summary = null;
            if (root.TryGetProperty("summary", out var sm)
                && sm.ValueKind == JsonValueKind.String)
            {
                summary = sm.GetString();
            }

            return (new RefactorBatchOutput(
                passed, attempts, tail, summary), null);
        }
    }
}
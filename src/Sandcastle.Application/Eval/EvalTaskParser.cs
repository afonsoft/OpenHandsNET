using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandcastle.Application.Eval;

/// <summary>
/// SPEC-152 RF-001: strict parser for the <c>eval_task_v1</c> task
/// contract (one JSON object per line — jsonl). Collects per-line
/// errors instead of failing the whole file so adapters can report
/// dataset quality; a task only loads when every rule passes:
/// <c>task_id</c> non-empty, <c>repo</c> XOR <c>image</c>, non-empty
/// <c>prompt</c>, checker <c>cmd</c> XOR <c>path</c>,
/// <c>timeout_s</c> &gt; 0 when present, <c>metadata</c> object.
/// </summary>
public static class EvalTaskParser
{
    /// <summary>Wire name embedded in every error detail.</summary>
    public const string ContractName = "eval_task_v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = false,
    };

    /// <summary>Serializes a task back to its canonical wire form.</summary>
    public static string ToJson(EvalTaskV1 task) =>
        JsonSerializer.Serialize(task, Json);

    /// <summary>
    /// Parses one jsonl line. Returns the task or appends a
    /// <c>line N: reason</c> entry to <paramref name="errors"/>.
    /// </summary>
    public static EvalTaskV1? ParseLine(
        string line, int lineNumber, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null; // blank lines são toleradas (fim de arquivo).
        }

        EvalTaskV1? task;
        try
        {
            task = JsonSerializer.Deserialize<EvalTaskV1>(line, Json);
        }
        catch (JsonException ex)
        {
            errors.Add($"line {lineNumber}: invalid {ContractName} JSON"
                + $" — {ex.Message}");
            return null;
        }

        if (task is null)
        {
            errors.Add($"line {lineNumber}: null {ContractName}");
            return null;
        }

        var why = Validate(task);
        if (why is not null)
        {
            errors.Add($"line {lineNumber}: {why}");
            return null;
        }

        return task;
    }

    /// <summary>
    /// Parses a whole jsonl payload — valid tasks in order,
    /// collected errors in <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyList<EvalTaskV1> ParseJsonl(
        string content, List<string> errors)
    {
        var tasks = new List<EvalTaskV1>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var task = ParseLine(raw, lineNo, errors);
            if (task is null)
            {
                continue;
            }

            if (!seen.Add(task.TaskId))
            {
                errors.Add($"line {lineNo}: duplicate task_id"
                    + $" '{task.TaskId}'");
                continue;
            }

            tasks.Add(task);
        }

        return tasks;
    }

    /// <summary>Semantic validation — returns the rejection reason.</summary>
    public static string? Validate(EvalTaskV1 task)
    {
        if (string.IsNullOrWhiteSpace(task.TaskId))
        {
            return "task_id is required";
        }

        if (task.TaskId.Length > 200)
        {
            return "task_id exceeds 200 chars";
        }

        var hasRepo = !string.IsNullOrWhiteSpace(task.Repo);
        var hasImage = !string.IsNullOrWhiteSpace(task.Image);
        if (hasRepo == hasImage)
        {
            return "exactly one of repo|image is required";
        }

        if (string.IsNullOrWhiteSpace(task.Prompt))
        {
            return "prompt is required";
        }

        var hasCmd = !string.IsNullOrWhiteSpace(task.Checker?.Cmd);
        var hasPath = !string.IsNullOrWhiteSpace(task.Checker?.Path);
        if (task.Checker is null || hasCmd == hasPath)
        {
            return "checker must define exactly one of cmd|path";
        }

        if (task.TimeoutS is <= 0)
        {
            return "timeout_s must be > 0";
        }

        return null;
    }
}
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Eval;

/// <summary>Resultado por assert (<c>result.json</c> → asserts[]).</summary>
public sealed record AssertResult(
    string Type,
    bool Pass,
    bool Skipped,
    double Weight,
    string? Detail);

/// <summary>
/// SPEC-39 RF-002: <c>result.json</c> por task —
/// <c>{score, events_summary, tokens, duration, artifact_refs}</c>.
/// </summary>
public sealed record EvalResult
{
    public required string Name { get; init; }
    public string? ConversationId { get; init; }
    public string ExecutionStatus { get; init; } = "";
    public double Score { get; init; }
    public bool Pass { get; init; }
    public IReadOnlyList<AssertResult> Asserts { get; init; } = [];
    public IReadOnlyDictionary<string, int> EventsSummary { get; init; } =
        new Dictionary<string, int>();
    public long Tokens { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<string> ArtifactRefs { get; init; } = [];
    public string Mode { get; init; } = "live";
    public string? Error { get; init; }

    /// <summary>Serialização estável (snake_case) para artifacts.</summary>
    public static string ToJson(EvalResult r) =>
        JsonSerializer.Serialize(r, JsonOptions);

    /// <summary>Desserializa um <c>result.json</c> salvo.</summary>
    public static EvalResult? FromJson(string json) =>
        JsonSerializer.Deserialize<EvalResult>(json, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

/// <summary>Resultado agregado da suite (média ponderada).</summary>
public sealed record SuiteResult(
    string Suite,
    double Score,
    int Total,
    int Passed,
    IReadOnlyList<EvalResult> Tasks);
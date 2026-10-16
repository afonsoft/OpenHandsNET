using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHandsNET.Eval;

/// <summary>
/// SPEC-39 RF-005: recordings versionáveis por hash do conteúdo da task
/// (modo <c>recorded</c> = replay determinístico das evidências, sem LLM).
/// Nunca gravar respostas com secrets — só evidências de asserts.
/// </summary>
public sealed class RecordingStore
{
    private readonly string _dir;

    /// <summary>Cria a store em <paramref name="dir"/> (ex.: <c>evals/recordings</c>).</summary>
    public RecordingStore(string dir) => _dir = dir;

    /// <summary>Hash estável da task (nome + instrução + asserts).</summary>
    public static string HashOf(EvalTask t)
    {
        var raw = JsonSerializer.Serialize(new
        {
            t.Name, t.Task, t.Repo, t.Setup,
            Assert = t.Assert.Select(a => new
            {
                a.Type, a.Cmd, a.Expect, a.Path, a.Pattern,
            }),
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>Caminho do recording da task.</summary>
    public string PathFor(EvalTask t) =>
        Path.Combine(_dir, $"{t.Name}-{HashOf(t)}.json");

    /// <summary>Carrega evidências gravadas; <see langword="null"/> se ausente.</summary>
    public Recording? Load(EvalTask t)
    {
        var path = PathFor(t);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Recording>(
            File.ReadAllText(path), JsonOptions);
    }

    /// <summary>Grava evidências de um run live para replay futuro.</summary>
    public async Task SaveAsync(
        EvalTask t, string conversationId, EvalEvidence evidence,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var rec = new Recording(conversationId, evidence);
        await File.WriteAllTextAsync(PathFor(t),
            JsonSerializer.Serialize(rec, JsonOptions), ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

/// <summary>Recording: id da conversa de origem + evidências congeladas.</summary>
public sealed record Recording(string? ConversationId, EvalEvidence Evidence);

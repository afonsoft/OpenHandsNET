using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-25 RF-005: central tool-result policy — per-tool output caps
/// (<c>AgentLlm:ToolOutputCaps</c>), head+tail truncation with marker, and
/// <c>artifact_ref</c>: truncated payloads land at
/// <c>/tmp/ohn-artifacts/&lt;action-id&gt;.txt</c> in the workspace so the
/// agent can <c>file_read</c> the rest and compaction can prune losslessly.
/// </summary>
public sealed class OutputPolicy(IOptions<AgentLlmOptions> options)
{
    private const int DefaultCap = 60 * 1024;
    private const string Marker = "…truncated {0} chars…";

    /// <summary>Cap for a tool (per-name override, then default).</summary>
    public int CapFor(string toolName)
    {
        var caps = options.Value.ToolOutputCaps;
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal)
            && caps.TryGetValue("mcp__*", out var mcp))
        {
            return mcp;
        }

        return caps.TryGetValue(toolName, out var cap) ? cap : DefaultCap;
    }

    /// <summary>
    /// Applies the policy to a string payload: returns (text, truncated).
    /// Head keeps 70% of the cap, tail 20%, marker notes elided chars.
    /// </summary>
    public static (string Text, bool Truncated) Truncate(
        string text, int cap)
    {
        if (text.Length <= cap)
        {
            return (text, false);
        }

        var head = (int)(cap * 0.7);
        var tail = (int)(cap * 0.2);
        var marker = string.Format(Marker, text.Length - head - tail);
        return (text[..head] + "\n" + marker + "\n"
            + text[^tail..], true);
    }

    /// <summary>
    /// Post-processes a tool observation: truncates oversized string fields
    /// (top-level) and, when anything was cut, persists the full payload via
    /// the executor and adds <c>artifact_ref</c>/<c>truncated</c> fields.
    /// </summary>
    public async Task<JsonElement> ApplyAsync(
        string toolName, string actionId, JsonElement observation,
        Domain.Sandbox.Sandbox? sandbox, ISandboxExecutor? executor,
        CancellationToken ct)
    {
        if (observation.ValueKind != JsonValueKind.Object)
        {
            return observation;
        }

        var cap = CapFor(toolName);
        var mutated = new Dictionary<string, JsonElement>();
        var truncated = false;
        foreach (var prop in observation.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String
                && prop.Value.GetString() is { } s
                && s.Length > cap)
            {
                var (t, tr) = Truncate(s, cap);
                mutated[prop.Name] = JsonSerializer.SerializeToElement(t);
                truncated |= tr;
            }
            else
            {
                mutated[prop.Name] = prop.Value;
            }
        }

        if (!truncated)
        {
            return observation;
        }

        // artifact_ref — payload completo no workspace (best-effort).
        if (sandbox is not null && executor is not null)
        {
            try
            {
                var artifact = $"/tmp/ohn-artifacts/{actionId}.txt";
                var full = observation.GetRawText();
                await executor.WriteFileAsync(
                    sandbox, artifact, full, ct);
                mutated["artifact_ref"] =
                    JsonSerializer.SerializeToElement(artifact);
            }
            catch (Exception)
            {
                // sem executor/fs — segue só com o truncamento.
            }
        }

        mutated["truncated"] = JsonSerializer.SerializeToElement(true);
        return JsonSerializer.SerializeToElement(mutated);
    }
}

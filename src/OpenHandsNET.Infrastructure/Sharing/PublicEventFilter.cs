using System.Text.Json;

using OpenHandsNET.Application.Sharing;
using OpenHandsNET.Infrastructure.Agent;

namespace OpenHandsNET.Infrastructure.Sharing;

/// <summary>
/// SPEC-56 RF-003 — public event filter. Two defences:
/// <list type="number">
///   <item>denylist of internal/ephemeral event kinds;</item>
///   <item>recursive payload sanitization — properties whose names denote
///   credentials/internal addresses are dropped, and every string value
///   goes through <see cref="SecretRedactor"/> (placeholders + resolved
///   secret values).</item>
/// </list>
/// Redaction is defence-in-depth: applied even on fields already redacted
/// at write time.
/// </summary>
public sealed class PublicEventFilter : IPublicEventFilter
{
    /// <summary>
    /// Kinds that never leave the private API: ephemeral streaming
    /// chunks, token/diagnostic internals, prompt internals and control
    /// events.
    /// </summary>
    private static readonly HashSet<string> DeniedKinds = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "StreamingDeltaEvent",
        "TokenEvent",
        "LLMCompletionLogEvent",
        "agent_diagnostic",
        "SystemPromptEvent",
        "CondensationRequest",
        "ACPToolCallEvent",
        "HookExecutionEvent",
    };

    /// <summary>
    /// Property names that are dropped recursively from public payloads —
    /// credentials and internal sandbox addresses (exact match,
    /// case-insensitive).
    /// </summary>
    private static readonly HashSet<string> DeniedProperties = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "session_api_key", "access_token", "refresh_token",
        "id_token", "token", "secret", "secrets", "password",
        "authorization", "cookie", "set-cookie", "client_secret",
        "private_key", "webhook_secret", "signing_secret",
        "agent_server_url", "server_url", "preview_url", "preview_token",
        "vscode_url", "connection_url",
    };

    /// <summary>
    /// Suffixes also treated as sensitive (covers
    /// <c>github_token</c>, <c>db_password</c>, …) while keeping metric
    /// fields like <c>prompt_tokens</c>/<c>completion_tokens</c>.
    /// </summary>
    private static readonly string[] DeniedSuffixes =
    [
        "_api_key", "_token", "_secret", "_password", "_cookie",
    ];

    /// <inheritdoc />
    public JsonElement? Filter(
        string kind, string rawPayload, IReadOnlyList<string>? secretValues)
    {
        if (DeniedKinds.Contains(kind))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(rawPayload);
        var sanitized = Sanitize(doc.RootElement, secretValues);
        return JsonSerializer.SerializeToElement(sanitized);
    }

    private static object? Sanitize(
        JsonElement el, IReadOnlyList<string>? secretValues)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Dictionary<string, object?>();
                foreach (var prop in el.EnumerateObject())
                {
                    if (IsDeniedProperty(prop.Name))
                    {
                        continue;
                    }

                    obj[prop.Name] = Sanitize(prop.Value, secretValues);
                }

                return obj;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(Sanitize(item, secretValues));
                }

                return list;

            case JsonValueKind.String:
                return SecretRedactor.Redact(el.GetString(), secretValues);

            case JsonValueKind.Number:
                return el.TryGetInt64(out var l)
                    ? l
                    : el.GetDouble();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return el.GetBoolean();
            default:
                return null;
        }
    }

    private static bool IsDeniedProperty(string name)
    {
        if (DeniedProperties.Contains(name))
        {
            return true;
        }

        return DeniedSuffixes.Any(suffix =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
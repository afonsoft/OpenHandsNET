using System.Text.RegularExpressions;

namespace OpenHandsNET.Infrastructure.Agent;

/// <summary>
/// Masks secrets before they reach the LLM prompt or any log/event payload
/// (SPEC-09 invariant): <c>{{secret:NAME}}</c> placeholders and known secret
/// values are replaced with <c>***</c>.
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>Redacts placeholders and each known secret value.</summary>
    public static string Redact(string? text, IEnumerable<string>? secretValues = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = PlaceholderPattern().Replace(text, "***");
        if (secretValues is not null)
        {
            foreach (var value in secretValues)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    result = result.Replace(value, "***", StringComparison.Ordinal);
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"\{\{secret:[^}]+\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();
}

using System.Security.Cryptography;
using System.Text;

using Sandcastle.Application.Alerts;

namespace Sandcastle.Infrastructure.Alerts;

/// <summary>
/// SPEC-151 RF-001/NFR — verificação de assinatura por provider.
/// Fail-closed por contrato: qualquer ausência/malformação devolve
/// <see langword="false"/> e o caller responde 401 sem processar o
/// payload. Comparações sempre constant-time.
/// </summary>
public static class AlertSignatureVerifiers
{
    /// <summary>Header da assinatura Sentry (hex HMAC-SHA256 do body).</summary>
    public const string SentrySignatureHeader = "sentry-hook-signature";

    /// <summary>Header da assinatura PagerDuty (v1, lista separada por vírgula).</summary>
    public const string PagerDutySignatureHeader = "X-PagerDuty-Signature";

    /// <summary>Header de segredo compartilhado do provider generic.</summary>
    public const string GenericSecretHeader = "X-Alert-Secret";

    /// <summary>Alias aceito do segredo compartilhado.</summary>
    public const string GenericSecretHeaderAlt = "X-Sandcastle-Alert-Secret";

    /// <summary>
    /// Despacha pelo provider: Sentry HMAC hex, PagerDuty cadeia v1,
    /// generic shared-secret. <paramref name="secret"/> é o valor do
    /// <c>secret_ref</c> resolvido do <c>ISecretStore</c>.
    /// </summary>
    public static bool Verify(
        AlertProvider provider, string? secret,
        IReadOnlyDictionary<string, string?> headers, byte[] payload)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        return provider switch
        {
            AlertProvider.Sentry => VerifySentry(secret,
                Header(headers, SentrySignatureHeader), payload),
            AlertProvider.PagerDuty => VerifyPagerDuty(secret,
                Header(headers, PagerDutySignatureHeader), payload),
            AlertProvider.Generic => VerifyGeneric(secret,
                Header(headers, GenericSecretHeader)
                    ?? Header(headers, GenericSecretHeaderAlt)),
            _ => false,
        };
    }

    /// <summary>
    /// Sentry: <c>sentry-hook-signature</c> = hex(HMAC-SHA256(secret, body)).
    /// Aceita o hex em lower ou upper-case (docs divergem).
    /// </summary>
    public static bool VerifySentry(
        string secret, string? signature, byte[] payload)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
        var presented = signature.Trim();
        return presented.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(presented.ToLowerInvariant()));
    }

    /// <summary>
    /// PagerDuty v1: <c>X-PagerDuty-Signature</c> =
    /// <c>"v1=hex[,v1=hex…]"</c> — cadeia p/ rotação de segredos;
    /// qualquer entrada que case o segredo corrente autentica.
    /// </summary>
    public static bool VerifyPagerDuty(
        string secret, string? signature, byte[] payload)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var valid = false;
        foreach (var part in signature.Split(',',
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("v1=", StringComparison.Ordinal)
                || part.Length - 3 != expected.Length)
            {
                continue; // entrada malformada — não curto-circuita
            }

            var presented = Encoding.ASCII.GetBytes(
                part[3..].ToLowerInvariant());
            // FixedTimeEquals mantém o tempo constante por entrada.
            valid |= CryptographicOperations.FixedTimeEquals(
                expectedBytes, presented);
        }

        return valid;
    }

    /// <summary>
    /// Generic: segredo compartilhado no header
    /// (<see cref="GenericSecretHeader"/> /
    /// <see cref="GenericSecretHeaderAlt"/>), compare fixo.
    /// </summary>
    public static bool VerifyGeneric(string secret, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(secret);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length
            && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string? Header(
        IReadOnlyDictionary<string, string?> headers, string name)
    {
        foreach (var (k, v) in headers)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
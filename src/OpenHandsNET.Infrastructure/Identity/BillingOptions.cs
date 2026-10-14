namespace OpenHandsNET.Infrastructure.Identity;

/// <summary>Billing/enforcement options (<c>Billing:*</c>) — SPEC-20.</summary>
public sealed class BillingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Billing";

    /// <summary>
    /// Master switch for plan enforcement. Default <c>false</c> — OSS
    /// self-host is never blocked; operators enable it explicitly.
    /// </summary>
    public bool EnforcementEnabled { get; set; }
}

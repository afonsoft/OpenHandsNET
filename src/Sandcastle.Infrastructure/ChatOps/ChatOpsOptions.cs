namespace Sandcastle.Infrastructure.ChatOps;

/// <summary><c>ChatOps</c> config section (SPEC-54 / SPEC-147).</summary>
public sealed class ChatOpsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "ChatOps";

    /// <summary>
    /// SPEC-147 RF-001: quando <c>true</c>, pings de grupo
    /// (<c>@channel</c>/<c>@here</c>/<c>@everyone</c>/<c>&lt;!…&gt;</c>)
    /// passam sem neutralização. Default <c>false</c> — o sanitizer roda
    /// sempre, a menos que a org opte in via settings
    /// (<c>chatops_allow_group_mentions</c>) ou a instância habilite
    /// globalmente aqui.
    /// </summary>
    public bool AllowGroupMentions { get; set; }
}
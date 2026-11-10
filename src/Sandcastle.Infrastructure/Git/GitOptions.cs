namespace Sandcastle.Infrastructure.Git;

/// <summary><c>Git</c> config section (SPEC-12).</summary>
public sealed class GitOptions
{
    /// <summary><c>container</c> (clone inside the sandbox) or <c>host</c> (pre-clone into the mounted workspace — dev/air-gapped).</summary>
    public string CloneMode { get; set; } = "container";

    /// <summary>Work branch template; <c>{id}</c> expands to the short conversation id.</summary>
    public string WorkBranch { get; set; } = "openhands/{id}";

    /// <summary>Repo directory inside the sandbox workspace.</summary>
    public string RepoPath { get; set; } = "/workspace/repo";

    /// <summary>Agent commit author name.</summary>
    public string AuthorName { get; set; } = "sandcastle-agent";

    /// <summary>Agent commit author email.</summary>
    public string AuthorEmail { get; set; } = "agent@sandcastle.local";

    /// <summary>
    /// SPEC-147 RF-002: <c>Git:CoAuthorUser</c> — quando true (default),
    /// commits do agente carregam trailer
    /// <c>Co-authored-by: {user} &lt;{email}&gt;</c> do dono da conversa
    /// (só com e-mail verificado; usuário/org podem desligar via
    /// settings — precedência user→org→global).
    /// </summary>
    public bool CoAuthorUser { get; set; } = true;
}
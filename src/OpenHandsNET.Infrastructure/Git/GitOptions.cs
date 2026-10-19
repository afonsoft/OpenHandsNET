namespace OpenHandsNET.Infrastructure.Git;

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
    public string AuthorName { get; set; } = "openhandsnet-agent";

    /// <summary>Agent commit author email.</summary>
    public string AuthorEmail { get; set; } = "agent@openhandsnet.local";
}
namespace OpenHandsNET.Domain.Events;

/// <summary>
/// Pull request created from a conversation's work branch (kind
/// <c>pull_request_created</c>, SPEC-52 RF-003). Environment-sourced.
/// </summary>
public sealed record PullRequestCreatedEvent : EventBase
{
    /// <summary>Creates an environment-sourced event.</summary>
    public PullRequestCreatedEvent() => Source = EventSource.Environment;

    /// <summary>Full PR URL.</summary>
    public required string Url { get; init; }

    /// <summary>PR number in the repository.</summary>
    public required int Number { get; init; }

    /// <summary>Repository (<c>owner/repo</c>).</summary>
    public string? Repository { get; init; }

    /// <summary>Provider kind (<c>github</c>).</summary>
    public string? ScmKind { get; init; }
}
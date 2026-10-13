namespace OpenHandsNET.Application.Git;

/// <summary>Parsed workspace git state (porcelain v2 buckets).</summary>
public sealed record GitChanges(
    string Branch,
    IReadOnlyList<GitFileStatus> Staged,
    IReadOnlyList<GitFileStatus> Modified,
    IReadOnlyList<GitFileStatus> Untracked,
    IReadOnlyList<GitFileStatus> Conflicts);

/// <summary>A file with its git status code (added|modified|deleted|renamed|conflict|untracked).</summary>
public sealed record GitFileStatus(string Path, string Status, string? OrigPath = null);

/// <summary>Structured diff report per file with hunks.</summary>
public sealed record GitDiffReport(
    IReadOnlyList<GitDiffFile> Files, int Additions, int Deletions);

/// <summary>One file's unified diff.</summary>
public sealed record GitDiffFile(
    string Path, string Status, IReadOnlyList<GitHunk> Hunks);

/// <summary>A unified-diff hunk header + body.</summary>
public sealed record GitHunk(
    int OldStart, int OldLines, int NewStart, int NewLines, string Content);

/// <summary>Commit listing row (commits-tab).</summary>
public sealed record GitLogEntry(
    string Hash, string Author, DateTimeOffset Date, string Message);

/// <summary>Result of a workspace commit.</summary>
public sealed record GitCommitResult(
    bool Success, string? Hash, string Output, string? Error);

/// <summary>Stored credential metadata (value lives encrypted in secrets).</summary>
public sealed record GitCredentialMeta(
    string Host, string Kind, string Owner, DateTimeOffset CreatedAt);

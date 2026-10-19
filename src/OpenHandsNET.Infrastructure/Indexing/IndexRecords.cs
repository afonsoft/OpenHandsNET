using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Indexing;

/// <summary>SPEC-60 RF-001: índice por repositório (por org).</summary>
public sealed class RepoIndexRecord : IOrgScoped
{
    public Guid Id { get; set; }
    public required Guid? OrgId { get; set; }
    public required string Repository { get; set; }
    public string Branch { get; set; } = "";
    public string? CommitSha { get; set; }
    /// <summary>indexing | ready | stale | failed.</summary>
    public string Status { get; set; } = "indexing";
    public string? StatsJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IndexedAt { get; set; }
}

/// <summary>Unidade de código indexada (símbolo ou chunk).</summary>
public sealed class CodeUnitRecord : IOrgScoped
{
    public Guid Id { get; set; }
    public required Guid? OrgId { get; set; }
    public Guid RepoIndexId { get; set; }
    public required string Path { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    /// <summary>symbol | chunk.</summary>
    public string Kind { get; set; } = "chunk";
    public required string Text { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>SPEC-60 RF-002: sessão Ask read-only.</summary>
public sealed class AskSessionRecord : IOrgScoped
{
    public Guid Id { get; set; }
    public required Guid? OrgId { get; set; }
    public Guid? RepoIndexId { get; set; }
    public required string Repository { get; set; }
    public string? Title { get; set; }
    /// <summary>JSON array de {role,text,citations,at}.</summary>
    public string MessagesJson { get; set; } = "[]";
    public string? CreatedBy { get; set; }
    public Guid? PromotedConversationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

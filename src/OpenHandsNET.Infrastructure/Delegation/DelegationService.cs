using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Delegation;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Delegation;

public sealed class DelegationService : IDelegationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DelegationService> _logger;
    private const int MaxDepth = 2;
    private const int MaxSiblings = 8;

    public DelegationService(AppDbContext db, ILogger<DelegationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DelegateTaskResponse> DelegateAsync(Guid parentConversationId, DelegateTaskRequest request, CancellationToken ct = default)
    {
        var parent = await _db.Conversations.FindAsync(new object[] { parentConversationId }, ct);
        if (parent == null)
        {
            throw new KeyNotFoundException($"Parent conversation '{parentConversationId}' not found");
        }

        if (parent.DelegationDepth >= MaxDepth)
        {
            throw new InvalidOperationException($"Maximum delegation depth ({MaxDepth}) reached.");
        }

        var siblingCount = await _db.Conversations.CountAsync(c => c.ParentConversationId == parentConversationId, ct);
        if (siblingCount >= MaxSiblings)
        {
            throw new InvalidOperationException($"Maximum sibling conversations limit ({MaxSiblings}) reached.");
        }

        var childId = Guid.NewGuid();
        int newDepth = parent.DelegationDepth + 1;

        _logger.LogInformation("Delegating sub-task from parent {ParentId} to child {ChildId} at depth {Depth}", parentConversationId, childId, newDepth);

        var child = new ConversationRecord
        {
            Id = childId,
            Title = request.TaskDescription,
            SelectedRepository = request.SelectedRepository ?? parent.SelectedRepository,
            AgentType = request.AgentType ?? parent.AgentType,
            LlmModel = request.LlmModel ?? parent.LlmModel,
            ExecutionStatus = "READY",
            ParentConversationId = parentConversationId,
            DelegationDepth = newDepth,
            OrgId = parent.OrgId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.Conversations.AddAsync(child, ct);
        await _db.SaveChangesAsync(ct);

        return new DelegateTaskResponse(childId, newDepth, "READY");
    }

    public async Task<IReadOnlyList<DelegationStatusDto>> ListSiblingsAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conv = await _db.Conversations.FindAsync(new object[] { conversationId }, ct);
        if (conv == null) return Array.Empty<DelegationStatusDto>();

        Guid? parentId = conv.ParentConversationId ?? conversationId;

        var family = await _db.Conversations
            .Where(c => c.Id == parentId || c.ParentConversationId == parentId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return family.Select(c => new DelegationStatusDto(
            c.Id,
            c.ParentConversationId,
            c.DelegationDepth,
            c.ExecutionStatus,
            c.Title ?? "Untitled",
            c.CreatedAt
        )).ToList();
    }

    public async Task WakeParentOnCompletionAsync(Guid childConversationId, string outcome, string summary, CancellationToken ct = default)
    {
        var child = await _db.Conversations.FindAsync(new object[] { childConversationId }, ct);
        if (child == null || !child.ParentConversationId.HasValue) return;

        var parentId = child.ParentConversationId.Value;
        _logger.LogInformation("Waking parent conversation {ParentId} for completed child {ChildId} with outcome {Outcome}", parentId, childConversationId, outcome);

        // Opcional: Inserir evento de notificação/intervenção ou logar no parent
        child.ExecutionStatus = outcome == "success" ? "COMPLETED" : "FAILED";
        child.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Delegation;
using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Infrastructure.Delegation;

/// <summary>
/// SPEC-62: delegação cross-sandbox. A filha é criada pelo pipeline normal
/// (<see cref="IAppConversationService.CreateAsync"/>): record STARTING +
/// start-task enfileirada + mensagem inicial no stream com kickoff — o
/// worker provisiona o sandbox e inicia o agente como qualquer conversa.
/// Permissões/modo/perfil do pai são herdados pela filha.
/// </summary>
public sealed class DelegationService : IDelegationService
{
    private readonly AppDbContext _db;
    private readonly IAppConversationService _conversations;
    private readonly ILogger<DelegationService> _logger;
    private const int MaxDepth = 2;
    private const int MaxSiblings = 8;

    public DelegationService(
        AppDbContext db,
        IAppConversationService conversations,
        ILogger<DelegationService> logger)
    {
        _db = db;
        _conversations = conversations;
        _logger = logger;
    }

    public async Task<DelegateTaskResponse> DelegateAsync(
        Guid parentConversationId, DelegateTaskRequest request,
        CancellationToken ct = default)
    {
        var parent = await _db.Conversations.FindAsync(
            new object[] { parentConversationId }, ct);
        if (parent == null)
        {
            throw new KeyNotFoundException(
                $"Parent conversation '{parentConversationId}' not found");
        }

        if (parent.DelegationDepth >= MaxDepth)
        {
            throw new InvalidOperationException(
                $"Maximum delegation depth ({MaxDepth}) reached.");
        }

        var siblingCount = await _db.Conversations.CountAsync(
            c => c.ParentConversationId == parentConversationId, ct);
        if (siblingCount >= MaxSiblings)
        {
            throw new InvalidOperationException(
                $"Maximum sibling conversations limit ({MaxSiblings}) reached.");
        }

        int newDepth = parent.DelegationDepth + 1;

        // SEC: herda as restrições do pai — permissões, modo, confirmation
        // mode e sandbox profile nunca relaxam numa filha.
        var inheritedRules = Application.Agent.PermissionRules.Parse(
            parent.PermissionsJson);

        var task = await _conversations.CreateAsync(
            new CreateConversationRequest(
                InitialMessage: new InitialMessage(
                    "user", [request.TaskDescription], Run: true),
                SelectedRepository: request.SelectedRepository
                    ?? parent.SelectedRepository,
                AgentType: request.AgentType ?? parent.AgentType,
                LlmModel: request.LlmModel ?? parent.LlmModel,
                SandboxSpecId: null,
                ConfirmationMode: parent.ConfirmationMode,
                SandboxProfile: parent.SandboxProfile,
                Permissions: inheritedRules.Count > 0 ? inheritedRules : null,
                Mode: parent.Mode),
            ct);

        var childId = task.AppConversationId
            ?? throw new InvalidOperationException(
                "Start task did not yield a conversation id.");
        var child = await _db.Conversations.FindAsync(
            new object[] { childId }, ct)
            ?? throw new InvalidOperationException(
                $"Delegated conversation '{childId}' not found.");

        child.ParentConversationId = parentConversationId;
        child.DelegationDepth = newDepth;
        child.OrgId = parent.OrgId;
        child.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Delegated sub-task from parent {ParentId} to child {ChildId}"
            + " at depth {Depth} (start-task {TaskId})",
            parentConversationId, childId, newDepth, task.Id);

        return new DelegateTaskResponse(childId, newDepth, task.Status.ToString());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationStatusDto>> ListSiblingsAsync(
        Guid conversationId, CancellationToken ct = default)
    {
        var conv = await _db.Conversations.FindAsync(
            new object[] { conversationId }, ct);
        if (conv == null) return Array.Empty<DelegationStatusDto>();

        Guid parentId = conv.ParentConversationId ?? conversationId;

        var children = await _db.Conversations
            .Where(c => c.ParentConversationId == parentId)
            .OrderBy(c => c.CreatedAtTicks)
            .ToListAsync(ct);

        return children.Select(c => new DelegationStatusDto(
            c.Id,
            c.ParentConversationId,
            c.DelegationDepth,
            c.ExecutionStatus,
            c.Title ?? "Untitled",
            c.CreatedAt
        )).ToList();
    }

    public async Task WakeParentOnCompletionAsync(
        Guid childConversationId, string outcome, string summary,
        CancellationToken ct = default)
    {
        var child = await _db.Conversations.FindAsync(
            new object[] { childConversationId }, ct);
        if (child == null || !child.ParentConversationId.HasValue) return;

        var parentId = child.ParentConversationId.Value;
        _logger.LogInformation(
            "Waking parent conversation {ParentId} for completed child"
            + " {ChildId} with outcome {Outcome}",
            parentId, childConversationId, outcome);

        child.ExecutionStatus = outcome == "success" ? "COMPLETED" : "FAILED";
        child.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

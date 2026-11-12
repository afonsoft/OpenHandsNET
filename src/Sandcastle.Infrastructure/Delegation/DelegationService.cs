using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Delegation;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Delegation;

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

    /// <summary>
    /// SPEC-143: o cap de siblings conta só os filhos AINDA ATIVOS —
    /// orquestradores (scan) fazem fan-out em ondas, reutilizando slots à
    /// medida que os filhos terminam. Enum names + literais legados do
    /// <see cref="WakeParentOnCompletionAsync"/>.
    /// </summary>
    private static readonly HashSet<string> TerminalStatuses = new(
        StringComparer.Ordinal)
    {
        nameof(Domain.Conversations.ConversationExecutionStatus.Stopped),
        nameof(Domain.Conversations.ConversationExecutionStatus.Finished),
        nameof(Domain.Conversations.ConversationExecutionStatus.Error),
        nameof(Domain.Conversations.ConversationExecutionStatus.Archived),
        "COMPLETED", "FAILED", "STOPPED", "CANCELLED",
    };

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

        // SPEC-143: só siblings ativos contam — filhos terminados
        // (waves de scan) liberam slots do cap.
        var activeCount = await _db.Conversations.CountAsync(
            c => c.ParentConversationId == parentConversationId
                && !TerminalStatuses.Contains(c.ExecutionStatus), ct);
        var siblingCount = activeCount;
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

        var createRequest = new CreateConversationRequest(
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
                Mode: parent.Mode);

        // SPEC-143: fora de request HTTP (orquestradores) não há
        // ICurrentUserAccessor — propaga o owner do pai para a filha via
        // CreateForUserAsync, mantendo plan/quota enforcement.
        var task = Guid.TryParse(parent.CreatedByUserId, out var ownerId)
            ? await _conversations.CreateForUserAsync(
                ownerId, parent.OrgId, createRequest, ct)
            : await _conversations.CreateAsync(createRequest, ct);

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
using System.Text.Json;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.DTOs;
using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/app-conversations</c> — paridade V1 (create assíncrono via
/// start-task, stream-start SSE, send-message com 404/409/410/503).
/// </summary>
public static class AppConversationEndpoints
{
    private const string IdGuid = "/{id:guid}";
    /// <summary>Maps the app-conversations route group.</summary>
    public static IEndpointRouteBuilder MapAppConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/app-conversations").WithTags("Conversations");

        group.MapGet("/search",
            SearchConversationsAsync);

        // SPEC-44 RF-002: inbox de attention — conversas que aguardam o
        // usuário (mesmo scoping do /search: não-arquivadas).
        group.MapGet("/attention",
            ListAttentionAsync);

        group.MapPost("/{id:guid}/attention/dismiss",
            DismissAttentionAsync);

        group.MapGet("/count",
            CountConversationsAsync);

        group.MapGet("/",
            ListConversationsAsync);

        group.MapGet(IdGuid,
            GetConversationAsync);

        group.MapPost("/", CreateAsync);
        group.MapPost("/stream-start", StreamStartAsync);

        // SPEC-40 RF-002: surfaces do sandbox (terminal ttyd…) resolvidas
        // para URL pública (SPEC-38) + preview token (RF-004). SPEC-113
        // RF-003: RoutingMode internal → proxy same-origin /api/v1/….
        group.MapGet("/{id:guid}/surfaces",
            GetSurfacesAsync).RequireAuthorization();

        group.MapPatch(IdGuid,
            UpdateConversationAsync);

        group.MapDelete(IdGuid,
            DeleteConversationAsync);

        group.MapPost("/{id:guid}/send-message", SendMessageAsync);

        // SPEC-41 RF-005: resume retoma run pausado — 409 fora de Paused.
        group.MapPost("/{id:guid}/resume",
            ResumeConversationAsync);

        // SPEC-135 RF-001: fork — clona eventos até o corte numa
        // conversa-filha (start-task próprio, parent_conversation_id).
        group.MapPost("/{id:guid}/fork", ForkAsync);

        // SPEC-135 RF-003: turn undo — soft-delete do último turno do
        // usuário + revert do workdir ao checkpoint do início do turno.
        group.MapPost("/{id:guid}/undo", UndoAsync);

        // SPEC-18 RF-005/006: switch_profile recria o sandbox com o novo
        // profile (start-task); switch_acp_model vale a partir do próximo run.
        group.MapPost("/{id:guid}/switch_profile",
            SwitchProfileAsync);
        group.MapPost("/{id:guid}/switch_acp_model",
            SwitchAcpModelAsync);

        // SPEC-29 RF-002: override de permissões por conversa (dono).
        group.MapPatch("/{id:guid}/permissions",
            UpdatePermissionsAsync);

        // SPEC-29 RF-003: plan mode — overlay read-only (deny mutations).
        group.MapPost("/{id:guid}/mode",
            SetConversationModeAsync);
        group.MapGet("/{id:guid}/model",
            GetConversationModelAsync);

        // SPEC-25 RF-006: superfície de tools — builtin + MCP + repo skills.
        // SPEC-29 RF-002: catálogo EFETIVO (deny da conversa/org removidas).
        group.MapGet("/{id:guid}/tools",
            GetConversationToolsAsync);

        // SPEC-30 RF-004: merged memory view — org entries + project INDEX.
        group.MapGet("/{id:guid}/memory",
            GetConversationMemoryAsync);

        // SPEC-25 RF-006: rescan on-demand — repete a descoberta do worker,
        // atualiza o evento repo_context e invalida o cache de skills.
        group.MapPost("/{id:guid}/context/rescan",
            RescanContextAsync);

        // SPEC-22 RF-004/005: compaction manual + ledger de contexto.
        // Runner ativo → o request evento entra na fila dele; idle →
        // compactação inline sobre o histórico persistido.
        group.MapPost("/{id:guid}/context/compact",
            CompactContextAsync);

        group.MapGet("/{id:guid}/context",
            GetConversationContextAsync);

        // SPEC-34 RF-002: artifacts — ref opaco (ws:/art:) resolvido pelo
        // store ativo; Range habilitado p/ previews do client.
        group.MapGet("/{id:guid}/artifacts/{*ref}",
            GetArtifactAsync);

        // Proxies git/files foram para GitEndpoints (SPEC-12).
        // skills/hooks reais em SkillEndpoints/HookEndpoints (SPEC-11).

        // start-tasks (polling).
        var tasks = app.MapGroup("/api/v1/app-conversations/start-tasks")
            .WithTags("Conversations");
        tasks.MapGet("/", BatchTasksAsync);
        tasks.MapGet("/search", SearchTasksAsync);
        tasks.MapGet("/count",
            CountTasksAsync);
        tasks.MapGet(IdGuid,
            GetStartTaskAsync);

        // SPEC-23 RF-005: catálogo de modelos para o ModelSelector da UI.
        app.MapGet("/api/v1/models",
            ListModelsAsync).WithTags("Models");

        return app;
    }

    private static async Task<IResult> SearchConversationsAsync(
        string? title__eq,
        DateTimeOffset? created_at__gte,
        string? page_id,
        int? limit,
        string? attention,
        IAppConversationService svc,
        CancellationToken ct)
    {
        var page = await svc.SearchAsync(new ConversationQuery(
            title__eq, created_at__gte, page_id, limit ?? 100,
            Attention: attention == "1" || attention == "true"), ct);
        return Results.Ok(new
        {
            items = page.Items.Select(ToDto),
            next_page_id = page.NextPageId,
        });
    }

    private static async Task<IResult> ListAttentionAsync(
        int? limit,
        IAppConversationService svc,
        CancellationToken ct)
    {
        var items = await svc.ListAttentionAsync(limit ?? 20, ct);
        return Results.Ok(new
        {
            count = items.Count,
            items = items.Select(i => new
            {
                conversation_id = i.ConversationId,
                title = i.Title,
                kind = i.Kind,
                since = i.SinceTicks,
                excerpt = i.Excerpt,
            }),
        });
    }

    private static async Task<IResult> DismissAttentionAsync(
        Guid id,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return await svc.DismissAttentionAsync(id, ct) switch
        {
            AttentionDismissResult.Dismissed
                or AttentionDismissResult.Noop => Results.NoContent(),
            AttentionDismissResult.Conflict =>
                SandcastleProblemFactory.Create(http, 409,
                    "confirmation requires an explicit decision"),
            _ => SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found"),
        };
    }

    private static async Task<IResult> CountConversationsAsync(
        string? title__eq,
        DateTimeOffset? created_at__gte,
        IAppConversationService svc,
        CancellationToken ct)
    {
        return Results.Ok(await svc.CountAsync(
            new ConversationQuery(title__eq, created_at__gte), ct));
    }

    private static async Task<IResult> ListConversationsAsync(
        string ids,
        IAppConversationService svc,
        CancellationToken ct)
    {
        var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).ToList();
        var items = await svc.GetBatchAsync(parsed, ct);
        return Results.Ok(items.Select(ToDto));
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return await svc.GetAsync(id, ct) is { } c
            ? Results.Ok(ToDto(c))
            : SandcastleProblemFactory.Create(http, 404, $"Conversation {id} not found");
    }

    private static async Task<IResult> GetSurfacesAsync(
        [AsParameters] SurfacesParams p)
    {
        var conv = await p.Convs.GetAsync(p.Id, p.Ct);
        if (conv is null)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 404, $"Conversation {p.Id} not found");
        }

        if (conv.SandboxId is not { } sid
            || await p.Sandboxes.GetAsync(sid, p.Ct) is not { } sandbox)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var surfaces = await p.Executor.SurfacesAsync(sandbox, p.Ct);
        return Results.Ok(surfaces.Select(s =>
        {
            var (url, expires) = ResolveSurfaceUrl(
                p.Options.Value, p.Tokens, sandbox, s);

            return (object)new
            {
                name = s.Name,
                protocol = s.Protocol,
                port = s.Port,
                url,
                expires_at = expires,
            };
        }).ToList());
    }

    private static async Task<IResult> UpdateConversationAsync(
        Guid id,
        PatchConversationRequest body,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return await svc.UpdateAsync(id, body.Title, body.Tags, body.Pinned, ct) is { } c
            ? Results.Ok(ToDto(c))
            : SandcastleProblemFactory.Create(http, 404, $"Conversation {id} not found");
    }

    private static async Task<IResult> DeleteConversationAsync(
        Guid id,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return await svc.DeleteAsync(id, ct)
            ? Results.NoContent()
            : SandcastleProblemFactory.Create(http, 404, $"Conversation {id} not found");
    }

    private static async Task<IResult> ResumeConversationAsync(
        Guid id,
        IAppConversationService svc,
        Sandcastle.Application.Agent.IAgentRunnerHost runnerHost,
        HttpContext http,
        CancellationToken ct)
    {
        if (await svc.GetAsync(id, ct) is not { } conv)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        if (conv.ExecutionStatus
                != Sandcastle.Domain.Conversations
                    .ConversationExecutionStatus.Paused)
        {
            return SandcastleProblemFactory.Create(
                http, 409, $"Conversation {id} is not paused");
        }

        await runnerHost.ResumeAsync(id, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> SwitchProfileAsync(
        Guid id,
        SwitchProfileRequest body,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return MapSwitch(
            await svc.SwitchProfileAsync(id, body.Profile, ct), id, http);
    }

    private static async Task<IResult> SwitchAcpModelAsync(
        Guid id,
        SwitchAcpModelRequest body,
        IAppConversationService svc,
        Application.Agent.IModelCatalog catalog,
        HttpContext http,
        CancellationToken ct)
    {
        var requested = body.Model ?? "";
        // SPEC-23 RF-005: valida contra o catálogo quando saudável;
        // descoberta degradada (só "configured") é fail-open.
        var models = await catalog.ListAsync(ct);
        if (models.Any(m => m.Provider != "configured")
            && models.All(m => !string.Equals(
                m.Id, requested, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.UnprocessableEntity(new
            {
                error = "unknown_model",
                available = models.Select(m => m.Id).ToArray(),
            });
        }

        return MapSwitch(
            await svc.SwitchModelAsync(id, requested, ct), id, http);
    }

    private static async Task<IResult> UpdatePermissionsAsync(
        Guid id,
        SetPermissionsRequest body,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        var rules = body.Permissions?.Select(p =>
            new Application.Agent.AgentPermissionRule(
                p.Pattern, ParseDecision(p.Decision))).ToList();
        if (await svc.SetPermissionsAsync(id, rules, ct) is not { } conv)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        return Results.Ok(ToDto(conv));
    }

    private static async Task<IResult> SetConversationModeAsync(
        Guid id,
        SetModeRequest body,
        IAppConversationService svc,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Mode))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "mode must be 'plan' or 'build'");
        }

        if (await svc.SetModeAsync(id, body.Mode, ct) is not { } conv)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        return Results.Ok(ToDto(conv));
    }

    private static async Task<IResult> GetConversationModelAsync(
        Guid id,
        IAppConversationService svc,
        Application.Agent.IModelCatalog catalog,
        Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
        HttpContext http,
        CancellationToken ct)
    {
        if (await svc.GetAsync(id, ct) is not { } conv)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        var model = conv.LlmModel ?? llmOpts.Value.Model;
        var info = await catalog.GetAsync(model, ct);
        return Results.Ok(new
        {
            model,
            role_models = llmOpts.Value.RoleModels,
            window = info?.ContextWindow,
            provider = info?.Provider,
        });
    }

    private static async Task<IResult> GetConversationToolsAsync(
        Guid id,
        Application.Agent.IToolRegistry registry,
        Infrastructure.Persistence.AppDbContext db,
        Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
        HttpContext http,
        CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([id], ct);
        if (record is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        var evaluator = await Infrastructure.Agent
            .ConversationPermissionResolver.ResolveAsync(
                db, record, llmOpts.Value, ct);
        var effective = Infrastructure.Agent.ConversationPermissionResolver
            .Catalog(registry, evaluator);
        var tools = effective.All.Select(t => new
        {
            name = t.Schema.Name,
            description = t.Schema.Description,
        });
        return Results.Ok(new { tools });
    }

    private static async Task<IResult> GetConversationMemoryAsync(
        Guid id,
        IAppConversationService svc,
        Infrastructure.Persistence.AppDbContext db,
        ISandboxService sandboxes,
        ISandboxExecutor executor,
        HttpContext http,
        CancellationToken ct)
    {
        if (await svc.GetAsync(id, ct) is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        var record = await db.Conversations.FindAsync([id], ct);
        var org = record?.OrgId is { } orgId
            ? (await db.MemoryRecords.AsNoTracking()
                .Where(m => m.OrgId == orgId && m.DeletedAt == null)
                .ToListAsync(ct))
                .OrderByDescending(m => m.UpdatedAt)
                .Select(m => new
                {
                    key = m.Key,
                    scope = "org",
                    updated_at = m.UpdatedAt,
                }).ToList()
            : [];

        IReadOnlyList<string> projectFiles = [];
        if (record?.SandboxId is { } sandboxId)
        {
            var sandbox = await sandboxes.GetAsync(sandboxId, ct);
            if (sandbox?.Status == Domain.Sandbox.SandboxStatus.Running)
            {
                try
                {
                    var files = await executor.ListFilesAsync(sandbox,
                        "/workspace/repo/.openhands/memory", ct);
                    projectFiles = files.Where(f => !f.IsDirectory)
                        .Select(f => f.Path).ToList();
                }
                catch (Exception) { /* memória ainda não existe */ }
            }
        }

        return Results.Ok(new { org, project_files = projectFiles });
    }

    private static async Task<IResult> RescanContextAsync(
        [AsParameters] RescanParams p)
    {
        if (await p.Svc.GetAsync(p.Id, p.Ct) is null)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 404, $"Conversation {p.Id} not found");
        }

        var sandbox = (await p.Sandboxes.SearchAsync(
                null, p.Id, p.Ct))
            .FirstOrDefault(s => s.Status == SandboxStatus.Running);
        if (sandbox is null)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 409, "Sandbox is not running");
        }

        var manifest = await p.Discovery.DiscoverAsync(p.Executor, sandbox, p.Ct);
        await p.Events.AppendAsync(p.Id, new ConversationStateUpdateEvent
        {
            Key = "repo_context",
            Value = JsonSerializer.SerializeToElement(new
            {
                instructions_files = manifest.InstructionFiles,
                skills = manifest.Skills.Select(s => new
                {
                    name = s.Name,
                    path = s.Path,
                    description = s.Description,
                }),
                config = manifest.HasRepoConfig,
                readme = manifest.ReadmePath,
            }, EventJson.Options),
        }, p.Ct);
        p.Sp.GetService<Infrastructure.Skills.RepoSkillProvider>()
            ?.Invalidate(sandbox.Id);
        return Results.Ok(manifest);
    }

    private static async Task<IResult> CompactContextAsync(
        [AsParameters] CompactParams p)
    {
        if (await p.Svc.GetAsync(p.Id, p.Ct) is null)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 404, $"Conversation {p.Id} not found");
        }

        if (p.Host.IsRunning(p.Id))
        {
            await p.Events.AppendAsync(p.Id, new CondensationRequest
            {
                Source = EventSource.User,
                Trigger = "manual",
                Instructions = p.Body?.Instructions,
            }, p.Ct);
            return Results.Accepted(value: new { queued = true });
        }

        var compacted = await p.Compactor.CompactStoredAsync(
            p.Id, Application.Agent.CompactionTrigger.Manual,
            p.Body?.Instructions, p.Ct);
        return Results.Accepted(value: new { queued = false, compacted });
    }

    private static async Task<IResult> GetConversationContextAsync(
        Guid id,
        IAppConversationService svc,
        IEventService events,
        HttpContext http,
        CancellationToken ct)
    {
        if (await svc.GetAsync(id, ct) is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        var page = await events.SearchAsync(id, new EventQuery(
            KindEq: "ConversationStateUpdateEvent",
            Sort: EventSortOrder.TimestampDesc, Limit: 50), ct);
        var state = page.Items
            .OfType<ConversationStateUpdateEvent>()
            .FirstOrDefault(e => e.Key == "context_state");
        return state?.Value is { } v
            ? Results.Ok(v)
            : Results.Ok(new
            {
                tokens_estimate = 0,
                window = (int?)null,
                compactions = 0,
                boundary_event_id = (string?)null,
                model = (string?)null,
                updated_at = (string?)null,
            });
    }

    private static async Task<IResult> GetArtifactAsync(
        Guid id,
        string @ref,
        IAppConversationService svc,
        Application.Artifacts.IArtifactStore store,
        HttpContext http,
        CancellationToken ct)
    {
        if (await svc.GetAsync(id, ct) is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found");
        }

        var stream = await store.OpenAsync(id, @ref, ct);
        if (stream is null)
        {
            return SandcastleProblemFactory.Create(
                http, 404, $"Artifact {@ref} not found");
        }

        // Range exige stream seekable — S3 response vem em rede;
        // bufferiza (payloads são de escala de tool output).
        if (!stream.CanSeek)
        {
            var buf = new MemoryStream();
            await stream.CopyToAsync(buf, ct);
            await stream.DisposeAsync();
            buf.Position = 0;
            stream = buf;
        }

        return Results.File(stream, "application/octet-stream",
            enableRangeProcessing: true);
    }

    private static async Task<IResult> CountTasksAsync(
        string? status,
        IStartTaskService svc,
        CancellationToken ct)
    {
        return Results.Ok(await svc.CountAsync(ParseStatus(status), ct));
    }

    private static async Task<IResult> GetStartTaskAsync(
        Guid id,
        IStartTaskService svc,
        HttpContext http,
        CancellationToken ct)
    {
        return await svc.GetAsync(id, ct) is { } t
            ? Results.Ok(ToDto(t))
            : SandcastleProblemFactory.Create(http, 404, $"Start task {id} not found");
    }

    private static async Task<IResult> ListModelsAsync(
        Application.Agent.IModelCatalog catalog,
        Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
        CancellationToken ct)
    {
        var models = await catalog.ListAsync(ct);
        return Results.Ok(new
        {
            @default = llmOpts.Value.Model,
            role_models = llmOpts.Value.RoleModels,
            models = models.Select(m => new
            {
                id = m.Id,
                provider = m.Provider,
                context_window = m.ContextWindow,
                pricing = m.Pricing is { } p ? new
                {
                    input_per_million = p.InputPerMillion,
                    output_per_million = p.OutputPerMillion,
                } : null,
            }).ToArray(),
        });
    }

    private static async Task<IResult> CreateAsync(
        [AsParameters] CreateConversationParams p)
    {
        var user = p.Current.User;

        // SPEC-59 RF-002: screening do initial_message.
        if (await ScreenInitialMessageAsync(p, user) is { } blocked)
        {
            return blocked;
        }

        // SPEC-57 RF-003: impersonation — scope sessions:impersonate +
        // alvo membro da mesma org; usage/quota atribuída ao alvo.
        if (p.Body.CreateAsUserId is { } targetId)
        {
            return await CreateImpersonatedAsync(p, user, targetId);
        }

        AppConversationStartTask createdTask;
        try
        {
            createdTask = await p.Svc.CreateAsync(Map(p.Body), p.Ct);
        }
        catch (Sandcastle.Application.Identity.PlanLimitExceededException ex)
        {
            return PlanLimitProblem(ex.Error);
        }
        catch (RepositorySelectionException ex)
        {
            return SandcastleProblemFactory.Create(p.Http, 400, ex.Message);
        }

        return Results.Created(
            $"/api/v1/app-conversations/start-tasks/{createdTask.Id}",
            ToDto(createdTask));
    }

    private static async Task<IResult?> ScreenInitialMessageAsync(
        CreateConversationParams p, AuthUser? user)
    {
        if (p.Body.InitialMessage is not { } im)
        {
            return null;
        }
        var text = string.Join(' ',
            im.Content.Select(c => c.ToString() ?? ""));
        var org2 = user is { } u3
            ? await p.OrgResolver.ResolveAsync(u3, null, p.Ct) : null;
        var verdict = await p.InputGuard.ScoreAsync(
            text, "ui", org2?.OrgId, p.Ct);
        if (verdict.Verdict
            != Application.Security.InputGuardVerdict.Block)
        {
            return null;
        }
        await p.Audit.RecordAsync(
            "guard.input_blocked",
            user?.UserId.ToString(), org2?.OrgId,
            "app_conversation", null,
            metadata: new { categories = verdict.Categories },
            cancellationToken: p.Http.RequestAborted);
        return SandcastleProblemFactory.Create(
            p.Http, 403, "input_blocked");
    }

    private static async Task<IResult> CreateImpersonatedAsync(
        CreateConversationParams p, AuthUser? user, Guid targetId)
    {
        if (user is null
            || !Policies.ScopeExtensions.HasScope(
                user, ApiScopes.SessionsImpersonate))
        {
            return Results.Json(
                new { error = "insufficient_scope" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        var org = await p.OrgResolver.ResolveAsync(user, null, p.Ct);
        var isMember = org is not null
            && await p.Db.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.OrgId == org.OrgId
                    && m.UserId == targetId, p.Ct);
        if (org is null)
            isMember = false;
        if (!isMember)
        {
            return SandcastleProblemFactory.Create(
                p.Http, 403, "impersonation target is not an org member");
        }
        try
        {
            var task = await p.Svc.CreateForUserAsync(
                targetId, org?.OrgId, Map(p.Body), p.Ct);
            if (task.AppConversationId is { } convId
                && await p.Db.Conversations.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == convId, p.Ct)
                    is { } conv)
            {
                conv.CreatedVia = user.ActorKind
                    == ActorKind.ServiceUser
                    ? $"service_user:{user.UserId}"
                    : $"user:{user.UserId}";
                await p.Db.SaveChangesAsync(p.Ct);
            }
            await p.Audit.RecordAsync(
                "session.impersonated", user.UserId.ToString(),
                org?.OrgId, "app_conversation",
                task.AppConversationId?.ToString(),
                metadata: new { target = targetId.ToString() },
                cancellationToken: p.Ct);
            return Results.Created(
                $"/api/v1/app-conversations/start-tasks/{task.Id}",
                ToDto(task));
        }
        catch (Sandcastle.Application.Identity
            .PlanLimitExceededException ex)
        {
            return PlanLimitProblem(ex.Error);
        }
        catch (RepositorySelectionException ex)
        {
            return SandcastleProblemFactory.Create(p.Http, 400, ex.Message);
        }
    }

    /// <summary>
    /// 403 problem+json com os campos extras do contrato de plano
    /// (SPEC-20 RF-003): detail fixo + limit_key/limit/used.
    /// </summary>
    internal static IResult PlanLimitProblem(
        Sandcastle.Application.Identity.PlanLimitError error) =>
        Results.Problem(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = "plan_limit_exceeded",
            Extensions =
            {
                ["limit_key"] = error.LimitKey,
                ["limit"] = error.Limit,
                ["used"] = error.Used,
            },
        });

    private static IResult StreamStartAsync(
        CreateAppConversationRequest body,
        IAppConversationService svc, IStartTaskService tasks,
        HttpContext http,
        Microsoft.Extensions.Options.IOptions<MultiRepoOptions> multiRepo,
        CancellationToken ct)
    {
        // SPEC-146: valida a seleção de repos ANTES de abrir o SSE —
        // inválido → 400 problem, nunca task+stream quebrada.
        try
        {
            RepositorySelection.Normalize(body.SelectedRepository,
                body.SelectedRepositories,
                multiRepo.Value.EffectiveMaxRepos);
        }
        catch (RepositorySelectionException ex)
        {
            return SandcastleProblemFactory.Create(http, 400, ex.Message);
        }

        // SSE: um item por transição de status até READY|ERROR.
        return TypedResults.ServerSentEvents(StreamTaskUpdates(body, svc, tasks, ct));
    }

    private static async IAsyncEnumerable<
        System.Net.ServerSentEvents.SseItem<StartTaskDto>> StreamTaskUpdates(
        CreateAppConversationRequest body,
        IAppConversationService svc, IStartTaskService tasks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var task = await svc.CreateAsync(Map(body), ct);
        yield return new System.Net.ServerSentEvents.SseItem<StartTaskDto>(ToDto(task));
        await foreach (var update in tasks.WatchAsync(task.Id, ct))
        {
            yield return new System.Net.ServerSentEvents.SseItem<StartTaskDto>(
                ToDto(update));
        }
    }

    private static async Task<IResult> SendMessageAsync(
        [AsParameters] SendMessageParams p)
    {
        // SPEC-59 RF-002: screening do texto antes de entrar no stream.
        var text = string.Join(' ', p.Body.Content
            .Select(c => c.ToString() ?? "")
            .Where(t => t.Length > 0));
        var org = p.Current.User is { } u2
            ? await p.Orgs.ResolveAsync(u2, null, p.Ct) : null;
        var verdict = await p.InputGuard.ScoreAsync(
            text, "ui", org?.OrgId, p.Ct);
        if (verdict.Verdict
            == Application.Security.InputGuardVerdict.Block)
        {
            await p.Audit.RecordAsync(
                "guard.input_blocked",
                p.Current.User?.UserId.ToString(), org?.OrgId,
                "app_conversation", p.Id.ToString(),
                metadata: new { categories = verdict.Categories },
                cancellationToken: p.Http.RequestAborted);
            return SandcastleProblemFactory.Create(
                p.Http, 403, "input_blocked");
        }

        var error = await p.Svc.ValidateSendMessageAsync(p.Id, p.Ct);
        if (error is not null)
        {
            return error switch
            {
                SendMessageError.NotFound => SandcastleProblemFactory.Create(
                    p.Http, 404, $"Conversation {p.Id} not found"),
                SendMessageError.Gone => SandcastleProblemFactory.Create(
                    p.Http, 410, SandcastleProblemFactory.ConversationGone),
                SendMessageError.ServiceUnavailable => SandcastleProblemFactory.Create(
                    p.Http, 503, SandcastleProblemFactory.SandboxError),
                _ => SandcastleProblemFactory.Create(
                    p.Http, 409, SandcastleProblemFactory.SandboxNotRunning),
            };
        }

        // Mensagem do usuário entra no stream como MessageEvent (V1).
        var content = p.Body.Content.Select(el => (object)el).ToList();
        var message = new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = p.Body.Role,
                content,
            }, EventJson.Options),
        };
        await p.Events.AppendAsync(p.Id, message, p.Ct);
        if (p.Body.Run)
        {
            await p.RunnerHost.NotifyMessageAsync(p.Id, p.Ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> BatchTasksAsync(
        string ids, IStartTaskService svc, CancellationToken ct)
    {
        var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).ToList();
        return Results.Ok((await svc.GetBatchAsync(parsed, ct)).Select(ToDto));
    }

    private static async Task<IResult> SearchTasksAsync(
        string? status, string? page_id, int? limit,
        IStartTaskService svc, CancellationToken ct) =>
        Results.Ok((await svc.SearchAsync(
                ParseStatus(status), page_id, limit ?? 100, ct))
            .Select(ToDto));

    private static IResult MapSwitch(
        SwitchResult r, Guid id, HttpContext http) => r.Error switch
        {
            null => Results.Ok(ToDto(r.Conversation!)),
            SwitchError.NotFound => SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found"),
            SwitchError.InvalidProfile => SandcastleProblemFactory.Create(
                http, 400, "unknown sandbox profile (small|medium|large)"),
            SwitchError.InvalidModel => SandcastleProblemFactory.Create(
                http, 400, "model must be a non-empty string"),
            SwitchError.SandboxError => SandcastleProblemFactory.Create(
                http, 502, SandcastleProblemFactory.SandboxError),
            _ => SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning),
        };

    private static CreateConversationRequest Map(CreateAppConversationRequest b) =>
        new(b.InitialMessage is null ? null : new InitialMessage(
                b.InitialMessage.Role,
                b.InitialMessage.Content.Cast<object>().ToList(),
                b.InitialMessage.Run),
            b.SelectedRepository, b.AgentType, b.LlmModel, b.SandboxSpecId,
            b.ConfirmationMode, b.SandboxProfile,
            b.Permissions?.Select(p => new Application.Agent.AgentPermissionRule(
                p.Pattern, ParseDecision(p.Decision))).ToList(),
            b.Mode, b.Secrets,
            SelectedRepositories: b.SelectedRepositories);

    private static StartTaskStatus? ParseStatus(string? s) =>
        Enum.TryParse<StartTaskStatus>(s, ignoreCase: true, out var v) ? v : null;

    private static Application.Agent.PermissionDecision ParseDecision(string s) =>
        Enum.TryParse<Application.Agent.PermissionDecision>(
            s, ignoreCase: true, out var d)
            ? d : Application.Agent.PermissionDecision.Ask;

    internal static StartTaskDto ToDto(AppConversationStartTask t) => new()
    {
        Id = t.Id.ToString(),
        Status = t.Status.ToString().ToUpperInvariant(),
        AppConversationId = t.AppConversationId?.ToString(),
        SandboxId = t.SandboxId?.ToString(),
        Detail = t.Detail,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };

    private static AppConversationDto ToDto(AppConversation c) => new()
    {
        Id = c.Id.ToString(),
        Title = c.Title,
        SandboxId = c.SandboxId?.ToString(),
        SelectedRepository = c.SelectedRepository,
        AgentType = c.AgentType,
        LlmModel = c.LlmModel,
        SandboxProfile = c.SandboxProfile,
        ExecutionStatus = c.ExecutionStatus.ToString().ToUpperInvariant(),
        Tags = c.Tags,
        Pinned = c.Pinned,
        ConfirmationMode = c.ConfirmationMode,
        NeedsAttention = c.NeedsAttentionKind,
        ParentConversationId = c.ParentConversationId?.ToString(),
        // SPEC-146: lista efetiva (null só quando não há repo algum).
        Repositories = c.EffectiveRepositories is { Count: > 0 } r ? r : null,
        PullRequests = c.PullRequests?.Select(p =>
            new ConversationPullRequestDto
            {
                Repository = p.Repository,
                Url = p.Url,
                Number = p.Number,
                State = p.State,
                HeadSha = p.HeadSha,
            }).ToList(),
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };

    /// <summary>
    /// SPEC-115 RF-001: resolve a URL user-facing de uma surface. Em
    /// <c>RoutingMode=internal</c> (default OSS) é SEMPRE o path relativo
    /// same-origin do proxy — o client nunca recebe host/porta do sandbox.
    /// Modos <c>path</c>/<c>host</c> (opt-in enterprise) levam preview token.
    /// </summary>
    internal static (string Url, DateTimeOffset? Expires) ResolveSurfaceUrl(
        Sandcastle.Sandboxing.SandboxOptions options,
        Routing.PreviewTokenService tokens,
        Domain.Sandbox.Sandbox sandbox,
        Application.Sandbox.AgentSurface surface)
    {
        var internalMode = (options.RoutingMode ?? "internal")
            .Equals("internal", StringComparison.OrdinalIgnoreCase);
        if (internalMode)
        {
            // SPEC-113 RF-003: proxy same-origin — cookie de sessão cobre
            // o iframe/WS do client em qualquer RoutingMode.
            return ($"/api/v1/sandboxes/{sandbox.Id}/surfaces/{surface.Port}/", null);
        }

        var (token, exp) = tokens.Mint(sandbox.Id, surface.Port);
        var base_ = Sandcastle.Sandboxing.SandboxUrlBuilder
            .BuildFor(options, sandbox.Id, surface.Port);
        return ($"{base_}?token={Uri.EscapeDataString(token)}", exp);
    }

    /// <summary>Binding do GET surfaces (SPEC-104 — AsParameters).</summary>
    private sealed class SurfacesParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromServices] public IAppConversationService Convs { get; init; } = null!;
        [FromServices] public ISandboxService Sandboxes { get; init; } = null!;
        [FromServices] public ISandboxExecutor Executor { get; init; } = null!;
        [FromServices] public Microsoft.Extensions.Options.IOptions<Sandcastle.Sandboxing.SandboxOptions> Options { get; init; } = null!;
        [FromServices] public Routing.PreviewTokenService Tokens { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>Binding do POST context/rescan (SPEC-104 — AsParameters).</summary>
    private sealed class RescanParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromServices] public IAppConversationService Svc { get; init; } = null!;
        [FromServices] public ISandboxService Sandboxes { get; init; } = null!;
        [FromServices] public ISandboxExecutor Executor { get; init; } = null!;
        [FromServices] public Application.Discovery.IRepoDiscoveryService Discovery { get; init; } = null!;
        [FromServices] public IEventService Events { get; init; } = null!;
        [FromServices] public IServiceProvider Sp { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>Binding do POST context/compact (SPEC-104 — AsParameters).</summary>
    private sealed class CompactParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromBody] public CompactContextRequest? Body { get; init; }
        [FromServices] public IAppConversationService Svc { get; init; } = null!;
        [FromServices] public Application.Agent.ICompactionService Compactor { get; init; } = null!;
        [FromServices] public Application.Agent.IAgentRunnerHost Host { get; init; } = null!;
        [FromServices] public IEventService Events { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>Binding do POST /app-conversations (SPEC-104 — AsParameters).</summary>
    private sealed class CreateConversationParams
    {
        [FromBody] public CreateAppConversationRequest Body { get; init; } = null!;
        [FromServices] public IAppConversationService Svc { get; init; } = null!;
        [FromServices] public ICurrentUserAccessor Current { get; init; } = null!;
        [FromServices] public IEffectiveOrgResolver OrgResolver { get; init; } = null!;
        [FromServices] public IAuditService Audit { get; init; } = null!;
        [FromServices] public Sandcastle.Application.Security.IInputGuard InputGuard { get; init; } = null!;
        [FromServices] public Infrastructure.Persistence.AppDbContext Db { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }

    /// <summary>
    /// SPEC-135 RF-001: <c>POST .../fork</c> — service checa run ativa e
    /// validade do <c>at_event_id</c>; retorna a task da conversa-filha.
    /// </summary>
    private static async Task<IResult> ForkAsync(
        Guid id, ForkConversationRequest body,
        IConversationUxService ux,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        HttpContext http, CancellationToken ct)
    {
        var org = current.User is { } u
            ? await orgs.ResolveAsync(u, null, ct) : null;
        var (result, error) = await ux.ForkAsync(
            id, org?.OrgId, current.User?.UserId.ToString(), body, ct);
        return error switch
        {
            null => Results.Ok(new
            {
                id = result!.Id,
                start_task_id = result.StartTaskId,
                parent_conversation_id = result.ParentConversationId,
            }),
            ForkError.RunActive => SandcastleProblemFactory.Create(
                http, 409, "conversation has an active run"),
            ForkError.EventNotFound => SandcastleProblemFactory.Create(
                http, 404, "at_event_id not found in conversation"),
            _ => SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found"),
        };
    }

    /// <summary>
    /// SPEC-135 RF-003: <c>POST .../undo</c> — idempotente; 404 no_turn
    /// quando não há turno de usuário restante, 409 com run ativa.
    /// </summary>
    private static async Task<IResult> UndoAsync(
        Guid id, IConversationUxService ux,
        HttpContext http, CancellationToken ct)
    {
        var (result, error) = await ux.UndoAsync(id, ct);
        return error switch
        {
            null => Results.Ok(result),
            UndoError.RunActive => SandcastleProblemFactory.Create(
                http, 409, "conversation has an active run"),
            UndoError.NoTurn => SandcastleProblemFactory.Create(
                http, 404, "no_turn"),
            _ => SandcastleProblemFactory.Create(
                http, 404, $"Conversation {id} not found"),
        };
    }

    /// <summary>Binding do POST send-message (SPEC-104 — AsParameters).</summary>
    private sealed class SendMessageParams
    {
        [FromRoute] public Guid Id { get; init; }
        [FromBody] public SendMessageRequest Body { get; init; } = null!;
        [FromServices] public IAppConversationService Svc { get; init; } = null!;
        [FromServices] public IEventService Events { get; init; } = null!;
        [FromServices] public Sandcastle.Application.Agent.IAgentRunnerHost RunnerHost { get; init; } = null!;
        [FromServices] public Sandcastle.Application.Security.IInputGuard InputGuard { get; init; } = null!;
        [FromServices] public ICurrentUserAccessor Current { get; init; } = null!;
        [FromServices] public IEffectiveOrgResolver Orgs { get; init; } = null!;
        [FromServices] public IAuditService Audit { get; init; } = null!;
        public HttpContext Http { get; init; } = null!;
        public CancellationToken Ct { get; init; }
    }
}
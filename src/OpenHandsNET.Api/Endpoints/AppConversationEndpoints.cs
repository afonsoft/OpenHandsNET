using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Api.DTOs;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Conversations;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Conversations;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/app-conversations</c> — paridade V1 (create assíncrono via
/// start-task, stream-start SSE, send-message com 404/409/410/503).
/// </summary>
public static class AppConversationEndpoints
{
    /// <summary>Maps the app-conversations route group.</summary>
    public static IEndpointRouteBuilder MapAppConversationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/app-conversations").WithTags("Conversations");

        group.MapGet("/search", async (
            string? title__eq, DateTimeOffset? created_at__gte,
            string? page_id, int? limit,
            IAppConversationService svc, CancellationToken ct) =>
        {
            var page = await svc.SearchAsync(new ConversationQuery(
                title__eq, created_at__gte, page_id, limit ?? 100), ct);
            return Results.Ok(new
            {
                items = page.Items.Select(ToDto),
                next_page_id = page.NextPageId,
            });
        });

        group.MapGet("/count", async (
            string? title__eq, DateTimeOffset? created_at__gte,
            IAppConversationService svc, CancellationToken ct) =>
            Results.Ok(await svc.CountAsync(
                new ConversationQuery(title__eq, created_at__gte), ct)));

        group.MapGet("/", async (
            string ids, IAppConversationService svc, CancellationToken ct) =>
        {
            var parsed = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();
            var items = await svc.GetBatchAsync(parsed, ct);
            return Results.Ok(items.Select(ToDto));
        });

        group.MapGet("/{id:guid}", async (
            Guid id, IAppConversationService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } c
                ? Results.Ok(ToDto(c))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapPost("/", CreateAsync);
        group.MapPost("/stream-start", StreamStartAsync);

        group.MapPatch("/{id:guid}", async (
            Guid id, PatchConversationRequest body,
            IAppConversationService svc, HttpContext http, CancellationToken ct) =>
            await svc.UpdateAsync(id, body.Title, body.Tags, body.Pinned, ct) is { } c
                ? Results.Ok(ToDto(c))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapDelete("/{id:guid}", async (
            Guid id, IAppConversationService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent()
                : OpenHandsNetProblemFactory.Create(http, 404, $"Conversation {id} not found"));

        group.MapPost("/{id:guid}/send-message", SendMessageAsync);

        // SPEC-18 RF-005/006: switch_profile recria o sandbox com o novo
        // profile (start-task); switch_acp_model vale a partir do próximo run.
        group.MapPost("/{id:guid}/switch_profile", async (
            Guid id, SwitchProfileRequest body, IAppConversationService svc,
            HttpContext http, CancellationToken ct) =>
            MapSwitch(
                await svc.SwitchProfileAsync(id, body.Profile, ct), id, http));
        group.MapPost("/{id:guid}/switch_acp_model", async (
            Guid id, SwitchAcpModelRequest body, IAppConversationService svc,
            Application.Agent.IModelCatalog catalog,
            HttpContext http, CancellationToken ct) =>
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
        });

        // SPEC-29 RF-002: override de permissões por conversa (dono).
        group.MapPatch("/{id:guid}/permissions", async (
            Guid id, SetPermissionsRequest body,
            IAppConversationService svc,
            HttpContext http, CancellationToken ct) =>
        {
            var rules = body.Permissions?.Select(p =>
                new Application.Agent.AgentPermissionRule(
                    p.Pattern, ParseDecision(p.Decision))).ToList();
            if (await svc.SetPermissionsAsync(id, rules, ct) is not { } conv)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            return Results.Ok(ToDto(conv));
        });

        // SPEC-29 RF-003: plan mode — overlay read-only (deny mutations).
        group.MapPost("/{id:guid}/mode", async (
            Guid id, SetModeRequest body,
            IAppConversationService svc,
            HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Mode))
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "mode must be 'plan' or 'build'");
            }

            if (await svc.SetModeAsync(id, body.Mode, ct) is not { } conv)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            return Results.Ok(ToDto(conv));
        });
        group.MapGet("/{id:guid}/model", async (
            Guid id, IAppConversationService svc,
            Application.Agent.IModelCatalog catalog,
            Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
            HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is not { } conv)
            {
                return OpenHandsNetProblemFactory.Create(
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
        });

        // SPEC-25 RF-006: superfície de tools — builtin + MCP + repo skills.
        // SPEC-29 RF-002: catálogo EFETIVO (deny da conversa/org removidas).
        group.MapGet("/{id:guid}/tools", async (
            Guid id, IAppConversationService svc,
            Application.Agent.IToolRegistry registry,
            Infrastructure.Persistence.AppDbContext db,
            Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
            HttpContext http, CancellationToken ct) =>
        {
            var record = await db.Conversations.FindAsync([id], ct);
            if (record is null)
            {
                return OpenHandsNetProblemFactory.Create(
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
        });

        // SPEC-30 RF-004: merged memory view — org entries + project INDEX.
        group.MapGet("/{id:guid}/memory", async (
            Guid id, IAppConversationService svc,
            Infrastructure.Persistence.AppDbContext db,
            ISandboxService sandboxes, ISandboxExecutor executor,
            HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
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
                        key = m.Key, scope = "org",
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
        });

        // SPEC-25 RF-006: rescan on-demand — repete a descoberta do worker,
        // atualiza o evento repo_context e invalida o cache de skills.
        group.MapPost("/{id:guid}/context/rescan", async (
            Guid id, IAppConversationService svc,
            ISandboxService sandboxes,
            ISandboxExecutor executor,
            Application.Discovery.IRepoDiscoveryService discovery,
            IEventService events,
            IServiceProvider sp,
            HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            var sandbox = (await sandboxes.SearchAsync(
                    null, id, ct))
                .FirstOrDefault(s => s.Status == SandboxStatus.Running);
            if (sandbox is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 409, "Sandbox is not running");
            }

            var manifest = await discovery.DiscoverAsync(executor, sandbox, ct);
            await events.AppendAsync(id, new ConversationStateUpdateEvent
            {
                Key = "repo_context",
                Value = JsonSerializer.SerializeToElement(new
                {
                    instructions_files = manifest.InstructionFiles,
                    skills = manifest.Skills.Select(s => new
                    {
                        name = s.Name, path = s.Path,
                        description = s.Description,
                    }),
                    config = manifest.HasRepoConfig,
                    readme = manifest.ReadmePath,
                }, EventJson.Options),
            }, ct);
            sp.GetService<Infrastructure.Skills.RepoSkillProvider>()
                ?.Invalidate(sandbox.Id);
            return Results.Ok(manifest);
        });

        // SPEC-22 RF-004/005: compaction manual + ledger de contexto.
        // Runner ativo → o request evento entra na fila dele; idle →
        // compactação inline sobre o histórico persistido.
        group.MapPost("/{id:guid}/context/compact", async (
            Guid id, CompactContextRequest? body,
            IAppConversationService svc,
            Application.Agent.ICompactionService compactor,
            Application.Agent.IAgentRunnerHost host,
            IEventService events, HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            if (host.IsRunning(id))
            {
                await events.AppendAsync(id, new CondensationRequest
                {
                    Source = EventSource.User,
                    Trigger = "manual",
                    Instructions = body?.Instructions,
                }, ct);
                return Results.Accepted(value: new { queued = true });
            }

            var compacted = await compactor.CompactStoredAsync(
                id, Application.Agent.CompactionTrigger.Manual,
                body?.Instructions, ct);
            return Results.Accepted(value: new { queued = false, compacted });
        });

        group.MapGet("/{id:guid}/context", async (
            Guid id, IAppConversationService svc, IEventService events,
            HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
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
                    tokens_estimate = 0, window = (int?)null,
                    compactions = 0, boundary_event_id = (string?)null,
                    model = (string?)null, updated_at = (string?)null,
                });
        });

        // SPEC-34 RF-002: artifacts — ref opaco (ws:/art:) resolvido pelo
        // store ativo; Range habilitado p/ previews do client.
        group.MapGet("/{id:guid}/artifacts/{*ref}", async (
            Guid id, string @ref, IAppConversationService svc,
            Application.Artifacts.IArtifactStore store, HttpContext http,
            CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found");
            }

            var stream = await store.OpenAsync(id, @ref, ct);
            if (stream is null)
            {
                return OpenHandsNetProblemFactory.Create(
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
        });

        // Proxies git/files foram para GitEndpoints (SPEC-12).
        // skills/hooks reais em SkillEndpoints/HookEndpoints (SPEC-11).

        // start-tasks (polling).
        var tasks = app.MapGroup("/api/v1/app-conversations/start-tasks")
            .WithTags("Conversations");
        tasks.MapGet("/", BatchTasksAsync);
        tasks.MapGet("/search", SearchTasksAsync);
        tasks.MapGet("/count", async (
            string? status, IStartTaskService svc, CancellationToken ct) =>
            Results.Ok(await svc.CountAsync(ParseStatus(status), ct)));
        tasks.MapGet("/{id:guid}", async (
            Guid id, IStartTaskService svc, HttpContext http,
            CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } t
                ? Results.Ok(ToDto(t))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Start task {id} not found"));

        // SPEC-23 RF-005: catálogo de modelos para o ModelSelector da UI.
        app.MapGet("/api/v1/models", async (
            Application.Agent.IModelCatalog catalog,
            Microsoft.Extensions.Options.IOptions<
                Application.Agent.AgentLlmOptions> llmOpts,
            CancellationToken ct) =>
        {
            var models = await catalog.ListAsync(ct);
            return Results.Ok(new
            {
                @default = llmOpts.Value.Model,
                role_models = llmOpts.Value.RoleModels,
                models = models.Select(m => new
                {
                    id = m.Id, provider = m.Provider,
                    context_window = m.ContextWindow,
                    pricing = m.Pricing is { } p ? new
                    {
                        input_per_million = p.InputPerMillion,
                        output_per_million = p.OutputPerMillion,
                    } : null,
                }).ToArray(),
            });
        }).WithTags("Models");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateAppConversationRequest body,
        IAppConversationService svc, CancellationToken ct)
    {
        AppConversationStartTask task;
        try
        {
            task = await svc.CreateAsync(Map(body), ct);
        }
        catch (OpenHandsNET.Application.Identity.PlanLimitExceededException ex)
        {
            return PlanLimitProblem(ex.Error);
        }

        return Results.Created(
            $"/api/v1/app-conversations/start-tasks/{task.Id}", ToDto(task));
    }

    /// <summary>
    /// 403 problem+json com os campos extras do contrato de plano
    /// (SPEC-20 RF-003): detail fixo + limit_key/limit/used.
    /// </summary>
    internal static IResult PlanLimitProblem(
        OpenHandsNET.Application.Identity.PlanLimitError error) =>
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
        CancellationToken ct)
    {
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
        Guid id, SendMessageRequest body,
        IAppConversationService svc, IEventService events,
        OpenHandsNET.Application.Agent.IAgentRunnerHost runnerHost,
        HttpContext http, CancellationToken ct)
    {
        var error = await svc.ValidateSendMessageAsync(id, ct);
        if (error is not null)
        {
            return error switch
            {
                SendMessageError.NotFound => OpenHandsNetProblemFactory.Create(
                    http, 404, $"Conversation {id} not found"),
                SendMessageError.Gone => OpenHandsNetProblemFactory.Create(
                    http, 410, OpenHandsNetProblemFactory.ConversationGone),
                SendMessageError.ServiceUnavailable => OpenHandsNetProblemFactory.Create(
                    http, 503, OpenHandsNetProblemFactory.SandboxError),
                _ => OpenHandsNetProblemFactory.Create(
                    http, 409, OpenHandsNetProblemFactory.SandboxNotRunning),
            };
        }

        // Mensagem do usuário entra no stream como MessageEvent (V1).
        var content = body.Content.Select(el => (object)el).ToList();
        var message = new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = body.Role,
                content,
            }, EventJson.Options),
        };
        await events.AppendAsync(id, message, ct);
        if (body.Run)
        {
            await runnerHost.NotifyMessageAsync(id, ct);
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
        SwitchError.NotFound => OpenHandsNetProblemFactory.Create(
            http, 404, $"Conversation {id} not found"),
        SwitchError.InvalidProfile => OpenHandsNetProblemFactory.Create(
            http, 400, "unknown sandbox profile (small|medium|large)"),
        SwitchError.InvalidModel => OpenHandsNetProblemFactory.Create(
            http, 400, "model must be a non-empty string"),
        SwitchError.SandboxError => OpenHandsNetProblemFactory.Create(
            http, 502, OpenHandsNetProblemFactory.SandboxError),
        _ => OpenHandsNetProblemFactory.Create(
            http, 409, OpenHandsNetProblemFactory.SandboxNotRunning),
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
            b.Mode, b.Secrets);

    private static StartTaskStatus? ParseStatus(string? s) =>
        Enum.TryParse<StartTaskStatus>(s, ignoreCase: true, out var v) ? v : null;

    private static Application.Agent.PermissionDecision ParseDecision(string s) =>
        Enum.TryParse<Application.Agent.PermissionDecision>(
            s, ignoreCase: true, out var d)
            ? d : Application.Agent.PermissionDecision.Ask;

    private static StartTaskDto ToDto(AppConversationStartTask t) => new()
    {
        Id = t.Id.ToString(), Status = t.Status.ToString().ToUpperInvariant(),
        AppConversationId = t.AppConversationId?.ToString(),
        SandboxId = t.SandboxId?.ToString(), Detail = t.Detail,
        CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt,
    };

    private static AppConversationDto ToDto(AppConversation c) => new()
    {
        Id = c.Id.ToString(), Title = c.Title, SandboxId = c.SandboxId?.ToString(),
        SelectedRepository = c.SelectedRepository, AgentType = c.AgentType,
        LlmModel = c.LlmModel, SandboxProfile = c.SandboxProfile,
        ExecutionStatus = c.ExecutionStatus.ToString().ToUpperInvariant(),
        Tags = c.Tags, Pinned = c.Pinned, ConfirmationMode = c.ConfirmationMode,
        CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
    };
}

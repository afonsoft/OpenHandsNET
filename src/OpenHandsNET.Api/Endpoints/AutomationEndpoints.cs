using System.Collections.Concurrent;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Automations;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/automations</c> — CRUD owner-scoped + run-now + histórico +
/// hooks (SPEC-47 RF-003). <c>POST /api/v1/hooks/{token}</c> é o trigger
/// anônimo rate-limited (NFR: 404 em inválido, não 401).
/// </summary>
public static class AutomationEndpoints
{
    /// <summary>Maps automation + hook-trigger endpoints.</summary>
    public static IEndpointRouteBuilder MapAutomationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/automations").WithTags("Automations");

        g.MapGet("", async (
            ICurrentUserAccessor current, IAutomationService svc,
            CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : Results.Ok((await svc.ListAsync(u.UserId, ct))
                    .Select(ToDto).ToList()))
            .RequireAuthorization();

        g.MapPost("", async (
            AutomationUpsertRequest body, HttpContext ctx,
            ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
            IAutomationService svc, CancellationToken ct) =>
        {
            if (current.User is not { } u) { return Results.Unauthorized(); }
            var org = await resolver.ResolveAsync(u,
                ctx.Request.Headers["X-Org-Id"].ToString(), ct);
            var (info, error) = await svc.CreateAsync(
                ToUpsert(body), u.UserId, org?.OrgId, ct);
            return error is not null
                ? OpenHandsNetProblemFactory.Create(ctx, 400, error)
                : Results.Created(
                    $"/api/v1/automations/{info!.Id}", ToDto(info));
        }).RequireAuthorization();

        g.MapGet("/{id:guid}", async (
            Guid id, HttpContext ctx, ICurrentUserAccessor current,
            IAutomationService svc, CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : await svc.GetAsync(id, u.UserId, ct) is { } a
                    ? Results.Ok(ToDto(a))
                    : OpenHandsNetProblemFactory.Create(
                        ctx, 404, "automation not found"))
            .RequireAuthorization();

        g.MapPatch("/{id:guid}", async (
            Guid id, AutomationUpsertRequest body, HttpContext ctx,
            ICurrentUserAccessor current, IAutomationService svc,
            CancellationToken ct) =>
        {
            if (current.User is not { } u) { return Results.Unauthorized(); }
            var (info, error) = await svc.UpdateAsync(
                id, ToUpsert(body), u.UserId, ct);
            return error switch
            {
                "not found" => OpenHandsNetProblemFactory.Create(
                    ctx, 404, "automation not found"),
                not null => OpenHandsNetProblemFactory.Create(ctx, 400, error),
                _ => Results.Ok(ToDto(info!)),
            };
        }).RequireAuthorization();

        g.MapDelete("/{id:guid}", async (
            Guid id, ICurrentUserAccessor current, IAutomationService svc,
            CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : await svc.DeleteAsync(id, u.UserId, ct)
                    ? Results.NoContent()
                    : Results.NotFound())
            .RequireAuthorization();

        g.MapPost("/{id:guid}/run-now", async (
            Guid id, HttpContext ctx, ICurrentUserAccessor current,
            IAutomationService svc, CancellationToken ct) =>
        {
            if (current.User is not { } u) { return Results.Unauthorized(); }
            var (run, fired) = await svc.RunNowAsync(id, u.UserId, ct);
            return run switch
            {
                null => OpenHandsNetProblemFactory.Create(
                    ctx, 404, "automation not found"),
                { Status: "skipped" } when !fired =>
                    OpenHandsNetProblemFactory.Create(
                        ctx, 409, "automation run already in progress"),
                _ => (IResult)Results.Accepted(null, new
                {
                    run_id = run.Id,
                    conversation_id = run.ConversationId,
                    status = run.Status,
                }),
            };
        }).RequireAuthorization();

        g.MapGet("/{id:guid}/runs", async (
            Guid id, int? take, ICurrentUserAccessor current,
            IAutomationService svc, CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : Results.Ok((await svc.ListRunsAsync(
                        id, u.UserId, take ?? 50, ct))
                    .Select(ToRunDto).ToList()))
            .RequireAuthorization();

        // histórico global do owner — antes de /{id} para não colidir.
        g.MapGet("/runs", async (
            DateTimeOffset? from, DateTimeOffset? to, int? take,
            ICurrentUserAccessor current, IAutomationService svc,
            CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : Results.Ok((await svc.ListAllRunsAsync(
                        u.UserId, from, to, take ?? 50, ct))
                    .Select(ToRunDto).ToList()))
            .RequireAuthorization();

        g.MapPost("/{id:guid}/hooks", async (
            Guid id, HttpContext ctx, ICurrentUserAccessor current,
            IAutomationService svc, CancellationToken ct) =>
        {
            if (current.User is not { } u) { return Results.Unauthorized(); }
            var (hook, token) = await svc.CreateHookAsync(id, u.UserId, ct);
            return hook is null
                ? OpenHandsNetProblemFactory.Create(
                    ctx, 404, "automation not found")
                : Results.Created(
                    $"/api/v1/automations/hooks/{hook.Id}", new
                    {
                        id = hook.Id,
                        automation_id = hook.AutomationId,
                        token, // exibido uma única vez — só hash no banco
                        created_at = hook.CreatedAt,
                    });
        }).RequireAuthorization();

        g.MapDelete("/hooks/{hookId:guid}", async (
            Guid hookId, ICurrentUserAccessor current,
            IAutomationService svc, CancellationToken ct) =>
            current.User is not { } u
                ? Results.Unauthorized()
                : await svc.DeleteHookAsync(hookId, u.UserId, ct)
                    ? Results.NoContent()
                    : Results.NotFound())
            .RequireAuthorization();

        // Trigger anônimo — rate-limit duplo (token 10/min + IP 60/min);
        // token inválido → 404 (não vaza existência). SPEC-47 RF-003/NFR.
        app.MapPost("/api/v1/hooks/{token}", async (
            string token, HttpContext ctx, IAutomationService svc,
            CancellationToken ct) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            if (!HookRateLimiter.Allow(token)
                || !HookRateLimiter.AllowIp(ip))
            {
                return OpenHandsNetProblemFactory.Create(
                    ctx, 429, "rate limit exceeded");
            }

            var run = await svc.FireByTokenAsync(token, ip, ct);
            return run is null
                ? OpenHandsNetProblemFactory.Create(ctx, 404, "not found")
                : Results.Accepted(null, new
                {
                    run_id = run.Id,
                    conversation_id = run.ConversationId,
                    status = run.Status,
                });
        }).AllowAnonymous().WithTags("Automations");

        return app;
    }

    /// <summary>
    /// Token bucket in-memory — janela fixa por chave (SPEC-47 NFR:
    /// 10/min por token, 60/min por IP). Single-instance é o default;
    /// multi-replica herda o lock do scheduler como próximo passo.
    /// </summary>
    public static class HookRateLimiter
    {
        private sealed class Bucket(int limit)
        {
            private int _limit = limit;
            private int _used;
            private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;

            public bool Allow()
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    _windowStart = now;
                    _used = 0;
                }
                return ++_used <= _limit;
            }
        }

        private static readonly ConcurrentDictionary<string, Bucket> Tokens = new();
        private static readonly ConcurrentDictionary<string, Bucket> Ips = new();

        /// <summary>10 disparos/minuto por token.</summary>
        public static bool Allow(string token) =>
            Tokens.GetOrAdd(token, _ => new Bucket(10)).Allow();

        /// <summary>60 disparos/minuto por IP.</summary>
        public static bool AllowIp(string? ip) =>
            Ips.GetOrAdd(ip ?? "unknown", _ => new Bucket(60)).Allow();

        /// <summary>Test hook: limpa os buckets.</summary>
        public static void Reset()
        {
            Tokens.Clear();
            Ips.Clear();
        }
    }

    private static AutomationUpsert ToUpsert(AutomationUpsertRequest r) =>
        new(r.Name, r.Prompt, r.LlmModel, r.SelectedRepository,
            r.ScheduleKind, r.ScheduleTimeUtc, r.IntervalMinutes,
            r.Weekday, r.Enabled, r.NextRunAt);

    private static object ToDto(AutomationInfo a) => new
    {
        id = a.Id,
        org_id = a.OrgId,
        created_by_user_id = a.CreatedByUserId,
        name = a.Name,
        prompt = a.Prompt,
        llm_model = a.LlmModel,
        selected_repository = a.SelectedRepository,
        schedule_kind = a.ScheduleKind,
        schedule_time_utc = a.ScheduleTimeUtc,
        interval_minutes = a.IntervalMinutes,
        weekday = a.Weekday,
        enabled = a.Enabled,
        next_run_at = a.NextRunAt,
        last_run_at = a.LastRunAt,
        created_at = a.CreatedAt,
        updated_at = a.UpdatedAt,
    };

    private static object ToRunDto(AutomationRunInfo r) => new
    {
        id = r.Id,
        automation_id = r.AutomationId,
        started_at = r.StartedAt,
        finished_at = r.FinishedAt,
        status = r.Status,
        conversation_id = r.ConversationId,
        error = r.Error,
    };
}

/// <summary>Body JSON do CRUD de automations (snake_case).</summary>
public sealed record AutomationUpsertRequest(
    string? Name = null, string? Prompt = null, string? LlmModel = null,
    string? SelectedRepository = null, string? ScheduleKind = null,
    string? ScheduleTimeUtc = null, int? IntervalMinutes = null,
    int? Weekday = null, bool? Enabled = null,
    DateTimeOffset? NextRunAt = null);

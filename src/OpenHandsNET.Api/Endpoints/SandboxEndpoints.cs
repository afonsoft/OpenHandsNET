using System.Text.Json;

using OpenHandsNET.Api.DTOs;
using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/sandboxes</c> + <c>/api/v1/sandbox-specs</c> — paridade
/// <c>sandbox_router</c>/<c>sandbox_spec_router</c> upstream.
/// </summary>
public static class SandboxEndpoints
{
    private const string IdGuid = "/{id:guid}";
    /// <summary>Maps sandbox and sandbox-spec route groups.</summary>
    public static IEndpointRouteBuilder MapSandboxEndpoints(this IEndpointRouteBuilder app)
    {
        var sandboxes = app.MapGroup("/api/v1/sandboxes").WithTags("Sandboxes");

        sandboxes.MapGet("/", async (
            string? status, Guid? conversation_id,
            ISandboxService svc, CancellationToken ct) =>
        {
            var st = Enum.TryParse<SandboxStatus>(status, ignoreCase: true, out var v)
                ? v : (SandboxStatus?)null;
            return Results.Ok((await svc.SearchAsync(st, conversation_id, ct))
                .Select(ToDto));
        });

        sandboxes.MapGet("/search", async (
            string? status, Guid? conversation_id,
            ISandboxService svc, CancellationToken ct) =>
        {
            var st = Enum.TryParse<SandboxStatus>(status, ignoreCase: true, out var v)
                ? v : (SandboxStatus?)null;
            return Results.Ok((await svc.SearchAsync(st, conversation_id, ct))
                .Select(ToDto));
        });

        sandboxes.MapGet(IdGuid, async (
            Guid id, ISandboxService svc, HttpContext http, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } s
                ? Results.Ok(ToDto(s))
                : OpenHandsNetProblemFactory.Create(http, 404, $"Sandbox {id} not found"));

        sandboxes.MapPost("/", async (
            CreateSandboxRequest body, ISandboxService svc, CancellationToken ct) =>
        {
            var sb = await svc.StartAsync(
                body.SpecId ?? Guid.Empty, body.ConversationId, ct);
            return Results.Created($"/api/v1/sandboxes/{sb.Id}", ToDto(sb));
        });

        sandboxes.MapPost("/{id:guid}/pause", async (
            Guid id, ISandboxService svc, HttpContext http, CancellationToken ct) =>
            await Safe(() => svc.PauseAsync(id, ct), http));

        sandboxes.MapPost("/{id:guid}/resume", async (
            Guid id, ISandboxService svc, HttpContext http, CancellationToken ct) =>
            await Safe(() => svc.ResumeAsync(id, ct), http));

        sandboxes.MapDelete(IdGuid, async (
            Guid id, ISandboxService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // SPEC-40 RF-004: preview token p/ surfaces públicas (iframe/WS
        // de ttyd/code-server — cookie de sessão não cobre terceiros).
        sandboxes.MapPost("/{id:guid}/preview-token", async (
            Guid id, JsonElement body,
            ISandboxService svc,
            Routing.PreviewTokenService tokens,
            HttpContext http, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Sandbox {id} not found");
            }

            var port = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("port", out var p)
                && p.TryGetInt32(out var pv) ? pv : 8000;
            var (token, exp) = tokens.Mint(id, port);
            return Results.Ok(new { token, expires_at = exp, port });
        }).RequireAuthorization();

        // SPEC-113 RF-003: proxy same-origin para surfaces do sandbox —
        // funciona em qualquer RoutingMode (no "internal" o middleware
        // SandboxHostRouter é no-op). Auth: sessão (FallbackPolicy) ou
        // ?token= preview token (SPEC-40 RF-004) para links compartilhados.
        sandboxes.MapGet("/{id:guid}/surfaces/{port:int}/{**rest}", async (
            Guid id, int port, string? rest,
            HttpContext http, CancellationToken ct) =>
        {
            var tokens = http.RequestServices
                .GetRequiredService<Routing.PreviewTokenService>();
            if (http.User.Identity?.IsAuthenticated != true
                && !tokens.Validate(http.Request.Query["token"], id, port))
            {
                return Results.Unauthorized();
            }

            var svc = http.RequestServices
                .GetRequiredService<ISandboxService>();
            var proxy = http.RequestServices
                .GetRequiredService<Routing.SandboxSurfaceProxy>();

            var sandbox = await svc.GetAsync(id, ct);
            if (sandbox?.ContainerId is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Sandbox {id} not found");
            }

            await proxy.ProxyAsync(http,
                Routing.SandboxSurfaceProxy.UpstreamFor(
                    sandbox.ContainerId, port),
                string.Concat("/", rest));
            return Results.Empty;
        }).AllowAnonymous(); // auth dentro do handler: sessão OU ?token= preview

        // SPEC-24 RF-007: probe /alive v2 do agent in-container
        // (agent_version nulo = v1/sem agent → sem streaming).
        sandboxes.MapGet("/{id:guid}/agent", async (
            Guid id, ISandboxService svc, ISandboxExecutor executor,
            HttpContext http, CancellationToken ct) =>
        {
            var s = await svc.GetAsync(id, ct);
            if (s is null)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 404, $"Sandbox {id} not found");
            }

            var info = await executor.AgentInfoAsync(s, ct);
            return Results.Ok(new
            {
                agent_version = info?.AgentVersion,
                state = info?.State,
                uptime_s = info?.UptimeS,
                sessions = info?.Sessions,
                streaming = info is { AgentVersion: >= 2 },
            });
        });

        // SPEC-17 §5: cauda do log de provisionamento (blueprint).
        sandboxes.MapGet("/{id:guid}/blueprint-log", async (
            Guid id, ISandboxService svc, HttpContext http, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } s
                ? Results.Ok(new { blueprint_log = s.BlueprintLogTail ?? "" })
                : OpenHandsNetProblemFactory.Create(http, 404, $"Sandbox {id} not found"));

        // Secrets do sandbox: metadados apenas — valores nunca trafegam (SPEC-08).
        // SPEC-80 RF-001: `id` recebido para o param aparecer no OpenAPI.
        sandboxes.MapGet("/{id:guid}/settings/secrets", (Guid id) =>
            Results.Ok(Array.Empty<object>()));
        sandboxes.MapGet("/{id:guid}/settings/secrets/{name}",
            (Guid id, string name, HttpContext http) =>
                OpenHandsNetProblemFactory.Create(
                    http, 404, $"Secret {name} not found"));

        var specs = app.MapGroup("/api/v1/sandbox-specs").WithTags("Sandboxes");

        specs.MapGet(IdGuid, async (
            Guid id, ISandboxService svc, HttpContext http, CancellationToken ct) =>
            await svc.GetSpecAsync(id, ct) is { } s
                ? Results.Ok(ToDto(s))
                : OpenHandsNetProblemFactory.Create(
                    http, 404, $"Sandbox spec {id} not found"));

        specs.MapPost("/", async (
            SandboxSpecDto body, ISandboxService svc, CancellationToken ct) =>
        {
            var spec = new SandboxSpec(
                Guid.TryParse(body.Id, out var g) ? g : Guid.NewGuid(),
                body.Name, body.Image, body.MemoryMb, body.CpuLimit,
                body.TimeoutMinutes, body.Env, body.MountWorkspace,
                body.PidsLimit, body.Profile);
            await svc.UpsertSpecAsync(spec, ct);
            return Results.Created(
                $"/api/v1/sandbox-specs/{spec.Id}", ToDto(spec));
        });

        specs.MapDelete(IdGuid, (Guid id, HttpContext http) =>
            // specs são imutáveis no MVP — delete via upsert tombstone adiado.
            Results.NoContent());

        return app;
    }

    private static async Task<IResult> Safe(
        Func<Task<Domain.Sandbox.Sandbox>> action, HttpContext http)
    {
        try
        {
            return Results.Ok(ToDto(await action()));
        }
        catch (KeyNotFoundException ex)
        {
            return OpenHandsNetProblemFactory.Create(http, 404, ex.Message);
        }
    }

    private static SandboxDto ToDto(Domain.Sandbox.Sandbox s) => new()
    {
        Id = s.Id.ToString(),
        SpecId = s.SpecId.ToString(),
        ConversationId = s.ConversationId?.ToString(),
        Status = s.Status.ToString().ToUpperInvariant(),
        Pool = s.Pool,
        PoolKey = s.PoolKey,
        BlueprintStatus = s.BlueprintStatus,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
    };

    private static SandboxSpecDto ToDto(SandboxSpec s) => new()
    {
        Id = s.Id.ToString(),
        Name = s.Name,
        Image = s.Image,
        MemoryMb = s.MemoryMb,
        CpuLimit = s.CpuLimit,
        TimeoutMinutes = s.TimeoutMinutes,
        Env = s.Env?.ToDictionary(kv => kv.Key, kv => kv.Value),
        MountWorkspace = s.MountWorkspace,
        PidsLimit = s.PidsLimit,
        Profile = s.Profile,
    };
}
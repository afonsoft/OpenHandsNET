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

        sandboxes.MapGet("/{id:guid}", async (
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

        sandboxes.MapDelete("/{id:guid}", async (
            Guid id, ISandboxService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.NoContent();
        });

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
        sandboxes.MapGet("/{id:guid}/settings/secrets", () =>
            Results.Ok(Array.Empty<object>()));
        sandboxes.MapGet("/{id:guid}/settings/secrets/{name}",
            (string name, HttpContext http) =>
                OpenHandsNetProblemFactory.Create(
                    http, 404, $"Secret {name} not found"));

        var specs = app.MapGroup("/api/v1/sandbox-specs").WithTags("Sandboxes");

        specs.MapGet("/{id:guid}", async (
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

        specs.MapDelete("/{id:guid}", (Guid id, HttpContext http) =>
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
        Id = s.Id.ToString(), SpecId = s.SpecId.ToString(),
        ConversationId = s.ConversationId?.ToString(),
        Status = s.Status.ToString().ToUpperInvariant(),
        Pool = s.Pool,
        PoolKey = s.PoolKey,
        BlueprintStatus = s.BlueprintStatus,
        CreatedAt = s.CreatedAt, ExpiresAt = s.ExpiresAt,
    };

    private static SandboxSpecDto ToDto(SandboxSpec s) => new()
    {
        Id = s.Id.ToString(), Name = s.Name, Image = s.Image,
        MemoryMb = s.MemoryMb, CpuLimit = s.CpuLimit,
        TimeoutMinutes = s.TimeoutMinutes,
        Env = s.Env?.ToDictionary(kv => kv.Key, kv => kv.Value),
        MountWorkspace = s.MountWorkspace,
        PidsLimit = s.PidsLimit, Profile = s.Profile,
    };
}

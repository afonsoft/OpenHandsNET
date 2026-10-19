using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Indexing;
using OpenHandsNET.Infrastructure.Indexing;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>SPEC-60: repo index + ask sessions.</summary>
public static class IndexEndpoints
{
    public static WebApplication MapIndexEndpoints(this WebApplication app)
    {
        var repos = app.MapGroup("/api/v1/repos").RequireAuthorization();
        repos.MapGet("/", async (IRepoIndexService svc, CancellationToken ct)
            => Results.Ok(await svc.ListAsync(ct)));
        repos.MapPost("/", async (AddRepoRequest body,
            IRepoIndexService svc, IRepoIndexQueue queue,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Repository))
                return Results.BadRequest(new { error = "repository_required" });
            var info = await svc.AddAsync(
                body.Repository, body.Branch, ct);
            queue.Enqueue(info.Id);
            return Results.Accepted($"/api/v1/repos/{info.Id}", info);
        }).RequireAuthorization("perm:edit_org_settings");
        repos.MapGet("/{id:guid}", async (Guid id,
            IRepoIndexService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } info
                ? Results.Ok(info) : Results.NotFound());
        repos.MapPost("/{id:guid}/reindex", async (Guid id,
            IRepoIndexService svc, IRepoIndexQueue queue,
            CancellationToken ct) =>
        {
            if (!await svc.RequestReindexAsync(id, ct))
                return Results.NotFound();
            queue.Enqueue(id);
            return Results.Accepted();
        }).RequireAuthorization("perm:edit_org_settings");
        repos.MapDelete("/{id:guid}", async (Guid id,
            IRepoIndexService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization("perm:edit_org_settings");
        repos.MapGet("/{id:guid}/search", async (Guid id, string q,
            ICodeSearchStore search, CancellationToken ct) =>
            Results.Ok(await search.SearchAsync(id, q, 20, ct)));
        repos.MapGet("/{id:guid}/file", async (Guid id, string path,
            IRepoIndexService svc, CancellationToken ct) =>
            await svc.ReadFileAsync(id, path, ct) is { } c
                ? Results.Ok(new { path, content = c })
                : Results.NotFound());

        var ask = app.MapGroup("/api/v1/ask-sessions").RequireAuthorization();
        ask.MapGet("/", async (IAskService svc, CancellationToken ct)
            => Results.Ok(await svc.ListAsync(ct)));
        ask.MapPost("/", async (AskCreateRequest body,
            IAskService svc, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var s = await svc.CreateAsync(
                    body.Repository, body.Question, ct);
                return Results.Created(
                    $"/api/v1/ask-sessions/{s.Id}", s);
            }
            catch (IndexRequiredException)
            {
                return OpenHandsNetProblemFactory.Create(
                    http, 400, "index_required");
            }
            catch (IndexNotReadyException e)
            {
                return e.Status == RepoIndexStatus.Indexing
                    ? OpenHandsNetProblemFactory.Create(
                        http, 409, "index_in_progress")
                    : OpenHandsNetProblemFactory.Create(
                        http, 400, "index_required");
            }
        });
        ask.MapGet("/{id:guid}", async (Guid id,
            IAskService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } s
                ? Results.Ok(s) : Results.NotFound());
        ask.MapPost("/{id:guid}/messages", async (Guid id,
            AskMessageRequest body, IAskService svc,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(
                    await svc.AskAsync(id, body.Question, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        ask.MapPost("/{id:guid}/promote", async (Guid id,
            IAskService svc, CancellationToken ct) =>
        {
            try
            {
                var convId = await svc.PromoteAsync(id, ct);
                return Results.Ok(new { conversation_id = convId });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        return app;
    }

    public sealed record AddRepoRequest(
        string Repository, string? Branch);
    public sealed record AskCreateRequest(
        string Repository, string Question);
    public sealed record AskMessageRequest(string Question);
}

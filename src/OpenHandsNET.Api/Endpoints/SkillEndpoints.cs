using OpenHandsNET.Api.ProblemDetails;
using OpenHandsNET.Application.Skills;
using OpenHandsNET.Domain.Skills;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/skills</c> CRUD + skills resolvidas da conversa (SPEC-11 RF-001).
/// Owner real chega com SPEC-08; hoje tudo é single-tenant (owner nulo).
/// </summary>
public static class SkillEndpoints
{
    /// <summary>Maps skill endpoints.</summary>
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/skills", ListAsync).WithTags("Skills");
        app.MapPost("/api/v1/skills", CreateAsync).WithTags("Skills");
        app.MapGet("/api/v1/skills/{id:guid}", GetAsync).WithTags("Skills");
        app.MapPatch("/api/v1/skills/{id:guid}", UpdateAsync).WithTags("Skills");
        app.MapDelete("/api/v1/skills/{id:guid}", DeleteAsync).WithTags("Skills");
        app.MapGet("/api/v1/app-conversations/{id:guid}/skills", ForConversationAsync)
            .WithTags("Skills");
        return app;
    }

    private static async Task<IResult> ListAsync(
        ISkillService skills, CancellationToken ct) =>
        Results.Ok((await skills.ListAsync(null, null, ct))
            .Select(ToDto).ToList());

    private static async Task<IResult> CreateAsync(
        CreateSkillRequest req, ISkillService skills,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            var skill = await skills.CreateAsync(new Skill
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                Description = req.Description,
                ContentMd = req.ContentMd,
                Triggers = req.Triggers?.Select(t => new SkillTrigger(
                        ParseTrigger(t.Type), t.Value))
                    .ToList() ?? [],
                Source = SkillSource.User,
            }, ct);
            return Results.Created($"/api/v1/skills/{skill.Id}", ToDto(skill));
        }
        catch (InvalidOperationException ex)
        {
            return OpenHandsNetProblemFactory.Create(http, 400, ex.Message);
        }
    }

    private static async Task<IResult> GetAsync(
        Guid id, ISkillService skills, HttpContext http, CancellationToken ct) =>
        await skills.GetAsync(id, null, ct) is { } s
            ? Results.Ok(ToDto(s))
            : OpenHandsNetProblemFactory.Create(http, 404, "skill not found");

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateSkillDto req, ISkillService skills,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            var updated = await skills.UpdateAsync(id, null,
                new UpdateSkillRequest(req.Description, req.ContentMd,
                    req.Triggers?.Select(t => new SkillTrigger(
                        ParseTrigger(t.Type), t.Value)).ToList(),
                    req.Enabled), ct);
            return updated is { } s
                ? Results.Ok(ToDto(s))
                : OpenHandsNetProblemFactory.Create(
                    http, 404, "skill not found or read-only");
        }
        catch (InvalidOperationException ex)
        {
            return OpenHandsNetProblemFactory.Create(http, 400, ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, ISkillService skills, HttpContext http, CancellationToken ct) =>
        await skills.DeleteAsync(id, null, ct)
            ? Results.NoContent()
            : OpenHandsNetProblemFactory.Create(
                http, 404, "skill not found or read-only");

    private static async Task<IResult> ForConversationAsync(
        Guid id, ISkillService skills, CancellationToken ct) =>
        Results.Ok((await skills.ResolveForConversationAsync(
            id, null, null, null, null, ct)).Select(ToDto).ToList());

    private static SkillTriggerType ParseTrigger(string type) => type switch
    {
        "keyword" => SkillTriggerType.Keyword,
        "task" => SkillTriggerType.Task,
        _ => SkillTriggerType.Always,
    };

    private static object ToDto(Skill s) => new
    {
        id = s.Id,
        name = s.Name,
        description = s.Description,
        content_md = s.ContentMd,
        triggers = s.Triggers.Select(t => new
        {
            type = t.Type.ToString().ToLowerInvariant(),
            value = t.Value,
        }),
        source = s.Source.ToString().ToLowerInvariant(),
        enabled = s.Enabled,
        created_at = s.CreatedAt,
    };

    /// <summary>POST body.</summary>
    public sealed record CreateSkillRequest(
        string Name, string? Description, string ContentMd,
        IReadOnlyList<TriggerDto>? Triggers);

    /// <summary>PATCH body.</summary>
    public sealed record UpdateSkillDto(
        string? Description, string? ContentMd,
        IReadOnlyList<TriggerDto>? Triggers, bool? Enabled);

    /// <summary>Trigger clause DTO.</summary>
    public sealed record TriggerDto(string Type, string? Value);
}

using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Application.Identity;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// SPEC-26 RF-001/API Contract: preferências do usuário corrente
/// (<c>GET/PUT /users/me/preferences</c>) — merge raso em
/// <c>UserRecord.PreferencesJson</c>; chaves conhecidas hoje: <c>theme</c>.
/// </summary>
public static class UserEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(
        JsonSerializerDefaults.Web);

    /// <summary>Map <c>/api/v1/users/me</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/users/me").RequireAuthorization();

        g.MapGet("/preferences", async (
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            return Results.Ok(Parse(rec?.PreferencesJson));
        });

        g.MapPut("/preferences", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(
                    new { detail = "preferences must be a JSON object" });
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            // Merge raso: chaves do body sobrescrevem; demais preservadas.
            var merged = Parse(rec.PreferencesJson);
            foreach (var prop in body.EnumerateObject())
            {
                // whitelist — preferências nunca carregam payload arbitrário.
                if (prop.Name is "theme" or "sidebar_collapsed"
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    merged[prop.Name] = prop.Value.GetString()!;
                }
            }

            rec.PreferencesJson = JsonSerializer.Serialize(merged, JsonOpts);
            await db.SaveChangesAsync(ct);
            return Results.Ok(merged);
        });

        return app;
    }

    private static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer
                .Deserialize<Dictionary<string, string>>(json, JsonOpts)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
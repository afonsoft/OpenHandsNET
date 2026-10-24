using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenHandsNET.Domain.Configuration;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/settings</c> — settings é um blob JSON único.
/// Secrets CRUD vive em <see cref="SecretsEndpoints"/> (user-scoped,
/// RequireAuthorization) — as rotas legadas app-scoped aqui colidiam com
/// elas e produziam AmbiguousMatchException (SPEC-97/#218).
/// </summary>
public static class SettingsEndpoints
{
    private const string SettingsKey = "settings";

    /// <summary>Maps settings and secrets endpoints.</summary>
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/settings", async (
            AppDbContext db, CancellationToken ct) =>
        {
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingsKey, ct);
            return row is null
                ? Results.Ok(new Dictionary<string, object>())
                : Results.Text(row.Value, "application/json");
        }).WithTags("Settings");

        app.MapPost("/api/v1/settings", async (
            JsonElement body, AppDbContext db, CancellationToken ct) =>
        {
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingsKey, ct);
            var json = body.GetRawText();
            if (row is null)
            {
                db.AppSettings.Add(new AppSetting { Key = SettingsKey, Value = json });
            }
            else
            {
                row.Value = json;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Settings");

        return app;
    }
}
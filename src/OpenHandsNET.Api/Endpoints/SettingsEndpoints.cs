using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenHandsNET.Api.DTOs;
using OpenHandsNET.Domain.Configuration;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/settings</c> + <c>/api/v1/secrets</c> — settings é um blob JSON
/// único; secrets gravam valor no store mas só devolvem metadados.
/// </summary>
public static class SettingsEndpoints
{
    private const string SettingsKey = "settings";
    private const string SecretPrefix = "secret:";

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

        // Secrets: valor persistido, resposta expõe só metadados (nunca o valor).
        app.MapGet("/api/v1/secrets", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AppSettings
                .Where(s => s.Key.StartsWith(SecretPrefix))
                .Select(s => new { name = s.Key.Substring(SecretPrefix.Length) })
                .ToListAsync(ct))).WithTags("Secrets");

        app.MapGet("/api/v1/secrets/{name}", async (
            string name, AppDbContext db, CancellationToken ct) =>
            await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SecretPrefix + name, ct) is { } row
                ? Results.Ok(new SecretMetaDto
                {
                    Name = name, CreatedAt = DateTimeOffset.UnixEpoch,
                })
                : Results.NotFound()).WithTags("Secrets");

        app.MapPut("/api/v1/secrets/{name}", async (
            string name, JsonElement body, AppDbContext db, CancellationToken ct) =>
        {
            var value = body.TryGetProperty("value", out var v) ? v.GetString() : null;
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SecretPrefix + name, ct);
            if (row is null)
            {
                db.AppSettings.Add(new AppSetting
                {
                    Key = SecretPrefix + name, Value = value ?? "",
                });
            }
            else
            {
                row.Value = value ?? "";
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Secrets");

        app.MapDelete("/api/v1/secrets/{name}", async (
            string name, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SecretPrefix + name, ct) is { } row)
            {
                db.AppSettings.Remove(row);
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).WithTags("Secrets");

        return app;
    }
}

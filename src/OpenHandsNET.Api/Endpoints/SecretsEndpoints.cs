using Microsoft.AspNetCore.Mvc;

using OpenHandsNET.Api.Policies;
using OpenHandsNET.Application.Contracts;
using OpenHandsNET.Application.Identity;

namespace OpenHandsNET.Api.Endpoints;

/// <summary>
/// <c>/api/v1/secrets</c> — user-scoped secrets CRUD (SPEC-33 RF-003).
/// List returns NAMES only; values write-only, resolved at the sandbox edge.
/// </summary>
public static class SecretsEndpoints
{
    /// <summary>Maps secrets endpoints.</summary>
    public static IEndpointRouteBuilder MapSecretsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/secrets");

        g.MapGet("", async (
            ICurrentUserAccessor current, ISecretStore store,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            // SPEC-97: shape `[{name}]` — contrato que o client já consome
            // (o stub app-scoped removido respondia igual).
            var names = await store.ListNamesAsync(
                "user", user.UserId.ToString(), ct);
            return Results.Ok(names.Select(n => new { name = n }));
        }).RequireAuthorization()
            .RequireScope("secrets:write");

        g.MapPut("/{name}", async (
            string name, [FromBody] SecretSetRequest req,
            ICurrentUserAccessor current, ISecretStore store,
            IAuditService audit, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(req.Value)
                || !IsValidEnvName(name))
            {
                return Results.BadRequest(
                    new { detail = "invalid name or empty value" });
            }

            await store.SetAsync(
                "user", user.UserId.ToString(), name, req.Value, ct);
            await audit.RecordAsync(
                "secret.set", user.UserId.ToString(), null,
                "secret", name, cancellationToken: ct);
            return Results.Ok(new { name });
        }).RequireAuthorization()
            .RequireScope("secrets:write");

        g.MapDelete("/{name}", async (
            string name, ICurrentUserAccessor current,
            ISecretStore store, IAuditService audit,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var removed = await store.DeleteAsync(
                "user", user.UserId.ToString(), name, ct);
            if (removed)
            {
                await audit.RecordAsync(
                    "secret.delete", user.UserId.ToString(), null,
                    "secret", name, cancellationToken: ct);
            }

            return removed ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization()
            .RequireScope("secrets:write");

        return app;
    }

    /// <summary>Env-var-safe name (letters/digits/underscore, no leading digit).</summary>
    private static bool IsValidEnvName(string name) =>
        name.Length is > 0 and <= 128
        && !char.IsDigit(name[0])
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>PUT body — plaintext travels once over TLS, stored encrypted.</summary>
    public sealed record SecretSetRequest(string Value);
}
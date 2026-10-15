using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.RuntimeApi;

/// <summary>
/// SPEC-35 RF-002: openhandsnet-runtime-api — fronte REST interno do engine
/// de sandbox. Nunca recebe/persiste secrets de usuário: o Api resolve
/// spec/credenciais e manda só o payload materializado (env pronto).
/// Entrypoint explícito (namespace) — evita o <c>Program</c> global que
/// colidiria com o do Api em testes compartilhados.
/// </summary>
public sealed class RuntimeApiProgram
{
    /// <summary>App entrypoint.</summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<SandboxOptions>(
            builder.Configuration.GetSection("Sandbox"));
        builder.Services.AddSingleton(_ => DockerClientFactory.Create());
        builder.Services.AddSingleton<WorkspaceProvisioner>();
        builder.Services.AddSingleton<DockerNetworkPolicy>();
        builder.Services.AddSingleton<SquidConfigWriter>();
        builder.Services.AddSingleton<EgressProxyService>();
        builder.Services.AddSingleton<DockerSandboxEngine>();
        builder.Services.AddSingleton<ISandboxEngine>(
            sp => sp.GetRequiredService<DockerSandboxEngine>());
        // Warm pool mora aqui em modo split — engine local (IsRemote=false).
        builder.Services.AddSingleton<WarmPoolService>();
        builder.Services.AddHostedService(
            sp => sp.GetRequiredService<WarmPoolService>());

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(
                    System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
        });

        var app = builder.Build();

        // X-Runtime-Key gate — interno, fail-closed quando a key não está
        // configurada (o runtime-api não deve rodar aberto em rede alguma).
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path == "/healthz")
            {
                await next();
                return;
            }

            var expected = ctx.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options
                    .IOptions<SandboxOptions>>().Value.RemoteApiKey;
            var provided = ctx.Request.Headers["X-Runtime-Key"]
                .FirstOrDefault();
            if (expected is null || provided != expected)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        var rt = app.MapGroup("/runtime/v1");

        rt.MapPost("/sandboxes", async (
            EngineCreateRequest req, ISandboxEngine engine,
            CancellationToken ct) =>
            Results.Ok(await engine.CreateAndStartAsync(req, ct)));

        rt.MapGet("/sandboxes", async (
            bool? pool, ISandboxEngine engine, CancellationToken ct) =>
            Results.Ok(pool == true
                ? await engine.ListPoolAsync(ct)
                : await engine.ListManagedAsync(ct)));

        rt.MapGet("/sandboxes/{id}", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
            await engine.InspectAsync(id, ct) is { } info
                ? Results.Ok(info) : Results.NotFound());

        rt.MapPost("/sandboxes/{id}/pause", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
        {
            await engine.PauseAsync(id, ct);
            return Results.NoContent();
        });

        rt.MapPost("/sandboxes/{id}/unpause", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
        {
            await engine.UnpauseAsync(id, ct);
            return Results.NoContent();
        });

        rt.MapPost("/sandboxes/{id}/start", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
        {
            await engine.StartAsync(id, ct);
            return Results.NoContent();
        });

        rt.MapPost("/sandboxes/{id}/stop", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
        {
            await engine.StopAsync(id, ct);
            return Results.NoContent();
        });

        rt.MapDelete("/sandboxes/{id}", async (
            string id, ISandboxEngine engine, CancellationToken ct) =>
        {
            await engine.RemoveAsync(id, ct);
            return Results.NoContent();
        });

        rt.MapPost("/warm/claim", async (
            WarmClaimRequest req, ISandboxEngine engine,
            CancellationToken ct) =>
            await engine.ClaimWarmAsync(req, ct) is { } claim
                ? Results.Ok(claim) : Results.NoContent());

        app.Run();
    }
}

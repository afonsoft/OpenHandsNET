using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Infrastructure.Conversations;
using OpenHandsNET.Infrastructure.Persistence;

using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-45 RF-005: contrato de <c>POST /{id}/uploads</c> — campo
/// obrigatório, extensão allowlist, cap 10MB e 409 sem sandbox.
/// Guardas rodam antes do lookup do sandbox (400/413 sem Docker).
/// </summary>
public sealed class Spec45UploadEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ohn-spec45-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec45UploadEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
            });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<Guid> SeedConversationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = new ConversationRecord
        {
            Id = Guid.NewGuid(),
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    private static MultipartFormDataContent Upload(
        string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        return form;
    }

    // AC: campo 'file' ausente → 400.
    [Fact]
    public async Task Upload_Requires_File_Field()
    {
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/uploads",
            new MultipartFormDataContent());
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // AC: extensão fora da allowlist → 400 (antes do sandbox lookup).
    [Theory]
    [InlineData("evil.exe")]
    [InlineData("run.bat")]
    [InlineData("noext")]
    public async Task Upload_Rejects_Disallowed_Extension(string name)
    {
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/uploads",
            Upload(name, new byte[16]));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // AC: >10MB → 413.
    [Fact]
    public async Task Upload_Over_10MB_Returns_413()
    {
        var big = new byte[10 * 1024 * 1024 + 1];
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{Guid.NewGuid()}/uploads",
            Upload("big.pdf", big));
        resp.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // AC: conversa sem sandbox → 409 sandbox_not_running.
    [Fact]
    public async Task Upload_Without_Sandbox_Returns_409()
    {
        var convId = await SeedConversationAsync();
        var resp = await _http.PostAsync(
            $"/api/v1/app-conversations/{convId}/uploads",
            Upload("notes.md", "hi"u8.ToArray()));
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
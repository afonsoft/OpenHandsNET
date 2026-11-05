using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OpenHandsNET.Application.Agent;
using OpenHandsNET.Application.Sandbox;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent.Tools;
using OpenHandsNET.Infrastructure.Persistence;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-128: tools <c>context_*</c>/<c>memory_*</c> DB-backed —
/// roundtrip, limites, isolamento por conversa e busca.
/// </summary>
public sealed class BuiltinContextDbTests : IDisposable
{
    private static readonly Domain.Sandbox.Sandbox TestSandbox = new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;

    public BuiltinContextDbTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn)
            .UseSnakeCaseNamingConvention());
        _sp = sc.BuildServiceProvider();
        var db = _sp.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }

    private ToolCallContext Ctx(Guid? conversationId = null) => new(
        conversationId ?? Guid.NewGuid(), TestSandbox,
        new FakeSandboxExecutor(), new FakeEventService(), _sp,
        TimeSpan.FromSeconds(30));

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Context_Save_Get_Roundtrip()
    {
        var conv = Guid.NewGuid();
        var save = await BuiltinContextDb.ContextSave().Handler(
            Ctx(conv), Args("""{"title":"decisão","body":"usar X"}"""),
            CancellationToken.None);
        save.IsError.ShouldBeFalse();
        var id = save.Observation.GetProperty("id").GetGuid();

        var get = await BuiltinContextDb.ContextGet().Handler(
            Ctx(conv), Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);
        get.IsError.ShouldBeFalse();
        get.Observation.GetRawText().ShouldContain("usar X");
    }

    [Fact]
    public async Task Context_List_And_Summary_Only_Own_Conversation()
    {
        var conv = Guid.NewGuid();
        var other = Guid.NewGuid();
        await BuiltinContextDb.ContextSave().Handler(Ctx(conv),
            Args("""{"title":"a","body":"b1"}"""), CancellationToken.None);
        await BuiltinContextDb.ContextSave().Handler(Ctx(other),
            Args("""{"title":"z","body":"b2"}"""), CancellationToken.None);

        var list = await BuiltinContextDb.ContextList().Handler(
            Ctx(conv), Args("{}"), CancellationToken.None);
        list.Observation.GetProperty("count").GetInt32().ShouldBe(1);
        var summary = await BuiltinContextDb.ContextSummary().Handler(
            Ctx(conv), Args("{}"), CancellationToken.None);
        summary.Observation.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Context_Save_Rejects_Oversized_Body()
    {
        var body = new string('x', 33 * 1024);
        var res = await BuiltinContextDb.ContextSave().Handler(
            Ctx(), Args($$"""{"title":"big","body":"{{body}}"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
        res.Observation.GetRawText().ShouldContain("32KB");
    }

    [Fact]
    public async Task Context_Delete_Removes_Note()
    {
        var conv = Guid.NewGuid();
        var save = await BuiltinContextDb.ContextSave().Handler(
            Ctx(conv), Args("""{"title":"t","body":"b"}"""),
            CancellationToken.None);
        var id = save.Observation.GetProperty("id").GetGuid();
        var del = await BuiltinContextDb.ContextDelete().Handler(
            Ctx(conv), Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);
        del.IsError.ShouldBeFalse();
        var again = await BuiltinContextDb.ContextDelete().Handler(
            Ctx(conv), Args($$"""{"id":"{{id}}"}"""),
            CancellationToken.None);
        again.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Memory_Set_Get_Search_Roundtrip()
    {
        var conv = Guid.NewGuid();
        await BuiltinContextDb.MemorySet().Handler(Ctx(conv),
            Args("""{"key":"stack","value":"dotnet10"}"""),
            CancellationToken.None);
        // upsert
        await BuiltinContextDb.MemorySet().Handler(Ctx(conv),
            Args("""{"key":"stack","value":"dotnet11"}"""),
            CancellationToken.None);

        var get = await BuiltinContextDb.MemoryGet().Handler(
            Ctx(conv), Args("""{"key":"stack"}"""),
            CancellationToken.None);
        get.Observation.GetRawText().ShouldContain("dotnet11");

        var search = await BuiltinContextDb.MemorySearch().Handler(
            Ctx(conv), Args("""{"query":"dotnet"}"""),
            CancellationToken.None);
        search.Observation.GetProperty("count").GetInt32().ShouldBe(1);

        var miss = await BuiltinContextDb.MemoryGet().Handler(
            Ctx(conv), Args("""{"key":"nope"}"""),
            CancellationToken.None);
        miss.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Memory_Set_Rejects_Oversized_Value()
    {
        var res = await BuiltinContextDb.MemorySet().Handler(
            Ctx(), Args($$"""{"key":"k","value":"{{new string('v', 9 * 1024)}}"}"""),
            CancellationToken.None);
        res.IsError.ShouldBeTrue();
        res.Observation.GetRawText().ShouldContain("8KB");
    }

    public void Dispose()
    {
        _sp.Dispose();
        _conn.Dispose();
    }
}
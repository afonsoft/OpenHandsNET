using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OpenHandsNET.Application.Artifacts;
using OpenHandsNET.Application.Events;
using OpenHandsNET.Domain.Events;
using OpenHandsNET.Infrastructure.Agent.Deltas;
using OpenHandsNET.Infrastructure.Agent.Tools;
using OpenHandsNET.Infrastructure.Artifacts;

using Shouldly;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-34 AC: deltas efêmeros (bus, batcher 16ms/64B, nunca persistidos,
/// item_id compartilhado com o durável), artifact store workspace/S3,
/// OutputPolicy via IArtifactStore com redação de segredos.
/// </summary>
public sealed class Spec34DeltasArtifactsTests
{
    private const string TextScript = """
        [{"text":"hello world"},{"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
        """;

    // ---------- DeltaBatcher ----------

    [Fact]
    public void Batcher_Flushes_At_64_Bytes()
    {
        var bus = new CollectingBus();
        var b = new DeltaBatcher(Guid.NewGuid(), "item-1", bus);
        b.Add(new string('x', 70));
        bus.Frames.Count.ShouldBe(1);
        bus.Frames[0].Text.Length.ShouldBe(70);
        bus.Frames[0].Seq.ShouldBe(0);
    }

    [Fact]
    public void Batcher_Manual_Flush_Emits_Remainder_With_Seq()
    {
        var bus = new CollectingBus();
        var conv = Guid.NewGuid();
        var b = new DeltaBatcher(conv, "item-1", bus);
        b.Add("a");
        b.Add("b");
        b.Flush();
        b.Add("c");
        b.Flush();
        bus.Frames.Select(f => f.Text).ShouldBe(["ab", "c"]);
        bus.Frames.Select(f => f.Seq).ShouldBe([0, 1]);
        bus.Frames.ShouldAllBe(f => f.ItemId == "item-1");
    }

    [Fact]
    public async Task Batcher_Time_Threshold_Flushes()
    {
        var bus = new CollectingBus();
        var b = new DeltaBatcher(Guid.NewGuid(), "i", bus);
        b.Add("small");
        bus.Frames.ShouldBeEmpty();
        // Simula a passagem de tempo: força via flush manual do limite —
        // o batcher avalia a janela a cada Add; 2º add após >16ms flusha.
        await Task.Delay(20);
        b.Add("x");
        bus.Frames.Count.ShouldBe(1);
    }

    // ---------- AgentActivityBus ----------

    [Fact]
    public async Task Bus_Delivers_Only_To_Matching_Conversation()
    {
        var bus = new AgentActivityBus();
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Inscrição é lazy — inicia a enumeração antes de publicar.
        var firstA = bus.SubscribeAsync(convA, cts.Token)
            .FirstAsync(cts.Token).AsTask();
        var firstB = bus.SubscribeAsync(convB, cts.Token)
            .FirstAsync(cts.Token).AsTask();
        await Task.Delay(50, cts.Token);

        bus.Publish(convA, new DeltaFrame("i1", 0, "a-text"));
        bus.Publish(convB, new DeltaFrame("i2", 0, "b-text"));

        (await firstA).Text.ShouldBe("a-text");
        (await firstB).Text.ShouldBe("b-text");
    }

    [Fact]
    public async Task Bus_Drops_When_No_Subscriber()
    {
        var bus = new AgentActivityBus();
        var conv = Guid.NewGuid();
        bus.Publish(conv, new DeltaFrame("i", 0, "dropped"));
        // Subscribe depois do publish — não deve ver o frame antigo.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var seen = bus.SubscribeAsync(conv, cts.Token)
            .Take(1).ToListAsync(cts.Token);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await seen);
    }

    // ---------- Runner e2e ----------

    [Fact]
    public async Task Runner_Publishes_Deltas_To_Bus_Never_Persists()
    {
        var bus = new CollectingBus();
        await using var fx = new AgentRunnerFixture(
            TextScript, activityBus: bus);
        await fx.Runner.RunAsync(CancellationToken.None);

        // Deltas chegaram ao bus com item_id próprio.
        bus.Frames.ShouldNotBeEmpty();
        var itemId = bus.Frames[0].ItemId;
        bus.Frames.ShouldAllBe(f => f.ItemId == itemId);
        string.Concat(bus.Frames.Select(f => f.Text))
            .ShouldContain("hello");

        // Nada de StreamingDeltaEvent no store durável; o durável traz
        // o mesmo item_id (supersedes).
        fx.Events.Events.ShouldNotContain(e =>
            e.GetType() == typeof(StreamingDeltaEvent));
        var durable = fx.Events.Events
            .Where(e => e.ItemId == itemId).ToList();
        durable.ShouldNotBeEmpty();
        durable.ShouldContain(e =>
            e.GetType() == typeof(MessageEvent)
            || e.GetType() == typeof(ActionEvent));
    }

    [Fact]
    public async Task Runner_Without_Bus_Still_Persists_Durable()
    {
        await using var fx = new AgentRunnerFixture(TextScript);
        await fx.Runner.RunAsync(CancellationToken.None);
        fx.Events.Events.ShouldNotContain(e =>
            e.GetType() == typeof(StreamingDeltaEvent));
        fx.Events.Events.OfType<MessageEvent>()
            .ShouldContain(m => m.Source == EventSource.Agent
                && m.ItemId != null);
    }

    // ---------- WorkspaceArtifactStore ----------

    [Fact]
    public async Task Workspace_Put_Open_Roundtrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ohn-art-{Guid.NewGuid():N}");
        var store = new WorkspaceArtifactStore(
            Options.Create(new ArtifactOptions { WorkspaceRoot = root }),
            NullLogger<WorkspaceArtifactStore>.Instance);
        var conv = Guid.NewGuid();
        var data = Encoding.UTF8.GetBytes("artifact payload");

        var r = await store.PutAsync(conv, "obs.txt", data, CancellationToken.None);
        r.ShouldStartWith($"ws:{conv}/");

        await using var s = await store.OpenAsync(conv, r, CancellationToken.None);
        s.ShouldNotBeNull();
        using var reader = new StreamReader(s);
        (await reader.ReadToEndAsync()).ShouldBe("artifact payload");
    }

    [Fact]
    public async Task Workspace_Rejects_Traversal_And_Other_Conversation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ohn-art-{Guid.NewGuid():N}");
        var store = new WorkspaceArtifactStore(
            Options.Create(new ArtifactOptions { WorkspaceRoot = root }),
            NullLogger<WorkspaceArtifactStore>.Instance);
        var conv = Guid.NewGuid();
        var other = Guid.NewGuid();
        var r = await store.PutAsync(conv, "f.txt",
            Encoding.UTF8.GetBytes("x"), CancellationToken.None);

        // Ref de outra conversa → null; traversal → null.
        (await store.OpenAsync(other, r, CancellationToken.None)).ShouldBeNull();
        (await store.OpenAsync(conv, $"ws:{conv}/../escape",
            CancellationToken.None)).ShouldBeNull();
        (await store.OpenAsync(conv, $"ws:{conv}/sub/dir",
            CancellationToken.None)).ShouldBeNull();
        (await store.OpenAsync(conv, "bogus", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Workspace_DeleteConversation_Removes_Artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ohn-art-{Guid.NewGuid():N}");
        var store = new WorkspaceArtifactStore(
            Options.Create(new ArtifactOptions { WorkspaceRoot = root }),
            NullLogger<WorkspaceArtifactStore>.Instance);
        var conv = Guid.NewGuid();
        var r = await store.PutAsync(conv, "f.txt",
            Encoding.UTF8.GetBytes("x"), CancellationToken.None);
        await store.DeleteConversationAsync(conv, CancellationToken.None);
        (await store.OpenAsync(conv, r, CancellationToken.None)).ShouldBeNull();
    }

    // ---------- S3ArtifactStore (backend fake) ----------

    [Fact]
    public async Task S3_Put_Uses_Bucket_Key_And_Sse()
    {
        var backend = new FakeS3Backend();
        var opt = new ArtifactS3Options { Bucket = "bucket-x" };
        var store = new S3ArtifactStore(backend, opt,
            NullLogger<S3ArtifactStore>.Instance);
        var conv = Guid.NewGuid();

        var r = await store.PutAsync(conv, "obs.txt",
            Encoding.UTF8.GetBytes("payload"), CancellationToken.None);
        r.ShouldStartWith("art:");
        backend.LastBucket.ShouldBe("bucket-x");
        backend.LastKey.ShouldStartWith($"{conv}/");
        backend.LastSse.ShouldBeTrue();
        backend.Objects[backend.LastKey!].ShouldBe("payload");
    }

    [Fact]
    public async Task S3_Open_Resolves_Art_Ref_And_Misses_Others()
    {
        var backend = new FakeS3Backend();
        var store = new S3ArtifactStore(backend, new ArtifactS3Options(),
            NullLogger<S3ArtifactStore>.Instance);
        var conv = Guid.NewGuid();
        var r = await store.PutAsync(conv, "a.bin",
            Encoding.UTF8.GetBytes("bin"), CancellationToken.None);

        await using var s = await store.OpenAsync(conv, r, CancellationToken.None);
        s.ShouldNotBeNull();
        (await new StreamReader(s).ReadToEndAsync()).ShouldBe("bin");

        // ws: refs não pertencem a este provider; chave errada → null.
        (await store.OpenAsync(conv, $"ws:{conv}/x",
            CancellationToken.None)).ShouldBeNull();
        (await store.OpenAsync(conv, "art:missing",
            CancellationToken.None)).ShouldBeNull();
        (await store.OpenAsync(Guid.NewGuid(), r,
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task S3_Delete_Removes_Conversation_Prefix()
    {
        var backend = new FakeS3Backend();
        var store = new S3ArtifactStore(backend, new ArtifactS3Options(),
            NullLogger<S3ArtifactStore>.Instance);
        var conv = Guid.NewGuid();
        await store.PutAsync(conv, "a",
            new byte[] { 1 }, CancellationToken.None);
        await store.PutAsync(conv, "b",
            new byte[] { 2 }, CancellationToken.None);
        backend.Objects.Count.ShouldBe(2);
        await store.DeleteConversationAsync(conv, CancellationToken.None);
        backend.Objects.ShouldBeEmpty();
    }

    // ---------- OutputPolicy → IArtifactStore ----------

    [Fact]
    public async Task Policy_Truncation_Emits_Opaque_Ref_And_Redacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ohn-art-{Guid.NewGuid():N}");
        var store = new WorkspaceArtifactStore(
            Options.Create(new ArtifactOptions { WorkspaceRoot = root }),
            NullLogger<WorkspaceArtifactStore>.Instance);
        var policy = new OutputPolicy(Options.Create(
            new OpenHandsNET.Application.Agent.AgentLlmOptions()));
        var conv = Guid.NewGuid();
        var big = new string('y', 70_000) + "{{secret:TOKEN}}";
        var obs = JsonSerializer.SerializeToElement(new { output = big });

        var result = await policy.ApplyAsync("bash", "act-1", obs,
            sandbox: null, executor: null, conv, store,
            CancellationToken.None);

        result.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        var artifactRef = result.GetProperty("artifact_ref").GetString()!;
        artifactRef.ShouldStartWith($"ws:{conv}/");
        // O payload truncado preserva head+tail.
        result.GetProperty("output").GetString()!.Length
            .ShouldBeLessThan(big.Length);
        // Payload armazenado foi redigido (placeholder mascarado).
        await using var s = await store.OpenAsync(
            conv, artifactRef, CancellationToken.None);
        var stored = await new StreamReader(s!).ReadToEndAsync();
        stored.ShouldContain("***");
        stored.ShouldNotContain("{{secret:TOKEN}}");
    }

    // ---------- fakes ----------

    private sealed class CollectingBus : IAgentActivityBus
    {
        public List<DeltaFrame> Frames { get; } = [];

        public void Publish(Guid conversationId, DeltaFrame frame) =>
            Frames.Add(frame);

        public IAsyncEnumerable<DeltaFrame> SubscribeAsync(
            Guid conversationId, CancellationToken ct) =>
            AsyncEnumerable.Empty<DeltaFrame>();
    }

    private sealed class FakeS3Backend : IS3Backend
    {
        public Dictionary<string, string> Objects { get; } = new(
            StringComparer.Ordinal);
        public string? LastBucket { get; private set; }
        public string? LastKey { get; private set; }
        public bool LastSse { get; private set; }

        public Task PutAsync(string bucket, string key, byte[] data,
            bool sse, CancellationToken ct)
        {
            LastBucket = bucket;
            LastKey = key;
            LastSse = sse;
            Objects[key] = Encoding.UTF8.GetString(data);
            return Task.CompletedTask;
        }

        public Task<Stream?> GetAsync(string bucket, string key,
            CancellationToken ct) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var v)
                ? new MemoryStream(Encoding.UTF8.GetBytes(v)) : null);

        public Task DeletePrefixAsync(string bucket, string prefix,
            CancellationToken ct)
        {
            foreach (var k in Objects.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList())
            {
                Objects.Remove(k);
            }

            return Task.CompletedTask;
        }
    }
}
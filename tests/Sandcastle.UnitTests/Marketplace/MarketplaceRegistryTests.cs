using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Marketplace;
using Sandcastle.Domain.Marketplace;
using Sandcastle.Infrastructure.Marketplace;

namespace Sandcastle.UnitTests.Marketplace;

/// <summary>
/// SPEC-148 RF-001/RF-005: manifest parse/validation, sha256 verification,
/// HTTPS+AllowedHosts enforcement, offline bundled registry.
/// </summary>
public sealed class MarketplaceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"sc-mkt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private MarketplaceRegistry NewRegistry(
        MarketplaceOptions? options = null,
        IHttpClientFactory? http = null) =>
        new(Options.Create(options ?? new MarketplaceOptions()),
            http ?? new StaticHttpClientFactory(null),
            NullLogger<MarketplaceRegistry>.Instance);

    private string WriteRegistry(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "marketplace.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string SkillMd =
        "---\nname: test-skill\ndescription: d\n---\n\nbody";

    private static string Manifest(string entry) =>
        $$"""{"version":1,"entries":[{{entry}}]}""";

    private static string SkillEntry(
        string id = "test-skill", string version = "1.0.0",
        string? minApp = null, string? perms = null,
        string? checksum = null, string content = "") =>
        $$"""
        {"id":"{{id}}","name":"{{id}}","version":"{{version}}",
         "kind":"skill","source":"embedded"
         {{(minApp is null ? "" : $",\"min_app_version\":\"{minApp}\"")}}
         {{(perms is null ? "" : $",\"permissions_required\":[{perms}]")}}
         {{(checksum is null ? "" : $",\"checksum\":\"{checksum}\"")}}
         ,"files":[{"path":"SKILL.md","content":{{System.Text.Json.JsonSerializer.Serialize(content == "" ? SkillMd : content)}}}]}
        """;

    [Fact]
    public async Task Bundled_Registry_Loads_Offline()
    {
        var reg = NewRegistry();
        var entries = await reg.ListEntriesAsync(CancellationToken.None);
        entries.Count.ShouldBeGreaterThanOrEqualTo(10);
        entries.ShouldContain(e => e.Id == "iterate");
        entries.ShouldContain(e => e.Kind == MarketplaceEntryKind.Bundle);
        entries.ShouldContain(e => e.Kind == MarketplaceEntryKind.McpServer);
        entries.ShouldContain(e => e.Kind == MarketplaceEntryKind.Agent);
        entries.ShouldContain(e => e.Kind == MarketplaceEntryKind.Hook);
    }

    [Fact]
    public async Task Parse_Valid_Manifest_RoundTrips()
    {
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryFile = WriteRegistry(Manifest(
                SkillEntry("alpha") + "," + SkillEntry("beta"))),
        });
        var entries = await reg.ListEntriesAsync(CancellationToken.None);
        entries.Select(e => e.Id).ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task Parse_Invalid_Json_Throws_InvalidManifest()
    {
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryFile = WriteRegistry("{ not json"),
        });
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ListEntriesAsync(CancellationToken.None));
        ex.Code.ShouldBe("invalid_manifest");
    }

    [Fact]
    public async Task Parse_Drops_Invalid_Entries_Keeps_Valid()
    {
        var bad = """{"id":"BAD ID!","name":"x","version":"1.0.0","kind":"skill","files":[{"path":"a","content":"b"}]}""";
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryFile = WriteRegistry(
                Manifest(bad + "," + SkillEntry("good"))),
        });
        var entries = await reg.ListEntriesAsync(CancellationToken.None);
        entries.Select(e => e.Id).ShouldBe(["good"]);
    }

    [Fact]
    public void Validate_Rejects_Unsafe_File_Path()
    {
        var e = new MarketplaceEntry
        {
            Id = "x",
            Name = "x",
            Version = "1.0.0",
            Kind = MarketplaceEntryKind.Skill,
            Files = [new MarketplaceFile
            {
                Path = "../escape.md", Content = "x",
            }],
        };
        MarketplaceRegistry.Validate(e, out var error).ShouldBeFalse();
        error.ShouldContain("unsafe");
    }

    [Fact]
    public void CidrMatches_IPv4()
    {
        MarketplaceRegistry.CidrMatches(
            "10.0.0.0/8", IPAddress.Parse("10.44.1.2")).ShouldBeTrue();
        MarketplaceRegistry.CidrMatches(
            "10.0.0.0/8", IPAddress.Parse("11.0.0.1")).ShouldBeFalse();
        MarketplaceRegistry.CidrMatches(
            "192.168.1.10/32", IPAddress.Parse("192.168.1.10"))
            .ShouldBeTrue();
        MarketplaceRegistry.CidrMatches(
            "garbage/8", IPAddress.Parse("10.0.0.1")).ShouldBeFalse();
    }

    [Fact]
    public async Task Remote_Requires_Https()
    {
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryUrl = "http://registry.example.com/m.json",
            AllowedHosts = ["registry.example.com"],
        });
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ListEntriesAsync(CancellationToken.None));
        ex.Code.ShouldBe("registry_forbidden");
    }

    [Fact]
    public async Task Remote_Requires_AllowedHost()
    {
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryUrl = "https://evil.example.com/m.json",
            AllowedHosts = ["registry.example.com"],
        });
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ListEntriesAsync(CancellationToken.None));
        ex.Code.ShouldBe("registry_forbidden");
    }

    [Fact]
    public async Task Remote_NoAllowedHosts_Denied()
    {
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryUrl = "https://registry.example.com/m.json",
        });
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ListEntriesAsync(CancellationToken.None));
        ex.Code.ShouldBe("registry_forbidden");
    }

    [Fact]
    public async Task Remote_AllowedHost_Fetches()
    {
        var json = Manifest(SkillEntry("remote-skill"));
        var http = new StaticHttpClientFactory(json);
        var reg = NewRegistry(new MarketplaceOptions
        {
            RegistryUrl = "https://registry.example.com/m.json",
            AllowedHosts = ["registry.example.com"],
        }, http);
        var entries = await reg.ListEntriesAsync(CancellationToken.None);
        entries.Select(e => e.Id).ShouldBe(["remote-skill"]);
    }

    [Fact]
    public async Task RemoteFile_Requires_Sha256()
    {
        var entry = new MarketplaceEntry
        {
            Id = "r",
            Name = "r",
            Version = "1.0.0",
            Kind = MarketplaceEntryKind.Skill,
            Files = [new MarketplaceFile
            {
                Path = "SKILL.md", Url = "https://h.example.com/s.md",
            }],
        };
        var reg = NewRegistry();
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ResolveFileContentAsync(
                entry, entry.Files[0], CancellationToken.None));
        ex.Code.ShouldBe("checksum_required");
    }

    [Fact]
    public async Task RemoteFile_Sha256_Mismatch_Rejected()
    {
        var entry = new MarketplaceEntry
        {
            Id = "r",
            Name = "r",
            Version = "1.0.0",
            Kind = MarketplaceEntryKind.Skill,
            Files = [new MarketplaceFile
            {
                Path = "SKILL.md",
                Url = "https://h.example.com/s.md",
                Sha256 = new string('0', 64),
            }],
        };
        var reg = NewRegistry(new MarketplaceOptions
        {
            AllowedHosts = ["h.example.com"],
        }, new StaticHttpClientFactory("tampered content"));
        var ex = await Should.ThrowAsync<MarketplaceRegistryException>(
            () => reg.ResolveFileContentAsync(
                entry, entry.Files[0], CancellationToken.None));
        ex.Code.ShouldBe("checksum_mismatch");
    }

    [Fact]
    public async Task Inline_Sha256_Verified()
    {
        var content = "hello";
        var good = MarketplaceRegistry.Sha256Hex(content);
        var entry = new MarketplaceEntry
        {
            Id = "e",
            Name = "e",
            Version = "1.0.0",
            Kind = MarketplaceEntryKind.Skill,
            Files = [new MarketplaceFile
            {
                Path = "SKILL.md", Content = content, Sha256 = good,
            }],
        };
        var reg = NewRegistry();
        (await reg.ResolveFileContentAsync(
            entry, entry.Files[0], CancellationToken.None))
            .ShouldBe(content);
    }

    /// <summary>Canned-response HTTP factory for remote-registry tests.</summary>
    private sealed class StaticHttpClientFactory(string? body)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(
            new StaticHandler(body));

        private sealed class StaticHandler(string? body)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        body ?? "{}", Encoding.UTF8, "application/json"),
                });
        }
    }
}
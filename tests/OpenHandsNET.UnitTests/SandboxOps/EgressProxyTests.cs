using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenHandsNET.Infrastructure.Sandbox;
using OpenHandsNET.Sandboxing;

namespace OpenHandsNET.UnitTests.SandboxOps;

/// <summary>
/// SPEC-19: squid.conf/allowlist.txt generation (fail-closed, normalized
/// domains) + proxy env contract. Docker paths cobertos no teste de
/// integração opt-in.
/// </summary>
public sealed class EgressProxyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ohn-egress-{Guid.NewGuid():N}");

    private SquidConfigWriter Writer(string? allow = null) => new(
        Options.Create(new SandboxOptions
        {
            WorkspaceRoot = Path.Combine(_dir, "ws"),
            EgressConfigDir = Path.Combine(_dir, "egress"),
            EgressAllow = allow,
        }),
        NullLogger<SquidConfigWriter>.Instance);

    [Fact]
    public void ProxyEnv_Carries_Proxy_And_NoProxy()
    {
        var env = EgressProxyService.ProxyEnv();
        env.ShouldContain(e =>
            e == $"HTTP_PROXY=http://{EgressProxyService.ProxyName}:3128");
        env.ShouldContain(e => e == $"https_proxy={EgressProxyService.ProxyUrl}");
        env.ShouldContain(e => e == "NO_PROXY=localhost,127.0.0.1,::1");
    }

    [Fact]
    public void NormalizeDomains_Prefixes_Dot_And_Strips()
    {
        SquidConfigWriter.NormalizeDomains(
            "github.com; HTTPS://API.Anthropic.COM/v1 , *.nuget.org")
            .ShouldBe([".api.anthropic.com", ".github.com", ".nuget.org"]);
    }

    [Fact]
    public void NormalizeDomains_Empty_And_Invalid()
    {
        SquidConfigWriter.NormalizeDomains(null).ShouldBeEmpty();
        SquidConfigWriter.NormalizeDomains("").ShouldBeEmpty();
        SquidConfigWriter.NormalizeDomains(" ; , ").ShouldBeEmpty();
    }

    [Fact]
    public void Write_Empty_Allowlist_Is_Deny_All()
    {
        var w = Writer(null);
        w.Write();
        File.ReadAllText(Path.Combine(w.ConfigDir, "allowlist.txt"))
            .ShouldContain("empty allowlist");
        File.ReadAllText(Path.Combine(w.ConfigDir, "squid.conf"))
            .ShouldContain("http_access deny all");
    }

    [Fact]
    public void Write_Renders_Dstdomain_Acl()
    {
        var w = Writer("github.com;nuget.org");
        var hash = w.Write();
        File.ReadAllText(Path.Combine(w.ConfigDir, "allowlist.txt"))
            .ShouldBe(".github.com\n.nuget.org\n");
        var conf = File.ReadAllText(Path.Combine(w.ConfigDir, "squid.conf"));
        conf.ShouldContain("dstdomain \"/etc/squid/allowlist.txt\"");
        conf.ShouldContain("http_access deny CONNECT !SSL_ports");
        // Estável: mesmo conteúdo → mesmo hash.
        Writer("github.com;nuget.org").Write().ShouldBe(hash);
    }

    [Fact]
    public void Write_Hash_Changes_With_Allowlist()
    {
        Writer("github.com").Write()
            .ShouldNotBe(Writer("github.com;nuget.org").Write());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

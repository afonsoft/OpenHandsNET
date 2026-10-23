using System.Net;

namespace OpenHandsNET.IntegrationTests;

/// <summary>
/// SPEC-94: mirror <c>/framework-assets/{stem}/{ext}</c> — serve assets de
/// <c>_framework</c> sem extensão na URL (+ <c>?enc=b64</c>) para proxies
/// corporativos que bloqueiam .wasm/.dat/.dll.
/// </summary>
public sealed class FrameworkAssetsEndpointsTests
    : IClassFixture<SystemEndpointsTests.AppFactory>
{
    private readonly HttpClient _client;

    public FrameworkAssetsEndpointsTests(SystemEndpointsTests.AppFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<(string Stem, string Ext)?> FindRealAssetAsync()
    {
        // Descobre um asset _framework real do webroot (fingerprintado).
        var asmDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        var webroot = Path.GetFullPath(
            Path.Combine(asmDir, "..", "..", "..", "src",
                "OpenHandsNET.Client", "bin"));
        if (!Directory.Exists(webroot))
        {
            return null;
        }
        var framework = Directory.EnumerateFiles(
                webroot, "*.wasm", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                webroot, "*.dat", SearchOption.AllDirectories))
            .Where(f => f.Contains("_framework"))
            .FirstOrDefault();
        if (framework is null)
        {
            return null;
        }
        var name = Path.GetFileName(framework);
        var dot = name.LastIndexOf('.');
        return (name[..dot], name[(dot + 1)..]);
    }

    [Fact]
    public async Task Mirror_ServesRealAsset()
    {
        var asset = await FindRealAssetAsync();
        if (asset is null)
        {
            return; // webroot sem _framework neste leg — nada a servir
        }
        var res = await _client.GetAsync(
            $"/framework-assets/{asset.Value.Stem}/{asset.Value.Ext}");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Mirror_B64_ReturnsDecodablePayload()
    {
        var asset = await FindRealAssetAsync();
        if (asset is null)
        {
            return;
        }
        var res = await _client.GetAsync(
            $"/framework-assets/{asset.Value.Stem}/{asset.Value.Ext}?enc=b64");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        var bytes = Convert.FromBase64String(
            (await res.Content.ReadAsStringAsync()).Trim());
        bytes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Mirror_RejectsTraversalAndBadNames()
    {
        (await _client.GetAsync("/framework-assets/..%2f..%2fetc/wasm"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync("/framework-assets/evil%20name/wasm"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mirror_RejectsExtOutsideWhitelist()
    {
        (await _client.GetAsync("/framework-assets/dotnet/exe"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync("/framework-assets/dotnet/sh"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mirror_UnknownAsset_404()
    {
        (await _client.GetAsync("/framework-assets/nonexistent99/wasm"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
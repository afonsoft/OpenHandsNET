using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace OpenHandsNET.IntegrationTests.Api;

/// <summary>
/// SPEC-51 RF-002/RF-003: golden snapshot do contrato público.
/// <para>
/// O documento vivo em <c>GET /openapi/v1.json</c> é normalizado
/// (ordenação recursiva de propriedades, sem ruído de servidor) e
/// comparado com <c>Golden/openapi-v1.json</c>. Qualquer endpoint
/// adicionado/removido/alterado quebra o teste — o diff precisa de
/// revisão humana (o snapshot <b>é</b> o contrato).
/// </para>
/// <para>
/// Refresh consciente: rodar a suíte com
/// <c>OPENHANDSNET_UPDATE_GOLDEN=1</c> regrava o fixture a partir do
/// documento atual e o teste passa — commitar o fixture atualizado
/// junto com a mudança de endpoint.
/// </para>
/// </summary>
public sealed class Spec51OpenApiContractTests
{
    private const string UpdateGoldenEnv = "OPENHANDSNET_UPDATE_GOLDEN";

    [Fact]
    public async Task OpenApi_V1_Responds_And_Describes_Api()
    {
        using var factory = CreateFactory();
        using var http = factory.CreateClient();

        var res = await http.GetAsync("/openapi/v1.json");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await res.Content.ReadFromJsonAsync<JsonObject>();
        doc.ShouldNotBeNull();
        doc!["openapi"]!.GetValue<string>().ShouldStartWith("3.");
        doc["paths"]!.AsObject().Count.ShouldBeGreaterThan(50);
        doc["paths"]!.AsObject().ContainsKey("/api/v1/config")
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Every_Operation_Has_Summary()
    {
        using var factory = CreateFactory();
        using var http = factory.CreateClient();
        var doc = await http.GetFromJsonAsync<JsonObject>(
            "/openapi/v1.json");

        var missing = new List<string>();
        foreach (var (path, item) in doc!["paths"]!.AsObject())
        {
            foreach (var (method, op) in item!.AsObject())
            {
                if (op!["summary"]?.GetValue<string>()
                        is not { Length: > 0 })
                {
                    missing.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        missing.ShouldBeEmpty(
            "operações sem summary: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Contract_Matches_Golden_Snapshot()
    {
        using var factory = CreateFactory();
        using var http = factory.CreateClient();
        var raw = await http.GetStringAsync("/openapi/v1.json");
        var normalized = Normalize(raw);

        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Golden", "openapi-v1.json");
        if (Environment.GetEnvironmentVariable(UpdateGoldenEnv) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            File.WriteAllText(fixturePath, normalized);
            var sourcePath = FindSourceFixturePath();
            if (sourcePath is not null)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(sourcePath)!);
                File.WriteAllText(sourcePath, normalized);
            }
        }

        File.Exists(fixturePath).ShouldBeTrue(
            $"golden ausente — rode com {UpdateGoldenEnv}=1 para gerar");
        var expected = await File.ReadAllTextAsync(fixturePath);
        if (!string.Equals(expected, normalized, StringComparison.Ordinal))
        {
            var received = fixturePath + ".received";
            await File.WriteAllTextAsync(received, normalized);
            Assert.Fail(
                "OpenAPI contract drift — diff entre Golden/openapi-v1.json " +
                $"e {received}. Se a mudança é intencional, rode " +
                $"{UpdateGoldenEnv}=1 e commite o fixture (revisão " +
                "obrigatória).\n" + DiffSummary(expected, normalized));
        }
    }

    [Fact]
    public async Task Snapshot_Detects_Removed_Path()
    {
        // Teste do teste (DoD): remover um path do documento quebra a
        // comparação — prova de que o guard realmente detecta drift.
        using var factory = CreateFactory();
        using var http = factory.CreateClient();
        var raw = await http.GetStringAsync("/openapi/v1.json");
        var doc = JsonNode.Parse(raw)!.AsObject();
        doc["paths"]!.AsObject().Remove("/api/v1/config");
        Normalize(raw).ShouldNotBe(Normalize(doc.ToJsonString()));
    }

    [Fact]
    public async Task Scalar_Explorer_Off_By_Default_In_Production()
    {
        // Explorer é opt-in fora de Development; o JSON continua público.
        // Sem rota Scalar, /scalar/v1 cai no SPA fallback (index.html) —
        // o marcador do explorer ("Scalar API Reference") não aparece.
        using var factory = CreateFactory(explorer: false);
        using var http = factory.CreateClient();
        var body = await (await http.GetAsync("/scalar/v1"))
            .Content.ReadAsStringAsync();
        body.ShouldNotContain("Scalar API Reference");
        (await http.GetAsync("/openapi/v1.json")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scalar_Explorer_Responds_When_Enabled()
    {
        using var factory = CreateFactory(explorer: true);
        using var http = factory.CreateClient();
        var res = await http.GetAsync("/scalar/v1");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync())
            .ShouldContain("Scalar API Reference");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool explorer = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Database:Provider", "Sqlite");
            b.UseSetting("Database:ConnectionString",
                $"Data Source={Path.Combine(Path.GetTempPath(),
                    $"ohn-spec51-{Guid.NewGuid():N}.db")}");
            b.UseSetting("Observability:OtlpEndpoint", string.Empty);
            b.UseSetting("Sandbox:InstanceOwner",
                $"test-{Guid.NewGuid():N}");
            b.UseSetting("Auth:Provider", "none");
            b.UseSetting("Api:OpenApiExplorer", explorer ? "true" : "false");
        });

    /// <summary>
    /// Ordenação recursiva de chaves + drop de <c>servers</c> (ruído de
    /// ambiente). Resultado canônico e diff-legível.
    /// </summary>
    internal static string Normalize(string raw)
    {
        var node = JsonNode.Parse(raw)!;
        node.AsObject().Remove("servers");
        return Sort(node).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }) + "\n";
    }

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject obj => SortedObject(obj),
        JsonArray arr => SortedArray(arr),
        _ => node.DeepClone(),
    };

    private static JsonObject SortedObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in obj
            .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sorted[key] = value is null ? null : Sort(value);
        }

        return sorted;
    }

    private static JsonArray SortedArray(JsonArray arr)
    {
        var sorted = new JsonArray();
        foreach (var item in arr)
        {
            sorted.Add(item is null ? null : Sort(item));
        }

        return sorted;
    }

    private static string DiffSummary(string expected, string actual)
    {
        var exp = JsonNode.Parse(expected)!["paths"]!.AsObject()
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var act = JsonNode.Parse(actual)!["paths"]!.AsObject()
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var added = act.Except(exp).Order().ToList();
        var removed = exp.Except(act).Order().ToList();
        return $"paths adicionados: [{string.Join(", ", added)}]; " +
            $"removidos: [{string.Join(", ", removed)}]";
    }

    /// <summary>
    /// Em update mode também escreve no fixture-fonte do repo (o teste
    /// roda do bin/ — sobe até achar o .csproj do projeto de teste).
    /// </summary>
    private static string? FindSourceFixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.csproj").Length > 0)
            {
                return Path.Combine(dir.FullName,
                    "Golden", "openapi-v1.json");
            }

            dir = dir.Parent;
        }

        return null;
    }
}

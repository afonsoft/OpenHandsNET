using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenHandsNET.Application.Agent;
using OpenHandsNET.Domain.Sandbox;
using OpenHandsNET.Infrastructure.Agent;
using OpenHandsNET.Infrastructure.Agent.Browser;
using OpenHandsNET.Infrastructure.Agent.Tools;

namespace OpenHandsNET.UnitTests.Agent;

/// <summary>
/// SPEC-18: <c>browser</c> tool — schema, validation and dispatch into
/// <see cref="IBrowserService"/>; service-level argument validation.
/// </summary>
public sealed class BrowserToolTests
{
    private static Domain.Sandbox.Sandbox FakeSandbox() => new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private static ToolCallContext Ctx(IServiceProvider services) => new(
        Guid.NewGuid(), FakeSandbox(), new FakeSandboxExecutor(),
        new FakeEventService(), services, TimeSpan.FromSeconds(30));

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value);

    private sealed class FakeBrowserService(BrowserResult result) : IBrowserService
    {
        public BrowserActRequest? LastRequest { get; private set; }

        public Task<BrowserResult> ActAsync(
            Guid conversationId, BrowserActRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }

        public Task CloseAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<string?> CaptureStorageStateAsync(
            Guid conversationId, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task SetStorageStateAsync(
            Guid conversationId, string storageStateJson,
            CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Browser_Is_Registered_And_Risky()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .AddSingleton<IToolRegistry, ToolRegistry>()
            .BuildServiceProvider();
        var tool = services.GetRequiredService<IToolRegistry>().Find("browser");
        tool.ShouldNotBeNull();
        tool.Risky.ShouldBeTrue();
        tool.Schema.Description.ShouldNotContain("not available");
    }

    [Fact]
    public async Task Browser_Without_Service_Returns_Error()
    {
        var tool = BuiltinTools.Browser();
        var result = await tool.Handler(Ctx(new ServiceCollection()
                .BuildServiceProvider()),
            Args(new { action = "goto", url = "https://example.com" }),
            CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.Observation.GetProperty("error").GetString()!
            .ShouldContain("unavailable");
    }

    [Fact]
    public async Task Browser_Unknown_Action_Returns_Error()
    {
        var tool = BuiltinTools.Browser();
        var services = new ServiceCollection()
            .AddSingleton<IBrowserService>(new FakeBrowserService(
                new BrowserResult(true, null, null, null, null, null)))
            .BuildServiceProvider();
        var result = await tool.Handler(Ctx(services),
            Args(new { action = "teleport" }), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.Observation.GetProperty("error").GetString()!
            .ShouldContain("unknown browser action");
    }

    [Fact]
    public async Task Browser_Goto_Without_Url_Returns_Service_Error()
    {
        var tool = BuiltinTools.Browser();
        var services = new ServiceCollection()
            .AddSingleton<IBrowserService>(new FakeBrowserService(
                BrowserResult.Fail("goto requires url")))
            .BuildServiceProvider();
        var result = await tool.Handler(Ctx(services),
            Args(new { action = "goto" }), CancellationToken.None);
        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Browser_Success_Returns_Screenshot_Payload()
    {
        var browser = new FakeBrowserService(new BrowserResult(
            true, "https://example.com", "Example", "aGk=", "page text", null));
        var services = new ServiceCollection()
            .AddSingleton<IBrowserService>(browser).BuildServiceProvider();
        var tool = BuiltinTools.Browser();
        var result = await tool.Handler(Ctx(services),
            Args(new { action = "goto", url = "https://example.com" }),
            CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("screenshot_base64").GetString()
            .ShouldBe("aGk=");
        result.Observation.GetProperty("url").GetString()
            .ShouldBe("https://example.com");
        browser.LastRequest!.Action.ShouldBe(BrowserActionKind.Goto);
        browser.LastRequest.Url.ShouldBe("https://example.com");
    }

    [Theory]
    [InlineData(BrowserActionKind.Goto, null, null, null, "goto requires url")]
    [InlineData(BrowserActionKind.Click, null, null, null, "click requires selector")]
    [InlineData(BrowserActionKind.Type, null, "sel", null, "type requires text")]
    [InlineData(BrowserActionKind.Type, null, null, "x", "type requires selector")]
    public void Validate_Rejects_Missing_Args(
        BrowserActionKind action, string? url, string? selector, string? text,
        string expected)
    {
        PlaywrightBrowserService.Validate(
            new BrowserActRequest(action, url, selector, text))
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(BrowserActionKind.Scroll)]
    [InlineData(BrowserActionKind.Back)]
    [InlineData(BrowserActionKind.Forward)]
    [InlineData(BrowserActionKind.Screenshot)]
    [InlineData(BrowserActionKind.Content)]
    public void Validate_Accepts_Parameterless_Actions(BrowserActionKind action)
    {
        PlaywrightBrowserService.Validate(new BrowserActRequest(action))
            .ShouldBeNull();
    }
}

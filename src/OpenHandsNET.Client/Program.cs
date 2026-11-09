using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using OpenHandsNET.Client;
using OpenHandsNET.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// SPEC-132 RF-005: o HttpClient principal carrega o handler de 401 —
// resposta Unauthorized fora /api/v1/auth/* força /auth via AuthStateService.
builder.Services.AddScoped(sp =>
{
    var handler = new AuthRedirectHandler(
        async () => await sp.GetRequiredService<AuthStateService>()
            .HandleUnauthorizedAsync())
    {
        InnerHandler = new HttpClientHandler(),
    };
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    };
});
builder.Services.AddScoped<AuthStateService>();
builder.Services.AddScoped<OpenHandsNetApiClient>();
builder.Services.AddScoped<AdminApiClient>();
builder.Services.AddScoped<AdminState>();
builder.Services.AddScoped<MonacoInterop>();
builder.Services.AddScoped(sp => new ConversationHubClient(
    new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/hubs/conversations")));
builder.Services.AddScoped<ConversationStateService>();
builder.Services.AddScoped<AttentionInboxState>();
// SPEC-26: theme / toasts / palette / markdown.
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<PaletteService>();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<ChatNotificationsService>();
builder.Services.AddScoped<MarkdownService>();
// SPEC-46: dicionários JSON i18n/{lang}.json + fallback + Changed.
builder.Services.AddScoped<LocalizationService>();

var host = builder.Build();
// Carrega pt-BR/en-US + locale persistido antes do primeiro render.
await host.Services.GetRequiredService<LocalizationService>()
    .InitializeAsync();
// SPEC-132 RF-003: probe de sessão antes do primeiro render —
// sem flash de /auth em provider=none nem flash da shell em oidc.
await host.Services.GetRequiredService<AuthStateService>()
    .InitializeAsync();
await host.RunAsync();
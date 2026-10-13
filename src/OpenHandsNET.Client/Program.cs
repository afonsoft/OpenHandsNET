using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OpenHandsNET.Client;
using OpenHandsNET.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<OpenHandsNetApiClient>();
builder.Services.AddScoped(sp => new ConversationHubClient(
    new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/hubs/conversations")));
builder.Services.AddScoped<ConversationStateService>();

await builder.Build().RunAsync();

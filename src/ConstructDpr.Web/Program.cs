using ConstructDpr.Client;
using ConstructDpr.UI;
using ConstructDpr.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The web app is served by the server, so the server address is simply this page's address.
builder.Services.AddScoped<BrowserPlatform>();
builder.Services.AddScoped<IFileSaver>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<IKeyValueStore>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddScoped<ISessionStore>(sp => sp.GetRequiredService<BrowserPlatform>());
builder.Services.AddConstructDprUi(new HostOptions { FixedServerUrl = builder.HostEnvironment.BaseAddress, Platform = "web" });

await builder.Build().RunAsync();

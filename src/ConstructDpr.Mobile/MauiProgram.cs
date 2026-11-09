using ConstructDpr.Client;
using ConstructDpr.UI.Services;

namespace ConstructDpr.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton<MauiPlatform>();
        builder.Services.AddSingleton<IFileSaver>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<IKeyValueStore>(sp => sp.GetRequiredService<MauiPlatform>());
        builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<MauiPlatform>());
        // The phone asks for the server address once (Login screen) and remembers it.
        builder.Services.AddConstructDprUi(new HostOptions { FixedServerUrl = null, Platform = "android" });
        return builder.Build();
    }
}

using ConstructDpr.Client;
using ConstructDpr.Shared.Api;
using Microsoft.Extensions.DependencyInjection;

namespace ConstructDpr.UI.Services;

public static class ServiceSetup
{
    /// <summary>Registers the client and the shared screens' services. The host adds IFileSaver, IKeyValueStore and ISessionStore.</summary>
    public static IServiceCollection AddConstructDprUi(this IServiceCollection services, HostOptions host)
    {
        services.AddSingleton(host);
        services.AddScoped(sp => new AuthSession(sp.GetRequiredService<ISessionStore>()));
        services.AddScoped<IConstructDprApi>(sp => new RemoteApi(sp.GetRequiredService<AuthSession>()));
        services.AddScoped<ILiveUpdates>(sp => new LiveClient(sp.GetRequiredService<AuthSession>()));
        services.AddScoped<Notifier>();
        services.AddScoped<AppState>();
        return services;
    }
}

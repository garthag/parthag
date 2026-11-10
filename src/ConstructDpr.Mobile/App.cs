using ConstructDpr.Mobile.Pages;
using ConstructDpr.Mobile.Services;

namespace ConstructDpr.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new ContentPage
        {
            BackgroundColor = Ui.BrandDark,
            Content = new ActivityIndicator { IsRunning = true, Color = Colors.White, VerticalOptions = LayoutOptions.Center },
        }) { Title = "CONSTRUCT DPR" };
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            bool signedIn;
            try { signedIn = await AppState.StartAsync(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { signedIn = AppState.Session.IsSignedIn; }
            window.Page = signedIn ? new AppShell() : new LoginPage();
        });
        return window;
    }
}

using ConstructDpr.UI;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace ConstructDpr.Mobile;

/// <summary>One full-screen BlazorWebView showing the shared screens (ConstructDpr.UI).</summary>
public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        BackgroundColor = Color.FromArgb("#0B2236");
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        Content = view;
    }
}

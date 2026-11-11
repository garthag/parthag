using ConstructDpr.Mobile.Services;

namespace ConstructDpr.Mobile.Pages;

/// <summary>Server address (asked once), then sign in or create an account.</summary>
public sealed class LoginPage : ContentPage
{
    private readonly Entry _server = Ui.Field(null, "http://192.168.1.20:5080", Keyboard.Url);
    private readonly Entry _name = Ui.Field(null, "Your name");
    private readonly Entry _email = Ui.Field(null, "E-mail", Keyboard.Email);
    private readonly Entry _password = new() { Placeholder = "Password (8+ characters)", IsPassword = true, FontSize = 16, BackgroundColor = Colors.White };
    private readonly View _nameRow;
    private readonly Button _go;
    private readonly Button _toggle;
    private readonly ActivityIndicator _busy = new() { Color = Ui.Brand };
    private bool _register;

    public LoginPage()
    {
        BackgroundColor = Ui.BrandDark;
        _nameRow = Ui.Labelled("Your name", _name);
        _nameRow.IsVisible = false;
        _go = Ui.Primary("Sign in", async (_, _) => await GoAsync());
        _toggle = Ui.Plain("Create an account instead", (_, _) => Toggle());
        _server.Text = string.IsNullOrEmpty(AppState.Session.ServerUrl) ? "" : AppState.Session.ServerUrl;
        _email.Text = AppState.Session.Email;

        var card = Ui.Card(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Label { Text = "CONSTRUCT", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Ui.Brand },
                new Label { Text = "DPR · Daily progress & site management", FontSize = 14, TextColor = Ui.Accent, FontAttributes = FontAttributes.Bold },
                Ui.Labelled("Server address", _server),
                Ui.Small("Shown in the Windows app: Settings → Phones → Let phones connect to this PC. The phone must be on the same Wi-Fi as that PC."),
                _nameRow,
                Ui.Labelled("E-mail", _email),
                Ui.Labelled("Password", _password),
                _go, _toggle, _busy,
            },
        });
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(18, 60), Children = { card } } };
    }

    private void Toggle()
    {
        _register = !_register;
        _nameRow.IsVisible = _register;
        _go.Text = _register ? "Create account" : "Sign in";
        _toggle.Text = _register ? "I already have an account" : "Create an account instead";
    }

    private async Task GoAsync()
    {
        _busy.IsRunning = true;
        _go.IsEnabled = false;
        var ok = await Ui.TryAsync(this, async () =>
        {
            await AppState.Session.SetServerAsync(_server.Text ?? "");
            await AppState.Session.FeaturesAsync(); // reachable?
            if (_register) await AppState.Session.RegisterAsync(_email.Text ?? "", _password.Text ?? "", _name.Text ?? "");
            await AppState.Session.SignInAsync(_email.Text ?? "", _password.Text ?? "");
            await AppState.StartAsync();
        });
        _busy.IsRunning = false;
        _go.IsEnabled = true;
        if (ok && Window is { } w) w.Page = new AppShell();
    }
}

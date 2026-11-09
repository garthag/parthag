using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Client;

namespace ConstructDpr.App;

/// <summary>Server mode: server address, then sign in or create an account. Built in code to keep it small.</summary>
public sealed class SignInWindow : Window
{
    private readonly TextBox _server = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBox _email = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 4, 0, 16), Padding = new Thickness(6, 5, 6, 5) };
    private readonly StackPanel _nameRow = new() { Visibility = Visibility.Collapsed };
    private readonly Button _go = new() { Content = "Sign in", IsDefault = true };
    private readonly Button _toggle = new() { Content = "Create an account instead" };
    private bool _register;

    public SignInWindow(AuthSession session)
    {
        Title = "Sign in to the CONSTRUCT DPR server";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _go.Style = (Style)Application.Current.Resources["Primary"];
        _server.Text = session.ServerUrl;
        _email.Text = session.Email ?? "";

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "CONSTRUCT DPR", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Brand"] });
        panel.Children.Add(new TextBlock { Text = "Sign in to stay in live sync with the web and Android apps.", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 2, 0, 16), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Label("Server address (from your administrator)"));
        panel.Children.Add(_server);
        _nameRow.Children.Add(Label("Your name"));
        _nameRow.Children.Add(_name);
        panel.Children.Add(_nameRow);
        panel.Children.Add(Label("E-mail"));
        panel.Children.Add(_email);
        panel.Children.Add(Label("Password"));
        panel.Children.Add(_password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_go);
        buttons.Children.Add(_toggle);
        panel.Children.Add(buttons);
        Content = panel;

        _toggle.Click += (_, _) =>
        {
            _register = !_register;
            _nameRow.Visibility = _register ? Visibility.Visible : Visibility.Collapsed;
            _go.Content = _register ? "Create account" : "Sign in";
            _toggle.Content = _register ? "I already have an account" : "Create an account instead";
        };
        _go.Click += async (_, _) =>
        {
            IsEnabled = false;
            var ok = await Dialogs.RunAsync(async () =>
            {
                await session.SetServerAsync(_server.Text);
                await session.FeaturesAsync(); // reachable?
                if (_register) await session.RegisterAsync(_email.Text.Trim(), _password.Password, _name.Text.Trim());
                await session.SignInAsync(_email.Text.Trim(), _password.Password);
            });
            IsEnabled = true;
            if (ok) DialogResult = true;
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["Label"] };
}

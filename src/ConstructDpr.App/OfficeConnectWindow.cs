using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConstructDpr.Core;

namespace ConstructDpr.App;

/// <summary>
/// Office mode set-up: which SQL Server and database, then sign in or create an account in that database.
/// The first person to connect creates the tables; everyone else just signs in.
/// </summary>
public sealed class OfficeConnectWindow : Window
{
    private readonly TextBox _server = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _database = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly RadioButton _windowsAuth = new() { Content = "Windows sign-in (recommended on a company domain)", GroupName = "auth", Margin = new Thickness(0, 2, 0, 4) };
    private readonly RadioButton _sqlAuth = new() { Content = "SQL Server login", GroupName = "auth", Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _sqlUser = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly PasswordBox _sqlPassword = new() { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 5, 6, 5) };
    private readonly StackPanel _sqlLogin = new();
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _email = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(6, 5, 6, 5) };
    private readonly StackPanel _nameRow = new() { Visibility = Visibility.Collapsed };
    private readonly Button _go = new() { Content = "Connect and sign in", IsDefault = true };
    private readonly Button _toggle = new() { Content = "Create an account instead" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
    private bool _register;

    public OfficeConnectWindow()
    {
        Title = "Connect to the office SQL Server";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _go.Style = (Style)Application.Current.Resources["Primary"];
        var s = AppState.Settings;
        (_server.Text, _database.Text, _sqlUser.Text) = (s.OfficeServer, s.OfficeDatabase, s.OfficeSqlUser);
        (_windowsAuth.IsChecked, _sqlAuth.IsChecked) = (s.OfficeWindowsAuth, !s.OfficeWindowsAuth);
        _sqlLogin.Visibility = s.OfficeWindowsAuth ? Visibility.Collapsed : Visibility.Visible;
        _windowsAuth.Checked += (_, _) => _sqlLogin.Visibility = Visibility.Collapsed;
        _sqlAuth.Checked += (_, _) => _sqlLogin.Visibility = Visibility.Visible;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Office database (Microsoft SQL Server)", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("Brand") });
        panel.Children.Add(new TextBlock
        {
            Text = "Everyone in the office connects to the same database and sees changes within seconds. " +
                   "SQL Server Express is free. Ask IT for the server name, or use (localdb)\\MSSQLLocalDB to try on this PC.",
            Foreground = Brush("Muted"), Margin = new Thickness(0, 2, 0, 14), TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Label("SQL Server name, e.g. .\\SQLEXPRESS or OFFICE-PC\\SQLEXPRESS"));
        panel.Children.Add(_server);
        panel.Children.Add(Label("Database name (created automatically the first time)"));
        panel.Children.Add(_database);
        panel.Children.Add(_windowsAuth);
        panel.Children.Add(_sqlAuth);
        _sqlLogin.Children.Add(Label("SQL login"));
        _sqlLogin.Children.Add(_sqlUser);
        _sqlLogin.Children.Add(Label("SQL password"));
        _sqlLogin.Children.Add(_sqlPassword);
        panel.Children.Add(_sqlLogin);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Your CONSTRUCT DPR account", FontWeight = FontWeights.SemiBold, Foreground = Brush("Brand"), Margin = new Thickness(0, 0, 0, 6) });
        _nameRow.Children.Add(Label("Your name"));
        _nameRow.Children.Add(_name);
        panel.Children.Add(_nameRow);
        panel.Children.Add(Label("E-mail"));
        panel.Children.Add(_email);
        panel.Children.Add(Label("Password (at least 8 characters)"));
        panel.Children.Add(_password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_go);
        buttons.Children.Add(_toggle);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);
        Content = panel;

        _toggle.Click += (_, _) =>
        {
            _register = !_register;
            _nameRow.Visibility = _register ? Visibility.Visible : Visibility.Collapsed;
            _go.Content = _register ? "Connect and create account" : "Connect and sign in";
            _toggle.Content = _register ? "I already have an account" : "Create an account instead";
        };
        _go.Click += async (_, _) => await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var settings = new AppSettings
        {
            Mode = DataMode.Office, OfficeServer = _server.Text.Trim(), OfficeDatabase = _database.Text.Trim(),
            OfficeWindowsAuth = _windowsAuth.IsChecked == true, OfficeSqlUser = _sqlUser.Text.Trim(),
        };
        if (settings.OfficeServer.Length == 0) { Dialogs.Error("Enter the SQL Server name."); return; }
        IsEnabled = false;
        _status.Text = "Connecting… (the first connection also creates the tables)";
        Caller? caller = null;
        var ok = await Dialogs.RunAsync(async () =>
        {
            var accounts = await AppState.ConnectOfficeAsync(settings, _sqlPassword.Password);
            var (email, pw, name) = (_email.Text.Trim(), _password.Password, _name.Text.Trim());
            caller = await Task.Run(() => _register ? accounts.Register(email, pw, name) : accounts.SignIn(email, pw));
        });
        IsEnabled = true;
        _status.Text = "";
        if (!ok || caller is null) return;
        var s = AppState.Settings;
        (s.Mode, s.OfficeServer, s.OfficeDatabase, s.OfficeWindowsAuth, s.OfficeSqlUser) =
            (DataMode.Office, settings.OfficeServer, settings.OfficeDatabase, settings.OfficeWindowsAuth, settings.OfficeSqlUser);
        AppState.SaveSettings();
        await AppState.SaveOfficeSecretsAsync(new OfficeSecrets { SqlPassword = settings.OfficeWindowsAuth ? null : _sqlPassword.Password, UserId = caller.UserId });
        DialogResult = true;
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["Label"] };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

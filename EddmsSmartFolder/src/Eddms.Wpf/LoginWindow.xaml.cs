using System.Windows;
using Eddms.Core;

namespace Eddms.Wpf;

public partial class LoginWindow : Window
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public string? Password { get; private set; }
    public bool SyncNow { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        LoginBox.Text = Settings.LoginName;
        SqlBox.Text = Settings.ConnectionString;
        FolderBox.Text = Settings.DownloadRoot;
        DownloadCheck.IsChecked = Settings.DownloadFiles;
        Loaded += (_, _) => { if (LoginBox.Text.Length > 0) PasswordBox.Focus(); else LoginBox.Focus(); };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for downloaded drawings", InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LoginBox.Text) || PasswordBox.Password.Length == 0)
        {
            ErrorText.Text = "Enter your EDMS login name and password.";
            return;
        }
        Password = PasswordBox.Password;
        SyncNow = true;
        Finish();
    }

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        SyncNow = false;
        Finish();
    }

    private void Finish()
    {
        Settings.LoginName = LoginBox.Text.Trim();
        Settings.ConnectionString = string.IsNullOrWhiteSpace(SqlBox.Text) ? EddmsDatabase.DefaultConnectionString : SqlBox.Text.Trim();
        Settings.DownloadRoot = FolderBox.Text.Trim();
        Settings.DownloadFiles = DownloadCheck.IsChecked == true;
        Settings.Save();
        DialogResult = true;
    }
}

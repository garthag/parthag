using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Windows;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;
using Microsoft.Win32;

namespace ConstructDpr.App;

public static class Dialogs
{
    public static void Error(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "CONSTRUCT DPR", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Info(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "CONSTRUCT DPR", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "CONSTRUCT DPR", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Runs an action; rule violations, conflicts and connection problems become a message instead of a crash.</summary>
    public static async Task<bool> RunAsync(Func<Task> work, string? done = null)
    {
        var window = Application.Current.MainWindow;
        var old = window?.Cursor;
        if (window is not null) window.Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await work();
            if (done is not null) Info(done);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Error(ex.Message);
        }
        catch (Exception ex) when (ex is WorkflowException or ScheduleException or ConflictException or ForbiddenException or NotFoundException or HttpRequestException or ArgumentException)
        {
            Error(ex.Message);
        }
        finally
        {
            if (window is not null) window.Cursor = old;
        }
        return false;
    }

    /// <summary>Saves a report file the API produced (asks where, offers to open it).</summary>
    public static void SaveAndOpen(FileDownload f) =>
        SaveAndOpen(f.Content, f.FileName, f.ContentType == "application/pdf" ? "PDF (*.pdf)|*.pdf" : "Excel workbook (*.xlsx)|*.xlsx");

    /// <summary>Asks where to save, writes the bytes and offers to open the file.</summary>
    public static void SaveAndOpen(byte[] bytes, string fileName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = fileName, Filter = filter, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        File.WriteAllBytes(dlg.FileName, bytes);
        if (Confirm($"Saved {Path.GetFileName(dlg.FileName)}.\n\nOpen it now?"))
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    public static string? OpenFile(string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    /// <summary>Simple one-line text prompt (reject reason, revision reason, baseline name).</summary>
    public static string? Prompt(string title, string label, string initial = "", bool password = false)
    {
        var text = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), MinWidth = 380 };
        var secret = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 6, 0, 12), MinWidth = 380, Padding = new Thickness(6, 5, 6, 5) };
        System.Windows.Controls.Control box = password ? secret : text;
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.Resources["Primary"] };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        box.Focus();
        return w.ShowDialog() == true ? (password ? secret.Password : text.Text.Trim()) : null;
    }
}

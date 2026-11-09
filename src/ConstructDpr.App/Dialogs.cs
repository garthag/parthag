using System.Diagnostics;
using System.IO;
using System.Windows;
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
    public static string? Prompt(string title, string label, string initial = "")
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), MinWidth = 380 };
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
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

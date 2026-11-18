using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using DrawingRegister.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DrawingRegister.App;

public partial class MainWindow : Window, IUiService
{
    private readonly TaskCompletionSource<bool> _webViewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string>? _navigation;
    private string? _webViewError;

    public MainWindow(AppPaths paths, Database db)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(paths, db, this);
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            ViewModel.LoadProjects();
            await InitPreviewAsync(paths);
        };
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Set by the smoke test so modal dialogs don't block it.</summary>
    public bool SuppressDialogs { get; set; }

    // ------------------------------------------------------------------ PDF preview (WebView2 / Edge PDF viewer)

    private async Task InitPreviewAsync(AppPaths paths)
    {
        try
        {
            // The install folder may be read-only: keep WebView2's profile with the user's data.
            var env = await CoreWebView2Environment.CreateAsync(null, paths.WebViewDataDir);
            await Preview.EnsureCoreWebView2Async(env);
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Preview.CoreWebView2.NavigationCompleted += (_, e) =>
                _navigation?.TrySetResult(e.IsSuccess ? "rendered" : $"navigation failed: {e.WebErrorStatus}");
            _webViewReady.TrySetResult(true);
            ShowPreview(ViewModel.PreviewPath);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            _webViewError = ex.Message;
            _webViewReady.TrySetResult(false);
            Preview.Visibility = Visibility.Collapsed;
            PreviewMessage.Text = "The built-in preview needs the Microsoft Edge WebView2 Runtime (included with Windows 10/11 updates).\n" +
                                  "Use \"Open PDF\" to view drawings in your PDF viewer.";
            PreviewMessage.Visibility = Visibility.Visible;
        }
    }

    private void ShowPreview(string? path)
    {
        if (Preview.CoreWebView2 is null)
        {
            return;
        }

        if (path is null || !File.Exists(path))
        {
            Preview.Visibility = Visibility.Collapsed;
            PreviewMessage.Text = ViewModel.SelectedDrawing is null ? "Select a drawing to preview its PDF." : "This revision has no PDF file.";
            PreviewMessage.Visibility = Visibility.Visible;
            return;
        }

        PreviewMessage.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Visible;
        _navigation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Preview.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
    }

    /// <summary>For the smoke test: wait until the selected PDF has been rendered (or report why not).</summary>
    public async Task<string> WaitForPreviewAsync(TimeSpan timeout)
    {
        var ready = await Task.WhenAny(_webViewReady.Task, Task.Delay(timeout));
        if (ready != _webViewReady.Task)
        {
            return "WebView2 did not initialise in time";
        }

        if (!await _webViewReady.Task)
        {
            return $"WebView2 unavailable ({_webViewError})";
        }

        ShowPreview(ViewModel.PreviewPath);
        var navigation = _navigation?.Task ?? Task.FromResult("nothing to show");
        var finished = await Task.WhenAny(navigation, Task.Delay(timeout));
        return finished == navigation ? await navigation : "timed out";
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewPath))
        {
            ShowPreview(ViewModel.PreviewPath);
        }
    }

    // ------------------------------------------------------------------ drag & drop and grid

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && ViewModel.HasProject && !ViewModel.IsBusy
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!ViewModel.HasProject)
        {
            ShowError("Select or add a project first, then drop the drawings.");
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && !ViewModel.IsBusy)
        {
            await ViewModel.ImportPathsAsync(paths);
        }
    }

    private void OnDrawingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.EditDrawingCommand.CanExecute(null))
        {
            ViewModel.EditDrawingCommand.Execute(null);
        }
    }

    // ------------------------------------------------------------------ IUiService

    public Project? EditProject(Project project, bool isNew)
    {
        var dialog = new ProjectDialog(project, isNew) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public Drawing? EditDrawing(Drawing drawing)
    {
        var dialog = new DrawingDialog(drawing) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public IReadOnlyList<string> PickPdfFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Upload drawings",
            Filter = "PDF drawings (*.pdf)|*.pdf",
            Multiselect = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Upload every PDF in this folder (including sub-folders)" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    public string? PickCsvSaveLocation(string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (Excel) (*.csv)|*.csv", FileName = suggestedName };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowError(string message)
    {
        if (!SuppressDialogs)
        {
            MessageBox.Show(this, message, "Drawing Register", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowImportResult(ImportSummary summary)
    {
        if (!SuppressDialogs)
        {
            new ImportResultWindow(summary) { Owner = this }.ShowDialog();
        }
    }
}

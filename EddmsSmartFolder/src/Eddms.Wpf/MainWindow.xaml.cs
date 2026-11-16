using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Eddms.Core;

namespace Eddms.Wpf;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly EddmsDatabase _db;
    private string? _password;
    private CancellationTokenSource? _syncCts;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly HashSet<TabItem> _staleTabs = new();

    public MainWindow(AppSettings settings, string? password, bool syncNow)
    {
        InitializeComponent();
        _settings = settings;
        _password = password;
        _db = new EddmsDatabase(settings.ConnectionString);
        DownloadCheck.IsChecked = settings.DownloadFiles;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); MarkAllStale(); RefreshCurrentTab(); };

        Loaded += async (_, _) =>
        {
            try
            {
                SetStatus("Preparing database...");
                await _db.EnsureSchemaAsync();
                ReloadAll();
                SetStatus("Ready");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Could not open the SQL Server database:\n\n" + ex.Message +
                    "\n\nInstall SQL Server Express or LocalDB, or change the connection string on the sign-in screen.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (syncNow) await RunSyncAsync();
        };
    }

    // ------------------------------------------------------------- sync

    private async void Sync_Click(object sender, RoutedEventArgs e) => await RunSyncAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _syncCts?.Cancel();

    private async Task RunSyncAsync()
    {
        if (string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(_settings.LoginName))
        {
            var login = new LoginWindow { Owner = this };
            if (login.ShowDialog() != true || string.IsNullOrEmpty(login.Password)) return;
            _password = login.Password;
            _settings.LoginName = login.Settings.LoginName;
            _settings.DownloadRoot = login.Settings.DownloadRoot;
        }

        _settings.DownloadFiles = DownloadCheck.IsChecked == true;
        _settings.Save();

        SyncButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _syncCts = new CancellationTokenSource();
        var progress = new Progress<SyncProgress>(p =>
        {
            StatusText.Text = p.Message;
            Progress.IsIndeterminate = p.Total == 0;
            if (p.Total > 0) Progress.Value = (double)(p.Done + 1) / p.Total;
        });

        try
        {
            var options = new SyncOptions
            {
                LoginName = _settings.LoginName,
                Password = _password!,
                DownloadFiles = _settings.DownloadFiles,
                DownloadRoot = _settings.DownloadRoot,
            };
            var service = new SyncService(_db);
            var result = await Task.Run(() => service.RunAsync(options, progress, _syncCts.Token));

            ReloadAll();
            var msg = $"Sync finished: {result.Folders} folders, {result.Documents} documents";
            if (options.DownloadFiles) msg += $", {result.Downloaded} new drawings downloaded";
            SetStatus(msg);
            if (result.Warnings.Count > 0)
                MessageBox.Show(this, msg + "\n\nSome items had problems:\n\n" + string.Join("\n", result.Warnings.Take(25)) +
                                (result.Warnings.Count > 25 ? $"\n... and {result.Warnings.Count - 25} more (see Sync History)" : ""),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            ReloadAll();
            SetStatus("Sync stopped.");
        }
        catch (WrenchLoginException ex)
        {
            _password = null; // ask again next time
            SetStatus("Sign-in failed.");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ReloadAll();
            SetStatus("Sync failed.");
            MessageBox.Show(this, "Sync failed:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            SyncButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _syncCts.Dispose();
            _syncCts = null;
        }
    }

    // ------------------------------------------------------------- loading data

    private void ReloadAll()
    {
        LoadFolderTree();
        MarkAllStale();
        RefreshCurrentTab();
    }

    private void MarkAllStale()
    {
        foreach (TabItem t in Tabs.Items) _staleTabs.Add(t);
    }

    private void LoadFolderTree()
    {
        var selectedId = (FolderTree.SelectedItem as FolderNode)?.FolderId;
        var rows = _db.LoadFolders();
        var byParent = rows.ToLookup(r => r.ParentFolderId);

        FolderNode Build(EddmsDatabase.FolderRow r, int depth)
        {
            var node = new FolderNode { FolderId = r.FolderId, Name = r.FolderName, FullPath = r.FullPath, OwnCount = r.DocumentCount };
            foreach (var c in byParent[r.FolderId].OrderBy(x => x.SortOrder))
                node.Children.Add(Build(c, depth + 1));
            node.IsExpanded = depth < 2;
            return node;
        }

        var roots = byParent[null].OrderBy(r => r.SortOrder).Select(r => Build(r, 0)).ToList();
        FolderTree.ItemsSource = roots;
        if (selectedId == null && roots.Count > 0)
            FolderPathText.Text = "Select a folder on the left. Total documents: " + roots.Sum(r => r.TotalCount);
    }

    private void RefreshCurrentTab()
    {
        if (Tabs.SelectedItem is not TabItem tab || !_staleTabs.Remove(tab)) { UpdateCount(); return; }
        var search = SearchBox.Text;
        try
        {
            if (tab == FoldersTab)
            {
                var node = FolderTree.SelectedItem as FolderNode;
                DocumentsGrid.ItemsSource = _db.LoadDocuments(node?.FolderId, SubFoldersCheck.IsChecked == true, search).DefaultView;
                FolderPathText.Text = node?.FullPath ?? "All documents";
            }
            else if (tab == SubmissionTab)
                SubmissionGrid.ItemsSource = _db.LoadRegister(EddmsDatabase.Register.Submission, search).DefaultView;
            else if (tab == RevisionTab)
                RevisionGrid.ItemsSource = _db.LoadRegister(EddmsDatabase.Register.Revision, search).DefaultView;
            else if (tab == PublishedTab)
                PublishedGrid.ItemsSource = _db.LoadRegister(EddmsDatabase.Register.Published, search).DefaultView;
            else if (tab == HistoryTab)
                HistoryGrid.ItemsSource = _db.LoadSyncHistory().DefaultView;
        }
        catch (Exception ex)
        {
            _staleTabs.Add(tab);
            SetStatus("Could not load data: " + ex.Message);
        }
        UpdateCount();
    }

    private DataGrid? CurrentGrid() => (Tabs.SelectedItem as TabItem) switch
    {
        var t when t == FoldersTab => DocumentsGrid,
        var t when t == SubmissionTab => SubmissionGrid,
        var t when t == RevisionTab => RevisionGrid,
        var t when t == PublishedTab => PublishedGrid,
        var t when t == HistoryTab => HistoryGrid,
        _ => null,
    };

    private void UpdateCount()
    {
        var view = CurrentGrid()?.ItemsSource as DataView;
        CountText.Text = view == null ? "" : $"{view.Count} rows";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------- events

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs) RefreshCurrentTab();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _staleTabs.Add(FoldersTab);
        RefreshCurrentTab();
    }

    private void SubFolders_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _staleTabs.Add(FoldersTab);
        RefreshCurrentTab();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "DocId") { e.Cancel = true; return; }
        if (e.PropertyType == typeof(DateTime) && e.Column is DataGridTextColumn dc)
        {
            var binding = (System.Windows.Data.Binding)dc.Binding;
            binding.StringFormat = e.PropertyName is "Started" or "Finished" or "First Seen" or "Last Seen" or "Downloaded"
                ? "dd-MMM-yyyy HH:mm" : "dd-MMM-yyyy";
        }
        if (e.PropertyName is "Description" or "Approval Description" or "Reply" or "Approval")
            e.Column.Width = new DataGridLength(320);
        if (e.PropertyName is "Message")
            e.Column.Width = new DataGridLength(600);
        if (e.PropertyName is "Local File")
            e.Column.Width = new DataGridLength(260);
    }

    private void Submission_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not DataRowView r) return;
        e.Row.Background = r["Submission Status"] as string == "Pending Reply"
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE0))
            : r["Days Late"] is int late && late > 0 ? new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEC)) : null;
    }

    private void Revision_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not DataRowView r) return;
        e.Row.Foreground = r["State"] as string == "Superseded" ? Brushes.Gray : Brushes.Black;
    }

    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelectedFile();

    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenSelectedFile();

    private void OpenSelectedFile()
    {
        if (CurrentGrid()?.SelectedItem is not DataRowView row || !row.Row.Table.Columns.Contains("Local File")) return;
        var path = row["Local File"] as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            SetStatus("This drawing has not been downloaded yet. Tick 'Download drawings' and Sync.");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.DownloadRoot);
        Process.Start(new ProcessStartInfo(_settings.DownloadRoot) { UseShellExecute = true });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGrid()?.ItemsSource is not DataView view) return;
        var tabName = ((Tabs.SelectedItem as TabItem)?.Header as string ?? "Export").Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{tabName} {DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV for Excel (*.csv)|*.csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        var cols = view.Table!.Columns.Cast<DataColumn>().Where(c => c.ColumnName != "DocId").ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => Csv(c.ColumnName))));
        foreach (DataRowView r in view)
            sb.AppendLine(string.Join(",", cols.Select(c => Csv(r[c.ColumnName] switch
            {
                DateTime d => d.ToString("dd-MMM-yyyy"),
                DBNull => "",
                var v => v?.ToString() ?? "",
            }))));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        SetStatus($"Exported {view.Count} rows to {dlg.FileName}");
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _syncCts?.Cancel();
        base.OnClosing(e);
    }
}

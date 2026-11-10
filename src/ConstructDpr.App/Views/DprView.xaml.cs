using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Calculations;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App.Views;

public sealed record DprItem(int Id, string Title, string Sub, string Status, Brush Brush, DprStatus Raw);

public sealed record PhotoItem(PhotoInfo Info, ImageSource? Image, string Caption, string Sub);

/// <summary>Daily reports: list, editor and the submit → approve/reject → revise workflow (rules enforced by the API).</summary>
public partial class DprView : UserControl, IRefreshable, ISaveable
{
    private Dpr? _dpr;
    private bool _loading, _dirty;
    private List<DprItem> _items = new();

    public DprView()
    {
        InitializeComponent();
        NewShift.ItemsSource = Enum.GetValues<Shift>();
        NewShift.SelectedItem = Shift.Day;
        NewDate.SelectedDate = DateTime.Today;
        NewDate.DisplayDateEnd = DateTime.Today;
        foreach (var box in FindTextBoxes(Narrative)) box.TextChanged += (_, _) => { if (!_loading) _dirty = true; };
        StatusFilter.ItemsSource = new[] { "All", "Draft", "Submitted", "Approved", "Rejected" };
        StatusFilter.SelectedIndex = 0;
    }

    /// <summary>Selects an editor tab by its header text (screenshot mode).</summary>
    public void ShowTab(string startsWith)
    {
        foreach (var t in Tabs.Items.OfType<TabItem>())
            if ((t.Header?.ToString() ?? "").StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) Tabs.SelectedItem = t;
    }

    /// <summary>Ctrl+S.</summary>
    public async Task SaveShortcutAsync()
    {
        if (_dpr is { IsEditable: true } && SaveBtn.IsEnabled) await RunWorkflow((api, d) => api.SaveDprAsync(AppState.Pid, d), "Draft saved.");
    }

    private IEnumerable<DprItem> Filtered()
    {
        var status = StatusFilter.SelectedItem as string ?? "All";
        var q = SearchBox.Text.Trim();
        return _items.Where(i => (status == "All" || i.Raw.ToString() == status) &&
                                 (q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _loading = true;
        List.ItemsSource = Filtered().ToList();
        List.SelectedItem = (List.ItemsSource as List<DprItem>)?.FirstOrDefault(i => i.Id == _dpr?.Id);
        _loading = false;
    }

    private static IEnumerable<TextBox> FindTextBoxes(DependencyObject root)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (c is TextBox t) yield return t;
            foreach (var x in FindTextBoxes(c)) yield return x;
        }
    }

    public static Brush StatusBrush(DprStatus s) => new SolidColorBrush(s switch
    {
        DprStatus.Approved => Color.FromRgb(0x0F, 0x7B, 0x3E),
        DprStatus.Submitted => Color.FromRgb(0xC9, 0x85, 0x00),
        DprStatus.Rejected => Color.FromRgb(0xC6, 0x28, 0x28),
        _ => Color.FromRgb(0x6B, 0x77, 0x82),
    });

    public async Task RefreshAsync()
    {
        if (_dirty) { await LoadListAsync(_dpr?.Id, keepEditor: true); return; } // unsaved edits stay; the list still updates
        await LoadListAsync(_dpr?.Id);
    }

    /// <summary>Opens a report by id (screenshot mode, and after creating a report).</summary>
    public Task OpenAsync(int id) => LoadListAsync(id);

    private async Task LoadListAsync(int? select, bool keepEditor = false)
    {
        if (AppState.Pid == 0) { List.ItemsSource = null; Show(null); return; }
        _items = (await AppState.Api.DprsAsync(AppState.Pid)).Select(d => new DprItem(d.Id,
            d.Number + (d.Revision > 0 ? $"  R{d.Revision}" : ""),
            $"{d.ReportDate:ddd dd MMM yyyy} · {d.Shift} · {d.PreparedBy}", d.Status.ToString().ToUpperInvariant(), StatusBrush(d.Status), d.Status)).ToList();
        var items = Filtered().ToList();
        if (select is { } want && items.All(i => i.Id != want) && _items.Any(i => i.Id == want))
        {
            (StatusFilter.SelectedIndex, SearchBox.Text) = (0, ""); // make sure the report being opened is visible
            items = _items;
        }
        _loading = true;
        List.ItemsSource = items;
        var target = items.FirstOrDefault(i => i.Id == select) ?? items.FirstOrDefault();
        List.SelectedItem = target;
        _loading = false;
        if (keepEditor) return;
        if (target is null) Show(null);
        else Show(await AppState.Api.DprAsync(AppState.Pid, target.Id));
    }

    private async void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || List.SelectedItem is not DprItem item || item.Id == _dpr?.Id) return;
        if (_dpr is { IsEditable: true } && _dirty && Dialogs.Confirm($"Save your changes to {_dpr.Number} first?"))
            if (!await SaveAsync()) return;
        _dirty = false;
        await Dialogs.RunAsync(async () => Show(await AppState.Api.DprAsync(AppState.Pid, item.Id)));
    }

    private bool CanEdit(Dpr d) => d.IsEditable && (AppState.Can(Module.Dpr, Act.Edit) ||
        (AppState.Can(Module.Dpr, Act.Create) && string.Equals(d.PreparedBy, AppState.MyReportName, StringComparison.OrdinalIgnoreCase)));

    private async void Show(Dpr? d)
    {
        _loading = true;
        _dpr = d;
        _dirty = false;
        Editor.IsEnabled = d is not null;
        if (d is null)
        {
            Title.Text = "No reports yet";
            Meta.Text = "Create the first daily report on the left.";
            Notice.Visibility = Visibility.Collapsed;
            Lines.ItemsSource = Crew.ItemsSource = Equipment.ItemsSource = Materials.ItemsSource = null;
            _loading = false;
            return;
        }
        Title.Text = $"{d.Number}{(d.Revision > 0 ? $" · Revision {d.Revision}" : "")}";
        Meta.Text = $"{d.ReportDate:dddd, dd MMM yyyy} · {d.Shift} shift · prepared by {d.PreparedBy}" +
                    (d.ApprovedBy is null ? "" : $" · approved by {d.ApprovedBy} on {d.ApprovedAt?.ToLocalTime():dd MMM yyyy HH:mm}");
        Badge.Background = StatusBrush(d.Status);
        BadgeText.Text = d.Status.ToString().ToUpperInvariant();

        var editable = CanEdit(d);
        var reviewing = d.Status == DprStatus.Submitted && AppState.Can(Module.Dpr, Act.Approve);
        SaveBtn.IsEnabled = editable;
        SubmitBtn.IsEnabled = editable && AppState.Can(Module.Dpr, Act.Submit);
        ApproveBtn.IsEnabled = reviewing;
        RejectBtn.IsEnabled = d.Status == DprStatus.Submitted && AppState.Can(Module.Dpr, Act.Reject);
        ReviseBtn.IsEnabled = d.Status == DprStatus.Approved && AppState.Can(Module.Dpr, Act.Create | Act.Edit);
        AddLineBar.IsEnabled = AddCrewBtn.IsEnabled = AddEqBtn.IsEnabled = AddMatBtn.IsEnabled = editable;
        Lines.IsReadOnly = !(editable || reviewing);
        TodayCol.IsReadOnly = DelayCol.IsReadOnly = !editable;
        AcceptedCol.IsReadOnly = !reviewing;
        Crew.IsReadOnly = Equipment.IsReadOnly = Materials.IsReadOnly = !editable;
        Narrative.IsEnabled = editable;

        ActivityPick.ItemsSource = (await AppState.Api.ActivitiesAsync(AppState.Pid))
            .Where(a => a.Status is not (ActivityStatus.Completed or ActivityStatus.Cancelled))
            .Select(a => new ActivityChoice(a.Id, $"{a.Code}  {a.Name}  ({a.Unit})")).ToList();
        Narrative.DataContext = d;
        Bind();
        AddPhotoBtn.IsEnabled = RemovePhotoBtn.IsEnabled = CaptionBox.IsEnabled = editable;
        _ = LoadPhotosAsync(d);

        var (notice, tone) = d.Status switch
        {
            DprStatus.Approved when await AppState.Api.VerifyDprAsync(AppState.Pid, d.Id) => ($"✔ Approved and locked. Snapshot SHA-256 {d.SnapshotHash?[..16]}… verified. Corrections are made as a new revision.", "Good"),
            DprStatus.Approved => ("⚠ The stored content no longer matches its approval snapshot (SHA-256). The database may have been altered outside the app.", "Bad"),
            DprStatus.Rejected => ("Rejected: " + d.ReviewComment + "  — correct and submit again.", "Bad"),
            DprStatus.Submitted => ("Waiting for review. The reviewer can adjust the Accepted quantity before approving; the reported quantity is kept.", "Warn"),
            _ when d.ReviewComment is not null => (d.ReviewComment, "Muted"),
            _ => ("", ""),
        };
        if (_dpr != d) { _loading = false; return; } // another report was opened meanwhile
        Notice.Visibility = notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = notice;
        if (tone.Length > 0)
        {
            var c = ((SolidColorBrush)FindResource(tone)).Color;
            Notice.Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B));
            NoticeText.Foreground = new SolidColorBrush(c);
        }
        _loading = false;
    }

    private void Bind()
    {
        if (_dpr is null) return;
        Lines.ItemsSource = null; Lines.ItemsSource = _dpr.Activities;
        Crew.ItemsSource = null; Crew.ItemsSource = _dpr.Manpower;
        Equipment.ItemsSource = null; Equipment.ItemsSource = _dpr.Equipment;
        Materials.ItemsSource = null; Materials.ItemsSource = _dpr.Materials;
        CrewTotal.Text = $"Total {_dpr.Manpower.Sum(m => m.Headcount)} workers · {_dpr.Manpower.Sum(m => m.ManHours):0} man-hours";
    }

    /// <summary>Recomputes cumulative / balance / % on this PC while typing; the API repeats it on save.</summary>
    private void Recalc()
    {
        if (_dpr is null) return;
        foreach (var l in _dpr.Activities.Where(l => l.Activity is not null))
            Progress.ApplyLine(l, l.Activity!, l.PrevCum, _dpr.ReportDate, _dpr.Status == DprStatus.Submitted);
    }

    private void OnCellEdited(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || sender is not DataGrid grid) return;
        _dirty = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_dpr is null) return;
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            Recalc();
            Bind();
        }, DispatcherPriority.Background);
    }

    private void Commit()
    {
        foreach (var g in new[] { Lines, Crew, Equipment, Materials }) g.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        var date = DateOnly.FromDateTime(NewDate.SelectedDate ?? DateTime.Today);
        Dpr? d = null;
        if (await Dialogs.RunAsync(async () => d = await AppState.Api.CreateDprAsync(AppState.Pid, date, (Shift)NewShift.SelectedItem)))
            await LoadListAsync(d!.Id);
    }

    private async void OnAddLine(object sender, RoutedEventArgs e)
    {
        if (_dpr is null || ActivityPick.SelectedValue is not int id) return;
        if (_dpr.Activities.Any(l => l.ActivityId == id)) { Dialogs.Error("That activity is already in this report."); return; }
        _dpr.Activities.Add(new DprActivityLine { ActivityId = id });
        _dirty = true;
        await Dialogs.RunAsync(async () =>
        {
            var calc = await AppState.Api.RecalculateDprAsync(AppState.Pid, _dpr); // brings the activity and its previous quantity
            foreach (var l in _dpr.Activities)
                if (calc.Activities.FirstOrDefault(c => c.ActivityId == l.ActivityId) is { } c) (l.Activity, l.PrevCum) = (c.Activity, c.PrevCum);
        });
        Recalc();
        Bind();
        Tabs.SelectedIndex = 0;
    }

    private void OnAddCrew(object sender, RoutedEventArgs e) { _dpr?.Manpower.Add(new ManpowerLine { Agency = "Agency", Trade = "Trade" }); _dirty = true; Bind(); }
    private void OnAddEquipment(object sender, RoutedEventArgs e) { _dpr?.Equipment.Add(new EquipmentLine { Code = "EQ-", Type = "Type" }); _dirty = true; Bind(); }
    private void OnAddMaterial(object sender, RoutedEventArgs e) { _dpr?.Materials.Add(new MaterialLine { Code = "MAT-", Description = "Description", Unit = "nos" }); _dirty = true; Bind(); }

    private async Task<bool> RunWorkflow(Func<IConstructDprApi, Dpr, Task<Dpr>> action, string? done = null)
    {
        if (_dpr is null) return false;
        Commit();
        var current = _dpr;
        Dpr? result = null;
        if (!await Dialogs.RunAsync(async () => result = await action(AppState.Api, current))) return false;
        _dirty = false;
        await LoadListAsync(result!.Id);
        if (done is not null) Dialogs.Info(done);
        return true;
    }

    private Task<bool> SaveAsync() => RunWorkflow((api, d) => api.SaveDprAsync(AppState.Pid, d));
    private async void OnSave(object sender, RoutedEventArgs e) => await RunWorkflow((api, d) => api.SaveDprAsync(AppState.Pid, d), "Draft saved.");
    private async void OnSubmit(object sender, RoutedEventArgs e) => await RunWorkflow((api, d) => api.SubmitDprAsync(AppState.Pid, d), "Submitted for review.");
    private async void OnApprove(object sender, RoutedEventArgs e) => await RunWorkflow((api, d) => api.ApproveDprAsync(AppState.Pid, d.Id,
        d.Activities.Where(l => l.QtyAccepted is not null).ToDictionary(l => l.ActivityId, l => l.QtyAccepted!.Value)), "Approved. The report is locked and progress is updated.");

    private async void OnReject(object sender, RoutedEventArgs e)
    {
        var reason = Dialogs.Prompt("Reject report", "Reason for rejection (the preparer sees this):");
        if (reason is not null) await RunWorkflow((api, d) => api.RejectDprAsync(AppState.Pid, d.Id, reason));
    }

    private async void OnRevise(object sender, RoutedEventArgs e)
    {
        if (_dpr is null) return;
        var reason = Dialogs.Prompt("Start revision", $"{_dpr.Number} is approved and locked. A new draft revision will be created; the approved revision stays unchanged.\n\nReason for the revision:");
        if (reason is not null) await RunWorkflow((api, d) => api.ReviseDprAsync(AppState.Pid, d.Id, reason));
    }

    private async void OnPdf(object sender, RoutedEventArgs e) => await Download(ReportKind.DprPdf);
    private async void OnExcel(object sender, RoutedEventArgs e) => await Download(ReportKind.DprExcel);

    private async Task Download(ReportKind kind)
    {
        if (_dpr is null) return;
        if (_dirty) { Dialogs.Error("Save the report first, so the file shows your latest changes."); return; }
        await Dialogs.RunAsync(async () => Dialogs.SaveAndOpen(await AppState.Api.ReportAsync(AppState.Pid, kind, _dpr.Id)));
    }

    // ───────────── photos ─────────────

    private async Task LoadPhotosAsync(Dpr d)
    {
        List<PhotoInfo> infos;
        try { infos = await AppState.Api.PhotosAsync(AppState.Pid, d.Id); }
        catch (Exception ex) when (ex is HttpRequestException or NotFoundException) { return; }
        var items = new List<PhotoItem>();
        foreach (var p in infos)
        {
            ImageSource? img = null;
            try { img = Thumbnail((await AppState.Api.PhotoAsync(AppState.Pid, p.Id)).Content); }
            catch (Exception ex) when (ex is HttpRequestException or NotFoundException or NotSupportedException or FileFormatException) { }
            items.Add(new PhotoItem(p, img, p.Caption ?? p.FileName, $"{p.TakenBy} · {p.At.ToLocalTime():dd MMM HH:mm}"));
        }
        if (_dpr?.Id != d.Id) return;
        Photos.ItemsSource = items;
        PhotosTab.Header = $"Photos ({items.Count})";
    }

    private static BitmapImage Thumbnail(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 400;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Shrinks a photo to at most 1600 px (JPEG, quality 85): small enough for the database and quick to sync.</summary>
    public static byte[] Shrink(byte[] original)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(original), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var scale = Math.Min(1.0, 1600.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        // Respect the camera's orientation flag (photos taken in portrait).
        if (decoder.Frames[0].Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation") && meta.GetQuery("System.Photo.Orientation") is ushort o)
        {
            var angle = o switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
            if (angle != 0) frame = new TransformedBitmap(frame, new RotateTransform(angle));
        }
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private async void OnAddPhotos(object sender, RoutedEventArgs e)
    {
        if (_dpr is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Photos (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp", Title = "Add site photos" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var caption = CaptionBox.Text.Trim();
        var current = _dpr;
        foreach (var path in dlg.FileNames)
        {
            await Dialogs.RunAsync(async () =>
            {
                byte[] bytes;
                try { bytes = await Task.Run(() => Shrink(File.ReadAllBytes(path))); }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
                {
                    throw new WorkflowException($"{Path.GetFileName(path)} could not be read as a photo: {ex.Message}");
                }
                await AppState.Api.AddPhotoAsync(AppState.Pid, current.Id, Path.ChangeExtension(Path.GetFileName(path), ".jpg"), bytes, caption.Length == 0 ? null : caption);
            });
        }
        CaptionBox.Text = "";
        await LoadPhotosAsync(current);
    }

    private async void OnRemovePhoto(object sender, RoutedEventArgs e)
    {
        if (Photos.SelectedItem is not PhotoItem p || _dpr is null) return;
        if (!Dialogs.Confirm($"Remove the photo \"{p.Caption}\" from {_dpr.Number}?")) return;
        if (await Dialogs.RunAsync(() => AppState.Api.DeletePhotoAsync(AppState.Pid, p.Info.Id))) await LoadPhotosAsync(_dpr);
    }

    private async void OnOpenPhoto(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Photos.SelectedItem is not PhotoItem p) return;
        await Dialogs.RunAsync(async () =>
        {
            var f = await AppState.Api.PhotoAsync(AppState.Pid, p.Info.Id);
            var path = Path.Combine(Path.GetTempPath(), $"cdpr-photo-{p.Info.Id}{(f.ContentType == "image/png" ? ".png" : ".jpg")}");
            await File.WriteAllBytesAsync(path, f.Content);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
    }
}

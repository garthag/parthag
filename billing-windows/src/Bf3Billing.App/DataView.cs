using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>The data file: backup, restore, switching files, Excel import and export.</summary>
sealed class DataView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly AppSettings _settings;
    readonly Label _file = new() { AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 4, 0, 4) };
    readonly Label _counts = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 8) };

    public DataView(IAppHost host, AppSettings settings)
    {
        _host = host;
        _settings = settings;
        BackColor = Ui.Back;
        AutoScroll = true;

        var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        stack.Controls.Add(Card("Data file", "Everything is kept in one SQLite file on this computer. Copy it (or use Back up) to keep it safe; a backup opens on any PC with this app.",
            new Control[] { _file, _counts },
            ("Back up now…", Backup), ("Restore from backup…", Restore), ("Open folder", (_, _) => Ui.OpenShell(Path.GetDirectoryName(_host.Repo.Db.FilePath)!))));
        stack.Controls.Add(Card("Other data files", "Switch to another data file, start an empty one, or open a demo file with made-up bills to try the app. Your data file is not changed.",
            Array.Empty<Control>(),
            ("Open data file…", OpenOther), ("New empty data file…", NewFile), ("Open demo data file", OpenDemo),
            ("Back to default data file", (_, _) => Switch(BillingDb.DefaultPath))));
        stack.Controls.Add(Card("BSL workbooks",
            "The BSL master BOM (every sheet: drawings with their part lines, sets, totals checked against each sheet) and the contractor master " +
            "(contractors, work orders, the BOQ with client and agency rates, RA-1…RA-n bills, the DRAWING scope and REMARKS extra items). " +
            "Shows what was found before anything is saved; importing again updates in place.",
            Array.Empty<Control>(), ("Import BSL workbooks…", (_, _) => OpenBsl(_host))));
        stack.Controls.Add(Card("Import from Excel",
            "Import from Excel: open any workbook, even an untidy one with several sheets. For each sheet the app finds the heading row, guesses what it holds " +
            "(contractors, work orders, BOM or bill lines) and fits its columns to the app's fields. Columns that fit no field can be added as new fields. " +
            "Before anything is saved, every row is checked and coloured: new, already saved, repeated in the file (grouped), or with a problem.\n" +
            "Quick BOM and bill imports skip the check: they add what fits, leave other columns out, update BOM weights and skip bills already saved.",
            Array.Empty<Control>(),
            ("Import from Excel (check and match columns)…", (_, _) => OpenWizard(_host)),
            ("Quick BOM import…", (_, _) => ImportBom(_host)), ("Quick bill import…", (_, _) => ImportBills(_host)), ("Save Excel templates…", Templates),
            ("Delete unused BOM rows…", DeleteUnusedBom)));
        stack.Controls.Add(Card("Export", "Contractors, work orders, BOM, bill lines, bills and the work order abstract in one workbook. The 'Bill lines' sheet can be imported again.",
            Array.Empty<Control>(), ("Export everything to Excel…", ExportAll)));
        Controls.Add(stack);
    }

    static Control Card(string title, string text, Control[] extra, params (string Text, EventHandler Click)[] buttons)
    {
        var card = new Ui.Card { Width = 900, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(900, 0) };
        var inner = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new Point(14, 14), MaximumSize = new Size(870, 0) };
        inner.Controls.Add(new Label { Text = title, AutoSize = true, Font = Ui.Section, Margin = new Padding(0, 0, 0, 4) });
        inner.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 8) });
        foreach (var c in extra) inner.Controls.Add(c);
        var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(870, 0), Margin = new Padding(0) };
        foreach (var (t, click) in buttons) bar.Controls.Add(Ui.Btn(t, click, primary: t.StartsWith("Import from Excel") || t.StartsWith("Import BSL")));
        inner.Controls.Add(bar);
        card.Controls.Add(inner);
        return card;
    }

    public void RefreshData()
    {
        var c = _host.Repo.Counts();
        _file.Text = _host.Repo.Db.FilePath;
        var size = File.Exists(_host.Repo.Db.FilePath) ? new FileInfo(_host.Repo.Db.FilePath).Length / 1024.0 : 0;
        _counts.Text = $"{c.Contractors} contractors · {c.WorkOrders} work orders · {c.BomRows:N0} BOM rows · {c.Bills} bills · {c.Lines} bill lines · {size:N0} KB";
    }

    // ───────────────────────── Shared actions (also used by the dashboard) ─────────────────────────

    public static void OpenBsl(IAppHost host)
    {
        using var f = new BslImportForm(host.Repo);
        f.ShowDialog(host.Window);
        if (f.Changed) host.DataChanged();
    }

    public static void OpenWizard(IAppHost host)
    {
        using var f = new ImportWizardForm(host.Repo);
        f.Shown += (_, _) => f.ChooseFile();
        f.ShowDialog(host.Window);
        if (f.Changed) host.DataChanged();
    }

    public static void ImportBom(IAppHost host)
    {
        var path = Ui.OpenFile(host.Window, "Import BOM from Excel");
        if (path == null) return;
        ImportResult? r = null;
        Cursor.Current = Cursors.WaitCursor;
        if (Ui.Try(host.Window, () => r = ExcelIO.ImportBom(host.Repo, path)))
        {
            Ui.Info(host.Window, "BOM import: " + r!.Summary);
            host.DataChanged();
        }
    }

    public static void ImportBills(IAppHost host)
    {
        var path = Ui.OpenFile(host.Window, "Import bills from Excel");
        if (path == null) return;
        ImportResult? r = null;
        Cursor.Current = Cursors.WaitCursor;
        if (Ui.Try(host.Window, () => r = ExcelIO.ImportBills(host.Repo, path)))
        {
            Ui.Info(host.Window, "Bill import (bills): " + r!.Summary);
            host.DataChanged();
        }
    }

    // ───────────────────────── Actions ─────────────────────────

    void Backup(object? s, EventArgs e)
    {
        var name = $"billing-backup-{DateTime.Now:yyyy-MM-dd-HHmm}.db";
        using var d = new SaveFileDialog { Title = "Back up the data file", FileName = name, Filter = "Billing data file (*.db)|*.db", OverwritePrompt = true };
        if (Directory.Exists(_settings.LastBackupFolder)) d.InitialDirectory = _settings.LastBackupFolder;
        if (d.ShowDialog(_host.Window) != DialogResult.OK) return;
        if (string.Equals(Path.GetFullPath(d.FileName), _host.Repo.Db.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Info(_host.Window, "Choose a different file from the data file itself.");
            return;
        }
        if (Ui.Try(_host.Window, () => _host.Repo.Db.BackupTo(d.FileName)))
        {
            _settings.LastBackupFolder = Path.GetDirectoryName(d.FileName) ?? "";
            _settings.Save();
            Ui.Info(_host.Window, "Backup saved:" + Environment.NewLine + d.FileName);
        }
    }

    void Restore(object? s, EventArgs e)
    {
        var path = Ui.OpenFile(_host.Window, "Restore from a backup", "Billing data file (*.db)|*.db|All files (*.*)|*.*");
        if (path == null) return;
        if (!BillingDb.LooksLikeBillingDb(path)) { Ui.Info(_host.Window, "That file is not a billing data file."); return; }
        if (!Ui.Confirm(_host.Window, "Replace ALL data in the current data file with the backup?\n\nA copy of the current data is saved next to it first (billing-before-restore-….db).")) return;
        Ui.Try(_host.Window, () =>
        {
            var current = _host.Repo.Db.FilePath;
            var safety = Path.Combine(Path.GetDirectoryName(current)!, $"billing-before-restore-{DateTime.Now:yyyy-MM-dd-HHmmss}.db");
            _host.Repo.Db.BackupTo(safety);
            new BillingDb(path, seedMasters: false).BackupTo(current + ".restoring");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Copy(current + ".restoring", current, overwrite: true);
            File.Delete(current + ".restoring");
            _host.SwitchDataFile(current);
            Ui.Info(_host.Window, "Restored. The previous data was saved as:" + Environment.NewLine + safety);
        });
    }

    void OpenOther(object? s, EventArgs e)
    {
        var path = Ui.OpenFile(_host.Window, "Open a data file", "Billing data file (*.db)|*.db|All files (*.*)|*.*");
        if (path == null) return;
        if (!BillingDb.LooksLikeBillingDb(path)) { Ui.Info(_host.Window, "That file is not a billing data file."); return; }
        Switch(path);
    }

    void NewFile(object? s, EventArgs e)
    {
        var path = Ui.SaveFile(_host.Window, "New empty data file", "billing.db", "Billing data file (*.db)|*.db");
        if (path == null) return;
        if (File.Exists(path)) { Ui.Info(_host.Window, "That file already exists. Choose a new name."); return; }
        Switch(path);
    }

    void OpenDemo(object? s, EventArgs e)
    {
        var path = Path.Combine(Path.GetDirectoryName(BillingDb.DefaultPath)!, "billing-demo.db");
        Ui.Try(_host.Window, () =>
        {
            var repo = new BillingRepository(new BillingDb(path));
            if (repo.Counts().Bills == 0) Seed.LoadDemo(repo);
        });
        Switch(path);
        Ui.Info(_host.Window, "This is the demo data file with made-up bills. Your own data is untouched; use 'Back to default data file' on this tab to return to it.");
    }

    void Switch(string path) => Ui.Try(_host.Window, () => _host.SwitchDataFile(path));

    void Templates(object? s, EventArgs e)
    {
        using var d = new FolderBrowserDialog { Description = "Folder for the two Excel templates" };
        if (d.ShowDialog(_host.Window) != DialogResult.OK) return;
        var bom = Path.Combine(d.SelectedPath, "BOM import template.xlsx");
        var bills = Path.Combine(d.SelectedPath, "Bill import template.xlsx");
        if (Ui.Try(_host.Window, () => ExcelIO.WriteTemplates(bom, bills)))
            Ui.OfferOpen(_host.Window, d.SelectedPath, "Templates saved in this folder.");
    }

    void DeleteUnusedBom(object? s, EventArgs e)
    {
        if (!Ui.Confirm(_host.Window, "Delete every BOM row that no bill line uses? Use this before re-importing a revised BOM.")) return;
        var n = 0;
        if (Ui.Try(_host.Window, () => n = _host.Repo.DeleteUnusedBom()))
        {
            Ui.Info(_host.Window, $"{n} BOM row(s) deleted.");
            _host.DataChanged();
        }
    }

    void ExportAll(object? s, EventArgs e)
    {
        var path = Ui.SaveFile(_host.Window, "Export everything", $"BF3 billing {DateTime.Now:yyyy-MM-dd}.xlsx");
        if (path == null) return;
        Cursor.Current = Cursors.WaitCursor;
        if (Ui.Try(_host.Window, () => ExcelIO.ExportAll(_host.Repo, path)))
            Ui.OfferOpen(_host.Window, path, "Exported.");
    }
}

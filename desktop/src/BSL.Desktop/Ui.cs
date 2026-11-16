namespace BSL.Desktop;

/// <summary>Small UI helpers: message boxes, file dialogs, busy cursor.</summary>
public static class Ui
{
    public static readonly Font HeaderFont = new("Segoe UI", 11f, FontStyle.Bold);
    public static readonly Color ReadOnlyBack = Color.FromArgb(242, 242, 242);
    public static readonly Color Accent = Color.FromArgb(31, 78, 120);

    public static void Error(Exception ex)
    {
        LogError(ex);
        Error(ex is InvalidOperationException ? ex.Message : ex.Message + "\n\n" + ex.GetType().Name + "  (details in error.log)");
    }

    /// <summary>Full exception details to %AppData%\BSLProjectControl\error.log for support.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSLProjectControl");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
    public static void Error(string msg) => MessageBox.Show(msg, "BSL Project Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
    public static void Info(string msg) => MessageBox.Show(msg, "BSL Project Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
    public static bool Confirm(string msg) => MessageBox.Show(msg, "BSL Project Control", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public static string? OpenExcel(string title = "Open Excel workbook")
    {
        using var d = new OpenFileDialog { Title = title, Filter = "Excel workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm", InitialDirectory = Program.Settings.LastFolder ?? "" };
        if (d.ShowDialog() != DialogResult.OK) return null;
        Remember(d.FileName);
        return d.FileName;
    }

    public static string? Save(string fileName, string filter = "Excel workbook (*.xlsx)|*.xlsx")
    {
        using var d = new SaveFileDialog { FileName = Clean(fileName), Filter = filter, InitialDirectory = Program.Settings.LastFolder ?? "" };
        if (d.ShowDialog() != DialogResult.OK) return null;
        Remember(d.FileName);
        return d.FileName;
    }

    public static string? SavePdf(string fileName) => Save(fileName, "PDF document (*.pdf)|*.pdf");

    static string Clean(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    static void Remember(string file)
    {
        Program.Settings.LastFolder = Path.GetDirectoryName(file);
        try { Program.Settings.Save(); } catch { }
    }

    /// <summary>Opens a produced file with the default program (Excel, PDF viewer).</summary>
    public static void OpenFile(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Error("File saved to " + path + " but could not be opened: " + ex.Message); }
    }

    public static void Busy(Control owner, Action a)
    {
        var old = owner.Cursor;
        owner.Cursor = Cursors.WaitCursor;
        try { a(); }
        catch (Exception ex) { Error(ex); }
        finally { owner.Cursor = old; }
    }

    /// <summary>Sets a splitter position once the container has its real size (setting it in the constructor is ignored).</summary>
    public static void SplitterWhenSized(SplitContainer split, int distance)
    {
        void Apply(object? s, EventArgs e)
        {
            if (split.Width < distance + split.Panel2MinSize + 50) return;
            try { split.SplitterDistance = distance; split.SizeChanged -= Apply; } catch (InvalidOperationException) { }
        }
        split.SizeChanged += Apply;
        split.HandleCreated += Apply;
    }

    public static Button Button(string text, EventHandler click, int width = 100)
    {
        var b = new Button { Text = text, Width = width, Height = 28, Margin = new Padding(3) };
        b.Click += click;
        return b;
    }
}

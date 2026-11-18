using System.IO;
using System.Text;
using System.Windows;
using DrawingRegister.Core;

namespace DrawingRegister.App;

/// <summary>
/// <c>DrawingRegister.exe --smoke-test report.txt</c>: starts the real window in a temporary data folder,
/// adds a project, uploads PDFs (files and a folder), selects a drawing, waits for the PDF preview,
/// writes PASS/FAIL to the report and exits. Used by CI to verify the installed app works on Windows.
/// </summary>
internal static class SmokeTest
{
    public static string? ReportPath { get; private set; }

    public static async Task RunAsync(MainWindow window, string? report)
    {
        ReportPath = report;
        // Watchdog: never leave CI waiting on a stuck window.
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ =>
        {
            Report(report, "FAIL\n[FAIL] watchdog: smoke test did not finish within 3 minutes");
            Environment.Exit(2);
        }, TaskScheduler.Default);
        var log = new StringBuilder();
        var ok = false;
        try
        {
            var vm = window.ViewModel;
            window.SuppressDialogs = true;
            await Task.Delay(500);
            var project = new ProjectRepository(new Database(vm.Paths.DatabaseFile))
                .Save(new Project { Code = "SMOKE", Name = "Smoke Test Project", Client = "CI" });
            vm.LoadProjects(project.Id);
            Check(vm.SelectedProject?.Code == "SMOKE", "project created and selected", log);

            var inbox = Directory.CreateDirectory(Path.Combine(vm.Paths.Root, "inbox", "Package", "02_Structural")).FullName;
            File.WriteAllBytes(Path.Combine(inbox, "SMK-ST-A01-GA-0001_RevA.pdf"), SamplePdf("REV A"));
            File.WriteAllBytes(Path.Combine(inbox, "SMK-ST-A01-GA-0001_RevB.pdf"), SamplePdf("REV B"));
            await vm.ImportPathsAsync([Path.GetDirectoryName(inbox)!]);
            Check(vm.Drawings.Count == 1, $"folder upload registered 1 drawing (got {vm.Drawings.Count})", log);
            var drawing = vm.Drawings[0];
            Check(drawing.CurrentRevision == "B" && drawing.RevisionCount == 2, "revisions A and B, current B", log);
            Check(drawing.Discipline == "Structural", "discipline from file name / folder", log);

            vm.SelectedDrawing = drawing;
            Check(vm.Revisions.Count == 2 && vm.PreviewPath is not null && File.Exists(vm.PreviewPath), "revision file stored", log);
            var preview = await window.WaitForPreviewAsync(TimeSpan.FromSeconds(30));
            log.AppendLine($"[INFO] PDF preview: {preview}");

            vm.SearchText = "GA-0001";
            Check(vm.Drawings.Count == 1, "search", log);
            vm.SearchText = "nothing-matches";
            Check(vm.Drawings.Count == 0, "search with no match", log);
            ok = !log.ToString().Contains("[FAIL]", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            log.AppendLine($"[FAIL] exception: {ex}");
        }

        Report(report, (ok ? "PASS\n" : "FAIL\n") + log);
        Application.Current.Shutdown(ok ? 0 : 1);
    }

    private static void Check(bool condition, string name, StringBuilder log) =>
        log.AppendLine($"[{(condition ? "PASS" : "FAIL")}] {name}");

    public static void Report(string? path, string text)
    {
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, text);
        }
    }

    /// <summary>A minimal valid one-page PDF with a line of text.</summary>
    public static byte[] SamplePdf(string text)
    {
        var content = $"BT /F1 24 Tf 72 720 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

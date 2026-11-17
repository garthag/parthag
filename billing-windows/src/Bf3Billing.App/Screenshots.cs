using System.Drawing.Imaging;
using Bf3Billing.Core;
using Bf3Billing.Core.Bsl;

namespace Bf3Billing.App;

/// <summary>
/// "Bf3Billing.exe --screenshots folder" draws every screen to PNG files, on a temporary data file with the
/// demo bills and the sample BSL workbooks. Used by CI to show the app; the user's data file is never touched.
/// </summary>
static class Screenshots
{
    public static int Run(string outDir)
    {
        Ui.SelfTestMode = true;
        Directory.CreateDirectory(outDir);
        var dir = Path.Combine(Path.GetTempPath(), "bf3-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var n = 0;
        try
        {
            var repo = new BillingRepository(new BillingDb(Path.Combine(dir, "billing.db")));

            using (var first = new MainForm(repo, new AppSettings()) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 900) })
            {
                first.Show();
                Settle();
                Save(first, outDir, ref n, "first-start");
                first.Close();
            }

            Seed.LoadDemo(repo);
            var (bom, con) = BslSample.Write(Path.Combine(dir, "bsl"));
            BslImport.Apply(repo, BslImport.Analyze(bom, con));

            using var main = new MainForm(repo, new AppSettings()) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 900) };
            main.Show();
            Settle();
            var tabs = main.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TabControl>()).Single();
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                Settle();
                if (page.Text == "BOM" && page.Controls[0] is BomView)
                {
                    // Pick the drawing with the most part lines so the lower grid shows something.
                    var grid = page.Controls[0].Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<DataGridView>().First();
                    if (grid.Rows.Count > 0) { grid.CurrentCell = grid.Rows[0].Cells[1]; Settle(); }
                }
                Save(main, outDir, ref n, "tab-" + page.Text);
            }

            var over = repo.BillSummaries().First(b => b.OverScopeLines > 0 && b.Remarks == "Sample bill");
            Editor(repo, main, over.Id, outDir, ref n, "bill-editor-bom-lines");
            var boq = repo.BillSummaries().First(b => b.BillNo == "RA-2" && b.Remarks.StartsWith("Imported"));
            Editor(repo, main, boq.Id, outDir, ref n, "bill-editor-boq-lines");

            var xlsx = Path.Combine(dir, "untidy.xlsx");
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Agencies");
                ws.Cell(1, 1).Value = "LIST OF AGENCIES – BF#3";
                ws.Cell(3, 1).Value = "Agency Name"; ws.Cell(3, 2).Value = "GST No."; ws.Cell(3, 3).Value = "Mobile"; ws.Cell(3, 4).Value = "Bank A/c";
                string[][] rows =
                {
                    new[] { "G.R. Enterprises", "20AABFG8971H1ZH", "9334240112", "SBI 1234" },
                    new[] { "Shakti Fabricators", "20AAAAA1111A1Z1", "98350 12345", "PNB 999" },
                    new[] { "Shakti  Fabricators", "20AAAAA1111A1Z1", "98350 12345", "PNB 999" },
                    new[] { "SHAKTI FABRICATORS", "", "70000 00000", "" },
                    new[] { "Om Sai Works", "", "", "HDFC 42" },
                };
                for (var r = 0; r < rows.Length; r++) for (var c = 0; c < 4; c++) ws.Cell(4 + r, 1 + c).Value = rows[r][c];
                wb.SaveAs(xlsx);
            }
            using (var w = new ImportWizardForm(repo) { AutoAddNewFields = true, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 900) })
            {
                w.Show(main);
                w.ChooseFile(xlsx);
                Settle();
                w.CheckAllForTest();
                Settle();
                Save(w, outDir, ref n, "import-from-excel-check");
                w.Close();
            }
            using (var f = new BslImportForm(repo, bom, con) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 860) })
            {
                f.Show(main);
                f.Check();
                Settle();
                Save(f, outDir, ref n, "import-bsl-workbooks-check");
                f.Close();
            }
            main.Close();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void Editor(BillingRepository repo, Form owner, long billId, string outDir, ref int n, string name)
    {
        using var f = new BillEditorForm(repo, billId, null) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1400, 860) };
        f.Show(owner);
        Settle();
        Save(f, outDir, ref n, name);
        f.Close();
    }

    static void Settle()
    {
        for (var i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(60); }
    }

    static void Save(Form f, string dir, ref int n, string name)
    {
        f.PerformLayout();
        Application.DoEvents();
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        var file = Path.Combine(dir, $"{++n:00}-{Ui.SafeFileName(name).Replace(' ', '-').ToLowerInvariant()}.png");
        bmp.Save(file, ImageFormat.Png);
    }
}

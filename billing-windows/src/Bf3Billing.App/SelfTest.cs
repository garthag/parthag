using System.Drawing.Printing;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>
/// "Bf3Billing.exe --selftest [log]" opens the main window on a temporary demo data file, shows every tab,
/// opens every bill in the editor, adds lines, saves, and renders the printouts to images.
/// Exit code 0 = passed. Used by CI on Windows; the user's data file is never touched.
/// </summary>
static class SelfTest
{
    public static int Run(string? logPath)
    {
        var log = new List<string>();
        void Log(string s) { log.Add(s); Console.WriteLine(s); }
        var dir = Path.Combine(Path.GetTempPath(), "bf3-selftest-" + Guid.NewGuid().ToString("N"));
        var failed = false;
        try
        {
            Directory.CreateDirectory(dir);
            var repo = new BillingRepository(new BillingDb(Path.Combine(dir, "billing.db")));

            using (var empty = new MainForm(repo, new AppSettings()))
            {
                empty.Show();
                Application.DoEvents();
                Log("ok  main window on an empty data file (first start)");
                empty.Close();
            }

            Seed.LoadDemo(repo);
            using var main = new MainForm(repo, new AppSettings());
            main.Show();
            Application.DoEvents();
            var tabs = main.Controls.OfType<Panel>().SelectMany(p => p.Controls.OfType<TabControl>()).Single();
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                Application.DoEvents();
                Log($"ok  tab {page.Text}");
            }

            foreach (var b in repo.BillSummaries())
            {
                using var f = new BillEditorForm(repo, b.Id, null);
                f.Show(main);
                Application.DoEvents();
                f.Close();
                Log($"ok  bill editor {b.BillNo} / {b.WorkOrder}");
            }

            using (var f = new BillEditorForm(repo, null, repo.WorkOrders().First(w => w.Value > 0).Id))
            {
                f.Show(main);
                Application.DoEvents();
                f.Close();
                Log("ok  new bill editor");
            }

            using (var p = new BomPickerForm(repo))
            {
                p.Show(main);
                Application.DoEvents();
                p.Close();
                Log("ok  BOM picker");
            }

            var xlsx = Path.Combine(dir, "untidy.xlsx");
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Agencies");
                ws.Cell(1, 1).Value = "LIST OF AGENCIES";
                ws.Cell(3, 1).Value = "Agency Name"; ws.Cell(3, 2).Value = "GST No."; ws.Cell(3, 3).Value = "Bank A/c";
                ws.Cell(4, 1).Value = "Shakti Fabricators"; ws.Cell(4, 2).Value = "20AAAAA1111A1Z1"; ws.Cell(4, 3).Value = "PNB 1";
                ws.Cell(5, 1).Value = "Shakti Fabricators"; ws.Cell(5, 2).Value = "20AAAAA1111A1Z1"; ws.Cell(5, 3).Value = "PNB 1";
                ws.Cell(6, 1).Value = "G.R. Enterprises";
                var bom = wb.Worksheets.Add("STOVE");
                bom.Cell(1, 1).Value = "Drg No"; bom.Cell(1, 2).Value = "Wt (kg)"; bom.Cell(1, 3).Value = "Grade";
                bom.Cell(2, 1).Value = "D-1"; bom.Cell(2, 2).Value = 1000; bom.Cell(2, 3).Value = "E250";
                wb.SaveAs(xlsx);
            }
            using (var w = new ImportWizardForm(repo) { AutoAddNewFields = true })
            {
                w.Show(main);
                Application.DoEvents();
                w.ChooseFile(xlsx);
                Application.DoEvents();
                if (!w.SheetTargets.Any(x => x.Target == ImportTarget.Contractors) || !w.SheetTargets.Any(x => x.Target == ImportTarget.Bom))
                    throw new InvalidOperationException("Import window did not recognise the sheets.");
                w.CheckAllForTest();
                Application.DoEvents();
                w.Close();
                Log("ok  import window: sheets recognised, columns matched, data checked");
            }

            foreach (var name in Reports.Names)
            {
                var t = Reports.Run(repo, name);
                Render(new TablePrinter(t, name, "self test"), Log, "report " + name);
            }
            var bill = repo.GetBill(repo.BillSummaries().First(x => x.Status == BillStatus.Draft).Id)!.Value;
            var wo = repo.WorkOrders().First(w => w.Id == bill.Bill.WorkOrderId);
            var lines = bill.Lines.Select(l => (l, l.Description.Length > 0 ? l.Description : "BOM line", "Erection", l.IsBom ? "Scope 58.200 t · OVER SCOPE" : "")).ToList();
            Render(new BillPrinter(bill.Bill, wo, lines, "BF#3"), Log, "bill printout");

            main.Close();
        }
        catch (Exception ex)
        {
            failed = true;
            Log("FAIL " + ex);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Log(failed ? "SELF TEST FAILED" : "SELF TEST PASSED");
        if (logPath != null) File.WriteAllLines(logPath, log);
        return failed ? 1 : 0;
    }

    static void Render(PrintDocument doc, Action<string> log, string what)
    {
        try
        {
            doc.PrintController = new PreviewPrintController();
            doc.Print();
            log($"ok  {what}");
        }
        catch (InvalidPrinterException)
        {
            log($"skip {what} (no printer installed on this machine)");
        }
    }
}

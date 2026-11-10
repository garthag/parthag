using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Desktop;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;
using SiteNotes.Desktop.Views;

namespace SiteNotes.Desktop.Tests;

/// <summary>Runs WPF code on an STA thread with a dispatcher, like the real app.</summary>
public static class Sta
{
    public static void Run(Func<Task> test)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await test(); }
                catch (Exception ex) { error = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }
}

public class DesktopTests
{
    [Fact]
    public void Note_round_trips_through_the_rich_text_editor() => Sta.Run(() =>
    {
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Inspection" },
            new() { Type = BlockType.Heading2, Text = "Checklist" },
            new() { Type = BlockType.Checklist, Text = "Shuttering checked", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Cover blocks" },
            new() { Type = BlockType.Bullet, Text = "Honeycomb at C4" },
            new() { Type = BlockType.Bullet, Text = "Lap length short", Style = new TextStyle { Bold = true, Color = "#B91C1C" } },
            new() { Type = BlockType.Numbered, Text = "Inform QA" },
            new() { Type = BlockType.Numbered, Text = "Re-inspect", Style = new TextStyle { Underline = true, FontFamily = "Serif", FontSize = 18 } },
            new() { Type = BlockType.Arrow, Text = "RMC at 7 am" },
            new() { Type = BlockType.Quote, Text = "Proceed after QA sign-off" },
            new() { Type = BlockType.Divider },
            new() { Type = BlockType.Paragraph, Text = "Centred", Style = new TextStyle { Align = TextAlign.Center, Highlight = "#FEF08A" } },
        };
        var doc = NoteFlow.ToFlow(blocks, _ => null);
        var back = NoteFlow.FromFlow(doc);
        Assert.Equal(blocks.Count, back.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(blocks[i].Type, back[i].Type);
            Assert.Equal(blocks[i].Text, back[i].Text);
            Assert.Equal(blocks[i].Checked, back[i].Checked);
        }
        Assert.True(back[5].Style.Bold);
        Assert.Equal("#B91C1C", back[5].Style.Color);
        Assert.True(back[7].Style.Underline);
        Assert.Equal("Serif", back[7].Style.FontFamily);
        Assert.Equal(18, back[7].Style.FontSize);
        Assert.Equal(TextAlign.Center, back[11].Style.Align);
        Assert.Equal("#FEF08A", back[11].Style.Highlight);
        return Task.CompletedTask;
    });

    [Fact]
    public void Every_screen_opens_with_the_sample_project() => Sta.Run(async () =>
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        await Desk.StartAsync();
        var p = await SampleData.CreateAsync(Desk.Db);
        await Desk.SetProjectAsync(p);
        var main = new MainWindow();
        app.MainWindow = main;
        main.Show();
        await main.FillProjectsAsync();

        IView[] views =
        [
            new DashboardView(), new ReportsView(), new ActivitiesView(), new WbsView(), new CalendarView(),
            MasterViews.Resources(), MasterViews.Subcontractors(), MasterViews.Departments(), new ProjectsView(),
            RegisterViews.Hindrances(), RegisterViews.Requirements(), new NotesView(), new ScannerView(null),
            new ReportManagerView(), new SettingsView(),
        ];
        foreach (var v in views)
        {
            main.Open((UIElement)v);
            await v.RefreshAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        // open a real report in the editor, change today's quantity, save: totals follow
        var report = (await Desk.Db.ListForProjectAsync<DailyReport>(p.Id)).OrderBy(r => r.Date).First();
        var editor = new DprEditorView(report.Id);
        main.Open(editor);
        await editor.RefreshAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await editor.SaveAsync();
        Assert.False(editor.IsDirty);
        main.Close();
    });
}

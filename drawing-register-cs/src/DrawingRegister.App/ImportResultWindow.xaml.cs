using System.Windows;
using DrawingRegister.Core;

namespace DrawingRegister.App;

public partial class ImportResultWindow : Window
{
    public ImportResultWindow(ImportSummary summary)
    {
        InitializeComponent();
        SummaryText.Text =
            $"{summary.Total} file(s): {summary.Created} new drawing(s), {summary.NewRevisions} new revision(s), " +
            $"{summary.Duplicates} duplicate(s) skipped, {summary.Failed} failed.";
        ItemsGrid.ItemsSource = summary.Items
            .OrderBy(i => i.Status == ImportStatus.Failed ? 0 : 1)
            .Select(i => new
            {
                Status = i.Status switch
                {
                    ImportStatus.Created => "New",
                    ImportStatus.NewRevision => "New revision",
                    ImportStatus.Duplicate => "Duplicate",
                    _ => "Failed",
                },
                Path = i.RelativePath ?? i.FileName,
                i.DrawingNumber,
                i.Revision,
                i.Discipline,
                i.Error,
            })
            .ToList();
    }
}

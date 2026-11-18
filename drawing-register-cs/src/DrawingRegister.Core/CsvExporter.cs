using System.Text;

namespace DrawingRegister.Core;

/// <summary>Exports a project's drawing register as CSV (opens directly in Excel).</summary>
public static class CsvExporter
{
    private static readonly string[] Header =
        ["Project", "Drawing No", "Title", "Discipline", "Area", "Type", "Revision", "Status", "Revisions", "Folder", "Remarks", "Updated"];

    public static void Export(Project project, IEnumerable<Drawing> drawings, string path)
    {
        // UTF-8 with BOM so Excel detects the encoding
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(',', Header.Select(Escape)));
        foreach (var d in drawings)
        {
            string?[] cells =
            [
                project.Code, d.DrawingNumber, d.Title, d.Discipline, d.Area, d.DrawingType, d.CurrentRevision, d.Status,
                d.RevisionCount.ToString(System.Globalization.CultureInfo.InvariantCulture), d.FolderPath, d.Remarks,
                d.UpdatedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ];
            writer.WriteLine(string.Join(',', cells.Select(Escape)));
        }
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // neutralise spreadsheet formula injection
        if (value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

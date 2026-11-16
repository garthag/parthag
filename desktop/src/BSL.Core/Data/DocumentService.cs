using System.Data;
using System.Text.RegularExpressions;

namespace BSL.Core.Data;

/// <summary>Notes and file attachments for any record (drawings, contractors, work orders, BOQ items ...).</summary>
public sealed class DocumentService(Database db)
{
    public const int MaxFileMb = 100;

    // ------------------------------------------------------------------ notes
    public DataTable Notes(string entity, int recordId) => db.Query(
        "SELECT Id, NoteDate, Category, IsImportant, NoteText, CreatedBy, CreatedOn FROM dbo.RecordNote " +
        "WHERE EntityName = @e AND RecordId = @r ORDER BY IsImportant DESC, NoteDate DESC, Id DESC", ("@e", entity), ("@r", recordId));

    public int AddNote(string entity, int recordId, string text, string category, bool important, DateTime? date = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The note is empty.");
        return Convert.ToInt32(db.Scalar(
            "INSERT dbo.RecordNote(EntityName, RecordId, NoteDate, Category, NoteText, IsImportant) OUTPUT INSERTED.Id " +
            "VALUES(@e, @r, @d, @c, @t, @i)",
            ("@e", entity), ("@r", recordId), ("@d", (date ?? DateTime.Today).Date), ("@c", category.Trim().ToUpperInvariant()),
            ("@t", text.Trim().ToUpperInvariant()), ("@i", important)));
    }

    public void UpdateNote(int id, string text, string category, bool important) =>
        db.Execute("UPDATE dbo.RecordNote SET NoteText = @t, Category = @c, IsImportant = @i WHERE Id = @id",
            ("@t", text.Trim().ToUpperInvariant()), ("@c", category.Trim().ToUpperInvariant()), ("@i", important), ("@id", id));

    public void DeleteNote(int id) => db.Execute("DELETE dbo.RecordNote WHERE Id = @id", ("@id", id));

    // ------------------------------------------------------------------ attachments
    public DataTable Attachments(string entity, int recordId) => db.Query(
        "SELECT Id, DocType, FileName, Revision, FileSizeKb, Description, UploadedBy, UploadedOn FROM dbo.Attachment " +
        "WHERE EntityName = @e AND RecordId = @r ORDER BY UploadedOn DESC, Id DESC", ("@e", entity), ("@r", recordId));

    public int Upload(string entity, int recordId, string path, string docType = "DOCUMENT", string? revision = null, string? description = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("File not found", path);
        if (fi.Length > MaxFileMb * 1024L * 1024L) throw new InvalidOperationException($"{fi.Name} is larger than {MaxFileMb} MB.");
        var bytes = File.ReadAllBytes(path);
        var id = Convert.ToInt32(db.Scalar(
            "INSERT dbo.Attachment(EntityName, RecordId, DocType, FileName, FileExt, FileSizeKb, Content, Revision, Description) " +
            "OUTPUT INSERTED.Id VALUES(@e, @r, @t, @f, @x, @k, @c, @rev, @d)",
            ("@e", entity), ("@r", recordId), ("@t", docType.ToUpperInvariant()), ("@f", fi.Name),
            ("@x", fi.Extension.TrimStart('.').ToUpperInvariant()), ("@k", (int)Math.Max(1, (fi.Length + 1023) / 1024)), ("@c", bytes),
            ("@rev", revision?.ToUpperInvariant()), ("@d", description?.ToUpperInvariant())));
        db.Audit(entity, recordId, "UPLOAD", fi.Name);
        return id;
    }

    /// <summary>Writes the stored file to <paramref name="folder"/> (temp folder by default) and returns the path.</summary>
    public string Extract(int attachmentId, string? folder = null)
    {
        var t = db.Query("SELECT FileName, Content FROM dbo.Attachment WHERE Id = @id", ("@id", attachmentId));
        if (t.Rows.Count == 0) throw new InvalidOperationException("Attachment not found.");
        folder ??= Path.Combine(Path.GetTempPath(), "BSLProjectControl", attachmentId.ToString());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, (string)t.Rows[0]["FileName"]);
        File.WriteAllBytes(path, (byte[])t.Rows[0]["Content"]);
        return path;
    }

    public void DeleteAttachment(int id)
    {
        db.Execute("DELETE dbo.Attachment WHERE Id = @id", ("@id", id));
        db.Audit("Attachment", id, "DELETE");
    }

    public int Count(string table, string entity, int recordId) => Convert.ToInt32(db.Scalar(
        $"SELECT COUNT(*) FROM dbo.{table} WHERE EntityName = @e AND RecordId = @r", ("@e", entity), ("@r", recordId)));

    // ------------------------------------------------------------------ bulk drawing upload
    public sealed record DrawingMatch(string File, int? DrawingId, string? DrawingNo, string? Revision);

    /// <summary>Normalised key: letters and digits only, upper case ("ERC/SMS/F-001" → "ERCSMSF001").</summary>
    public static string Key(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Matches files to drawings by drawing number contained in the file name (longest match wins),
    /// e.g. "ERC_SMS_TSSS_F-006 R1.pdf" → drawing ERC_SMS_TSSS_F-006, revision R1.
    /// </summary>
    public List<DrawingMatch> MatchDrawings(IEnumerable<string> files)
    {
        var drawings = db.Query("SELECT Id, DrawingNo FROM dbo.DrawingMaster").Rows.Cast<DataRow>()
            .Select(r => (Id: (int)r[0], No: (string)r[1], Key: Key((string)r[1])))
            .Where(d => d.Key.Length >= 5).OrderByDescending(d => d.Key.Length).ToList();
        var result = new List<DrawingMatch>();
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var key = Key(name);
            var hit = drawings.FirstOrDefault(d => key.Contains(d.Key, StringComparison.Ordinal));
            var rev = Regex.Match(name, @"(?:^|[\s_\-\.])R(?:EV)?[\s_\-\.]?(\d{1,2})(?:$|[\s_\-\.])", RegexOptions.IgnoreCase);
            result.Add(new DrawingMatch(f, hit.No != null ? hit.Id : null, hit.No, rev.Success ? "R" + int.Parse(rev.Groups[1].Value) : null));
        }
        return result;
    }
}

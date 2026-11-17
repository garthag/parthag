using System.IO.Compression;

namespace DrawingRegister.Core;

public sealed record ImportProgress(int Done, int Total, string Current);

public sealed class ImportResult
{
    public List<Drawing> Added { get; } = [];
    public List<string> Duplicates { get; } = [];
    public List<string> Errors { get; } = [];
    public int JunkSkipped { get; set; }

    /// <summary>Files that were attached to a register row created earlier from an Excel drawing list.</summary>
    public int AttachedToList { get; set; }

    public int Unclassified => Added.Count(d => d.TypeCode == DrawingType.Other.Code);
    public int Plants => Added.Select(d => d.Plant).Distinct().Count();
    public int Units => Added.Select(d => d.Plant + "|" + d.Unit).Distinct().Count();
    public int Types => Added.Select(d => d.TypeCode).Distinct().Count();
}

/// <summary>Unzips drawing packages (ZIPs inside ZIPs too) and files every drawing into the register and library.</summary>
public sealed class DrawingImporter(IDrawingStore store, DrawingLibrary library)
{
    public const int MaxNestedDepth = 2;

    sealed record Source(string Path, long Size, Func<Stream> Open);

    /// <summary>
    /// Picks the register row a file belongs to: same drawing number, then the same sheet (or the only
    /// sheet), preferring a row of the same revision that still has no file.
    /// </summary>
    static Drawing? FindTemplate(IEnumerable<Drawing> sameNumber, Drawing file, HashSet<int> claimed)
    {
        var all = sameNumber.ToList();
        var candidates = all;
        if (candidates.Count == 0) return null;
        if (file.Sheet.Length > 0)
        {
            var sameSheet = candidates.Where(x => x.Sheet == file.Sheet || x.Sheet.Length == 0).ToList();
            if (sameSheet.Count > 0) candidates = sameSheet;
        }
        // Judge "several sheets" on every row of the drawing, not just the ones still without a file.
        else if (all.Select(x => x.Sheet).Distinct().Count() > 1)
        {
            // Several sheets and the file name doesn't say which: only an exact single-sheet row qualifies.
            var single = candidates.Where(x => x.Sheet is "" or "1/1" or "1").ToList();
            if (single.Count == 0) return null;
            candidates = single;
        }
        // An empty row of the same revision is filled; any other row only lends its plant / unit / type.
        return candidates.FirstOrDefault(x => x.FileMissing && !claimed.Contains(x.Id)
                                              && x.Revision.Equals(file.Revision, StringComparison.OrdinalIgnoreCase))
               ?? candidates.OrderByDescending(x => x.RevRank).First();
    }

    /// <summary>
    /// Adds drawings read from Excel as register rows without files. Rows already in the register
    /// (same number, sheet and revision) are skipped.
    /// </summary>
    public (int Added, int AlreadyThere) ImportList(DrawingListResult list, string source)
    {
        var existing = store.All().Select(d => $"{d.NumberKey}|{d.Sheet}|{d.Revision.ToUpperInvariant()}").ToHashSet();
        var batch = store.StartBatch(source, list.Plant);
        int added = 0, already = 0;
        foreach (var d in list.Drawings)
        {
            if (!existing.Add($"{d.NumberKey}|{d.Sheet}|{d.Revision.ToUpperInvariant()}")) { already++; continue; }
            d.BatchId = batch;
            store.Add(d);
            added++;
        }
        store.FinishBatch(batch, added, already, 0);
        return (added, already);
    }

    public ImportResult Import(IReadOnlyList<string> files, string defaultPlant,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var sources = new List<Source>();
        var disposables = new List<IDisposable>();
        try
        {
            foreach (var file in files)
            {
                try
                {
                    if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var zip = ZipFile.OpenRead(file);
                        disposables.Add(zip);
                        AddZip(zip, "", 0, sources, disposables, result);
                    }
                    else if (DrawingParser.IsDrawingFile(file))
                    {
                        var path = file;
                        sources.Add(new Source(Path.GetFileName(file), new FileInfo(file).Length, () => File.OpenRead(path)));
                    }
                    else result.JunkSkipped++;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"{Path.GetFileName(file)}: could not open ({e.Message})");
                }
            }

            var batch = store.StartBatch(string.Join("; ", files.Select(Path.GetFileName)), defaultPlant);
            ImportSources(sources, defaultPlant, batch, result, progress, ct);
            store.FinishBatch(batch, result.Added.Count, result.Duplicates.Count, result.Errors.Count);
            return result;
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }
    }

    static void AddZip(ZipArchive zip, string prefix, int depth, List<Source> sources, List<IDisposable> disposables, ImportResult result)
    {
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue; // folder
            if (!DrawingParser.IsDrawingFile(name)) { result.JunkSkipped++; continue; }
            var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && depth < MaxNestedDepth)
            {
                try
                {
                    var ms = new MemoryStream();
                    using (var s = entry.Open()) s.CopyTo(ms);
                    ms.Position = 0;
                    var inner = new ZipArchive(ms, ZipArchiveMode.Read);
                    disposables.Add(inner);
                    AddZip(inner, path[..^4], depth + 1, sources, disposables, result);
                }
                catch (InvalidDataException e)
                {
                    result.Errors.Add($"{path}: could not unzip ({e.Message})");
                }
                continue;
            }
            var e2 = entry;
            sources.Add(new Source(path, entry.Length, () => e2.Open()));
        }
    }

    void ImportSources(List<Source> sources, string defaultPlant, int batch, ImportResult result,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        // A single wrapper folder (usually the ZIP's own name) carries no meaning.
        var root = DrawingParser.CommonRootFolder(sources.Select(s => s.Path));
        var existing = store.All();
        var seen = existing.Where(d => !d.FileMissing).Select(d => d.DuplicateKey).ToHashSet();
        var byNumber = existing.ToLookup(d => d.NumberKey);
        var claimed = new HashSet<int>();

        for (var i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = sources[i];
            progress?.Report(new ImportProgress(i, sources.Count, s.Path));

            var rel = root.Length == 0 ? s.Path : s.Path[(root.Length + 1)..];
            var p = DrawingParser.Parse(rel, defaultPlant);
            var d = new Drawing
            {
                DrawingNo = p.DrawingNo, Title = p.Title, Revision = p.Revision, Plant = p.Plant, Unit = p.Unit,
                TypeCode = p.Type.Code, Kind = p.Kind, Status = p.Status, FileName = p.FileName, Sheet = p.Sheet,
                SourcePath = p.Folder, SizeBytes = s.Size, BatchId = batch,
            };

            // A drawing already in the register (e.g. from the BOM) decides plant, unit, type and title.
            var template = FindTemplate(byNumber[d.NumberKey], d, claimed);
            if (template != null)
            {
                d.DrawingNo = template.DrawingNo;
                d.Plant = template.Plant;
                d.Unit = template.Unit;
                d.TypeCode = template.TypeCode;
                d.Sheet = template.Sheet;
                d.Agency = template.Agency;
                if (template.Title.Length > 0) d.Title = template.Title;
                if (d.Kind.Length == 0) d.Kind = template.Kind;
            }
            if (!seen.Add(d.DuplicateKey)) { result.Duplicates.Add(s.Path); continue; }

            // Same revision listed without a file: fill that row instead of adding a new one.
            var placeholder = template is { FileMissing: true } && !claimed.Contains(template.Id)
                              && template.Revision.Equals(d.Revision, StringComparison.OrdinalIgnoreCase)
                ? template : null;
            try
            {
                if (placeholder != null)
                {
                    d.Id = placeholder.Id;
                    d.Remarks = placeholder.Remarks;
                    d.Status = placeholder.Status;
                    d.BatchId = placeholder.BatchId;
                    d.SourcePath = placeholder.SourcePath.Length > 0 ? $"{placeholder.SourcePath}; ZIP: {p.Folder}".TrimEnd(' ', ':', ';') : p.Folder;
                }
                using (var content = s.Open()) library.Store(d, content);
                if (placeholder != null)
                {
                    store.Update(d);
                    claimed.Add(placeholder.Id);
                    result.AttachedToList++;
                }
                else store.Add(d);
                result.Added.Add(d);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Don't leave a file in the library that the register doesn't know about.
                try { library.Delete(d); } catch (IOException) { }
                seen.Remove(d.DuplicateKey);
                result.Errors.Add($"{s.Path}: {e.Message}");
            }
        }
        progress?.Report(new ImportProgress(sources.Count, sources.Count, ""));
    }
}

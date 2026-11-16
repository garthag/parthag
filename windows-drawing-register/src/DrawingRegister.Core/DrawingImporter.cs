using System.IO.Compression;

namespace DrawingRegister.Core;

public sealed record ImportProgress(int Done, int Total, string Current);

public sealed class ImportResult
{
    public List<Drawing> Added { get; } = [];
    public List<string> Duplicates { get; } = [];
    public List<string> Errors { get; } = [];
    public int JunkSkipped { get; set; }

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
        var seen = store.All().Select(d => d.DuplicateKey).ToHashSet();

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
                TypeCode = p.Type.Code, Kind = p.Kind, Status = p.Status, FileName = p.FileName,
                SourcePath = p.Folder, SizeBytes = s.Size, BatchId = batch,
            };
            if (!seen.Add(d.DuplicateKey)) { result.Duplicates.Add(s.Path); continue; }
            try
            {
                using (var content = s.Open()) library.Store(d, content);
                store.Add(d);
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

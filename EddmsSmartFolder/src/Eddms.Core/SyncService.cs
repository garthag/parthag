namespace Eddms.Core;

public sealed class SyncOptions
{
    public required string LoginName { get; init; }
    public required string Password { get; init; }
    public string BaseUrl { get; init; } = WrenchClient.DefaultBaseUrl;

    /// <summary>Download the drawing files as well as the data.</summary>
    public bool DownloadFiles { get; init; }

    /// <summary>Root folder for downloaded drawings, laid out like the Smart Folder tree.</summary>
    public string DownloadRoot { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDMS Drawings");
}

public sealed record SyncResult(int Folders, int Documents, int Downloaded, int DownloadFailed, IReadOnlyList<string> Warnings);

/// <summary>Logs in, walks every Smart Folder, stores everything in SQL and (optionally) downloads drawings.</summary>
public sealed class SyncService
{
    private readonly EddmsDatabase _db;

    public SyncService(EddmsDatabase db) => _db = db;

    public async Task<SyncResult> RunAsync(SyncOptions options, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new SyncProgress("Preparing database..."));
        await _db.EnsureSchemaAsync(ct);
        var runId = _db.StartSyncRun(options.LoginName);
        var warnings = new List<string>();
        int folderCount = 0, docCount = 0, downloaded = 0, failed = 0;

        try
        {
            using var client = new WrenchClient(options.BaseUrl);
            progress?.Report(new SyncProgress($"Signing in to EDMS as {options.LoginName}..."));
            await client.LoginAsync(options.LoginName, options.Password, ct);

            progress?.Report(new SyncProgress("Reading Smart Folder tree..."));
            var roots = await client.GetFolderTreeAsync(progress, ct);
            var all = roots.SelectMany(r => r.SelfAndDescendants()).ToList();
            var syncedOn = DateTime.Now;
            _db.SaveFolderTree(all, syncedOn);
            folderCount = all.Count;

            // Folders without children hold the documents. Parents are shown as the sum of their children.
            var leafFolders = all.Where(f => !f.HasChild).ToList();
            var toDownload = new List<(SmartFolderInfo Folder, WrenchDocument Doc)>();
            var seenDocs = new HashSet<long>();

            for (int i = 0; i < leafFolders.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = leafFolders[i];
                progress?.Report(new SyncProgress($"Reading documents: {f.FullPath}", i, leafFolders.Count));
                try
                {
                    var contents = await client.GetFolderDocumentsAsync(f, ct);
                    _db.SaveFolderContents(f, contents, syncedOn);
                    foreach (var d in contents.Documents)
                        if (seenDocs.Add(d.DocId))
                            toDownload.Add((f, d));
                }
                catch (WrenchLoginException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"{f.FullPath}: {ex.Message}");
                }
            }
            docCount = seenDocs.Count;

            if (options.DownloadFiles)
            {
                for (int i = 0; i < toDownload.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (f, d) = toDownload[i];
                    int rev = d.InternalRevisionNumber ?? 0, ver = d.VersionNumber ?? 0;
                    if (_db.GetDownloadedPath(d.DocId, rev, ver) != null) continue; // already have this revision

                    progress?.Report(new SyncProgress($"Downloading {d.DocNumber} Rev {d.RevisionNumber}", i, toDownload.Count));
                    try
                    {
                        var folder = Path.Combine(new[] { options.DownloadRoot }
                            .Concat(f.FullPath.Split(" / ").Select(WrenchClient.SafeFileName)).ToArray());
                        var path = await client.DownloadDocumentAsync(d.DocId, f.FolderType, folder, ct);
                        _db.SetDownloadedPath(d.DocId, rev, ver, path);
                        downloaded++;
                    }
                    catch (WrenchLoginException) { throw; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        warnings.Add($"Download {d.DocNumber}: {ex.Message}");
                    }
                }
            }

            var summary = $"{folderCount} folders, {docCount} documents" +
                          (options.DownloadFiles ? $", {downloaded} files downloaded, {failed} failed" : "");
            _db.FinishSyncRun(runId, true, folderCount, docCount,
                warnings.Count == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, warnings));
            progress?.Report(new SyncProgress("Done: " + summary, 1, 1));
            return new SyncResult(folderCount, docCount, downloaded, failed, warnings);
        }
        catch (Exception ex)
        {
            _db.FinishSyncRun(runId, false, folderCount, docCount, ex.Message);
            throw;
        }
    }
}

using SiteNotes.Core.Data;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.Core;

/// <summary>A page of a scan being edited: the original photo, rotation and remarks.</summary>
public class ScanPageInput
{
    public string RawPath { get; set; } = "";
    public int Rotation { get; set; }
    public string Remarks { get; set; } = "";
    /// <summary>Existing MediaItem id when re-saving a document.</summary>
    public string? MediaId { get; set; }
}

/// <summary>
/// Saves a scanned document: every page is cleaned up and stored as its own image (in parallel on all
/// cores), then all pages are combined into one PDF. Filed in Scans › yyyy-MM-dd unless a folder is given.
/// Originals are kept (not synced) so the filter can be changed later.
/// </summary>
public class ScanService(LocalDatabase db, string rawFolder)
{
    public async Task<string> AutoFolderAsync(string projectId, DateTime day)
    {
        var all = await db.ListForProjectAsync<Folder>(projectId);
        var root = all.FirstOrDefault(f => f.ParentId == "" && f.Kind == FolderKind.Scans && f.Name == "Scans");
        if (root == null)
        {
            root = new Folder { ProjectId = projectId, Name = "Scans", Kind = FolderKind.Scans, Color = "#1F4E79" };
            await db.SaveAsync(root);
        }
        var name = day.ToString("yyyy-MM-dd");
        var dayFolder = all.FirstOrDefault(f => f.ParentId == root.Id && f.Name == name);
        if (dayFolder == null)
        {
            dayFolder = new Folder { ProjectId = projectId, ParentId = root.Id, Name = name, Kind = FolderKind.Scans };
            await db.SaveAsync(dayFolder);
        }
        return dayFolder.Id;
    }

    public async Task<ScanDocument> SaveAsync(ScanDocument doc, IReadOnlyList<ScanPageInput> pages, ScanFilter filter)
    {
        if (pages.Count == 0) throw new InvalidOperationException("A scan needs at least one page.");
        Directory.CreateDirectory(rawFolder);
        if (doc.FolderId == "") doc.FolderId = await AutoFolderAsync(doc.ProjectId, DateTime.Today);
        doc.Filter = filter;
        doc.PageCount = pages.Count;
        if (string.IsNullOrWhiteSpace(doc.Title)) doc.Title = $"Scan {DateTime.Now:dd-MMM-yyyy HH:mm}";

        var old = await db.ListAsync<MediaItem>(x => x.OwnerId == doc.Id);
        var media = new MediaItem[pages.Count];
        await Parallel.ForAsync(0, pages.Count, async (i, ct) =>
        {
            var p = pages[i];
            var item = old.FirstOrDefault(m => m.Id == p.MediaId) ?? new MediaItem();
            var name = $"scan_{doc.Id}_{Guid.NewGuid():N}.jpg";
            var path = db.MediaPath(name);
            var raw = Path.Combine(rawFolder, item.Id + ".jpg");
            await Task.Run(() =>
            {
                if (!ScanProcessor.Process(p.RawPath, path, filter, p.Rotation, 2200))
                    throw new InvalidOperationException($"Page {i + 1} could not be read as an image.");
                if (!string.Equals(Path.GetFullPath(p.RawPath), Path.GetFullPath(raw), StringComparison.OrdinalIgnoreCase)) File.Copy(p.RawPath, raw, true);
            }, ct);
            item.ProjectId = doc.ProjectId;
            item.OwnerType = MediaOwner.Scan;
            item.OwnerId = doc.Id;
            item.Kind = MediaKind.ScanPage;
            item.FileName = name;
            item.PageNo = i + 1;
            item.Remarks = p.Remarks;
            item.Caption = $"{doc.Title} – page {i + 1}";
            item.FileSize = new FileInfo(path).Length;
            item.IsUploaded = false;
            p.MediaId = item.Id;
            p.RawPath = raw;
            p.Rotation = 0;
            media[i] = item;
        });
        await db.SaveAllAsync(media);
        foreach (var gone in old.Where(o => media.All(m => m.Id != o.Id))) await db.DeleteAsync(gone);

        doc.PdfFileName = $"scan_{doc.Id}.pdf";
        var pdfPath = db.MediaPath(doc.PdfFileName);
        var pagePaths = media.Select(m => db.MediaPath(m.FileName)).ToList();
        await Task.Run(() =>
        {
            using var fs = File.Create(pdfPath);
            PdfReports.ScanPages(fs, doc.Title, pagePaths);
        });
        await db.SaveAsync(doc);
        return doc;
    }

    /// <summary>Pages of a saved document, ready to edit again (originals if still on this device).</summary>
    public async Task<List<ScanPageInput>> LoadPagesAsync(ScanDocument doc) =>
        (await db.ListAsync<MediaItem>(x => x.OwnerId == doc.Id)).OrderBy(m => m.PageNo).Select(m =>
        {
            var raw = Path.Combine(rawFolder, m.Id + ".jpg");
            return new ScanPageInput { MediaId = m.Id, Remarks = m.Remarks, RawPath = File.Exists(raw) ? raw : db.MediaPath(m.FileName) };
        }).ToList();
}

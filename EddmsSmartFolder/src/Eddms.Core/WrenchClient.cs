using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eddms.Core;

/// <summary>
/// Talks to the Wrench EDMS web site (eddms.cetapps.in) the same way the browser does:
/// two-step login, Smart Folder tree, the document grid of each folder, and file download.
/// </summary>
public sealed class WrenchClient : IDisposable
{
    public const string DefaultBaseUrl = "https://eddms.cetapps.in";
    private const string SmartFolderPage = "/WrenchWeb/SmartFolder/DisplaySmartFolder?folderType=-1";

    /// <summary>Columns used when a folder's page does not list its grid columns.</summary>
    private static readonly FolderColumn[] FallbackColumns =
    {
        new("DocNumber", "Document Number"), new("VendorDocNumber", "Vendor Document Number"),
        new("DocDescription", "Document Description"), new("UserRevisionNumber", "Revision Number"),
        new("SheetNo", "Sheet Number"), new("LifeCycleStatus", "Life Cycle Status/Issue Purpose"),
        new("InternalApprovalStatusCode", "Internal Approval Status Code"),
        new("InternalApprovalStatusDescripion", "Internal Approval Status Description"),
        new("CurrentStage", "Current Stage"), new("CurrentStageUser", "Current Stage User"),
        new("DocStatus", "Document Status"), new("FileName", "File Name"), new("CreatedOn", "Created On"),
        new("LastEditedOn", "Last Edited On"), new("WorkflowTeam", "Workflow Team"),
        new("FinalReleaseOn", "Final Released On"), new("ProjectNumber", "Project ID"),
    };

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string? _ajaxToken;

    public string BaseUrl { get; }
    public string? LoginName { get; private set; }

    public WrenchClient(string baseUrl = DefaultBaseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EddmsSmartFolder/1.0");
    }

    // ------------------------------------------------------------------ login

    public async Task LoginAsync(string loginName, string password, CancellationToken ct = default)
    {
        LoginName = loginName;
        var returnUrl = Uri.EscapeDataString(SmartFolderPage);

        // Step 1: login page -> post the login name.
        var html = await GetStringAsync($"/WrenchWeb/AccessControl/RedirectToLogin?returnUrl={returnUrl}", ct);
        var form = ReadLoginForm(html) ?? throw new WrenchLoginException("Could not find the EDMS login form.");
        form["LoginName"] = loginName;
        form["hiddenServerId"] = "1";
        AddTimeZone(form);
        html = await PostFormAsync("/WrenchWeb/AccessControl/Login", form, ct);

        // Step 2: password page -> post the password.
        form = ReadLoginForm(html);
        if (form == null || !form.ContainsKey("Password"))
            throw new WrenchLoginException(ReadLoginMessage(html) ?? $"Login name '{loginName}' was not accepted.");
        form["Password"] = password;
        form["hiddenServerId"] = "1";
        AddTimeZone(form);
        html = await PostFormAsync("/WrenchWeb/AccessControl/Login", form, ct);

        _ajaxToken = Regex.Match(html, @"getAjaxHeaderToken=function\(\)\{return""([^""]+)""").Groups[1].Value;
        if (ReadLoginForm(html) != null || string.IsNullOrEmpty(_ajaxToken))
            throw new WrenchLoginException(ReadLoginMessage(html) ?? "Login failed. Check the login name and password.");

        _http.DefaultRequestHeaders.Remove("X-Requested-With");
        _http.DefaultRequestHeaders.Remove("__RequestVerificationToken");
        _http.DefaultRequestHeaders.Remove("RequestVerificationToken");
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.Add("__RequestVerificationToken", _ajaxToken);
        _http.DefaultRequestHeaders.Add("RequestVerificationToken", _ajaxToken);
    }

    private static void AddTimeZone(Dictionary<string, string> form)
    {
        var offsetMinutes = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes; // JS getTimezoneOffset()
        form["ClientTimeZoneOffSet"] = offsetMinutes.ToString();
        var tz = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz, out var iana)) tz = iana;
        form["ClientTimeZoneOffSetId"] = tz;
    }

    /// <summary>Reads every input of the login form (first value wins, checkboxes skipped).</summary>
    private static Dictionary<string, string>? ReadLoginForm(string html)
    {
        var start = html.IndexOf("action=\"/WrenchWeb/AccessControl/Login\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = html.IndexOf("</form>", start, StringComparison.OrdinalIgnoreCase);
        var formHtml = end > start ? html[start..end] : html[start..];

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(formHtml, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attrs = ReadAttributes(input.Value);
            if (!attrs.TryGetValue("name", out var name) || string.IsNullOrEmpty(name)) continue;
            if (attrs.TryGetValue("type", out var type) && type.Equals("checkbox", StringComparison.OrdinalIgnoreCase)) continue;
            if (!fields.ContainsKey(name))
                fields[name] = attrs.TryGetValue("value", out var v) ? v : "";
        }
        return fields;
    }

    private static Dictionary<string, string> ReadAttributes(string tag)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Regex.Matches(tag, @"([\w\-:]+)\s*=\s*(?:""([^""]*)""|'([^']*)')"))
            d[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value);
        return d;
    }

    private static string? ReadLoginMessage(string html)
    {
        var m = Regex.Match(html, @"id=""DisplayMessage""[^>]*>(.*?)</", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = m.Success ? WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ------------------------------------------------------------- folders

    /// <summary>Loads the full Smart Folder tree, keeping the site's order.</summary>
    public async Task<List<SmartFolderInfo>> GetFolderTreeAsync(IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var rootRequest = new JsonObject
        {
            ["FolderType"] = -1, ["bCurrentLoginOnly"] = true, ["IsPinToSF"] = 0, ["FolderSubType"] = -1,
            ["IsProjectLinkedSmartFolder"] = false, ["IsSmartFolderForOperationLog"] = false,
            ["LinkFolderToDashboardTile"] = false, ["CachedFolderId"] = 0, ["ObjectType"] = 0,
            ["IsRuleCopy"] = false, ["IsCopySmartFolderForProject"] = false, ["DashboardTabId"] = 0,
        };
        var roots = await ReadFolderListAsync(rootRequest, ct);
        for (int i = 0; i < roots.Count; i++)
            await FillFolderAsync(roots[i], null, 0, i, progress, ct);
        return roots;
    }

    private async Task FillFolderAsync(SmartFolderInfo f, SmartFolderInfo? parent, int depth, int order,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        f.ParentFolderId = parent?.FolderId;
        f.Depth = depth;
        f.SortOrder = order;
        f.FullPath = parent == null ? f.FolderName : parent.FullPath + " / " + f.FolderName;
        progress?.Report(new SyncProgress("Folder: " + f.FullPath));
        if (!f.HasChild || depth > 20) return;

        var req = new JsonObject
        {
            ["IsProjectLinkedSmartFolder"] = false, ["isLoadOnDemand"] = true, ["bCurrentLoginOnly"] = "True",
            ["FolderType"] = f.FolderType, ["FolderSubType"] = -1,
            ["SelectedSmartFolder"] = new JsonObject { ["FolderID"] = f.FolderId, ["FolderType"] = f.FolderType },
            ["ObjectType"] = 0, ["IsPinToSF"] = 0, ["IsRuleCopy"] = false, ["IsSmartFolderForOperationLog"] = false,
            ["DashboardTabId"] = 0, ["LinkFolderToDashboardTile"] = false,
        };
        var seen = new HashSet<long> { f.FolderId };
        foreach (var child in await ReadFolderListAsync(req, ct))
        {
            if (!seen.Add(child.FolderId)) continue;
            f.Children.Add(child);
        }
        for (int i = 0; i < f.Children.Count; i++)
            await FillFolderAsync(f.Children[i], f, depth + 1, i, progress, ct);
    }

    private async Task<List<SmartFolderInfo>> ReadFolderListAsync(JsonObject request, CancellationToken ct)
    {
        using var doc = await PostJsonAsync("/WrenchWeb/SmartFolder/GetSmartFoldersOnDemand", request, ct);
        var list = new List<SmartFolderInfo>();
        foreach (var name in new[] { "SmartFolderList", "SmartFolderChildList" })
        {
            if (!doc.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in arr.EnumerateArray())
            {
                list.Add(new SmartFolderInfo
                {
                    FolderId = e.GetProperty("FolderID").GetInt64(),
                    FolderName = e.GetProperty("FolderName").GetString() ?? "",
                    FolderType = GetInt(e, "FolderType"),
                    FolderCriteriaId = GetInt(e, "FolderCriteriaId"),
                    FolderSubType = GetInt(e, "FolderSubType", -1),
                    DisplayCount = GetInt(e, "DisplayCount"),
                    HasChild = GetInt(e, "HasChild") == 1,
                    IsPublished = GetInt(e, "IsPublished") == 1,
                });
            }
        }
        return list;
    }

    private static int GetInt(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    // ------------------------------------------------------------ documents

    public sealed record FolderContents(IReadOnlyList<FolderColumn> Columns, IReadOnlyList<WrenchDocument> Documents, int TotalCount);

    /// <summary>Reads every document row of one Smart Folder (all pages).</summary>
    public async Task<FolderContents> GetFolderDocumentsAsync(SmartFolderInfo folder, CancellationToken ct = default)
    {
        // Opening the folder view selects it in the server session and tells us the grid view + columns.
        var page = await GetStringAsync(
            $"/WrenchWeb/SmartFolder/DisplaySmartFolderPartialView?FolderCriteriaId={folder.FolderCriteriaId}" +
            $"&FolderType={folder.FolderType}&FolderId={folder.FolderId}&FolderSubType={folder.FolderSubType}", ct);

        var viewId = Regex.Match(page, @"SelectedViewId\s*=\s*'(\d+)'").Groups[1].Value;
        if (string.IsNullOrEmpty(viewId)) viewId = "0";

        var columns = Regex.Matches(page, @"\\""title\\"":\\""(.*?)\\"",\\""field\\"":\\""(.*?)\\""")
            .Select(m => new FolderColumn(Unescape(m.Groups[2].Value), Unescape(m.Groups[1].Value)))
            .GroupBy(c => c.Field).Select(g => g.First())
            .ToList();
        if (columns.Count == 0) columns = FallbackColumns.ToList();

        // Some system folders (e.g. Sent Items) reject columns they don't have, so fall back step by step.
        var attempts = new List<List<FolderColumn>> { columns };
        attempts.Add(BasicColumns.ToList());
        attempts.Add(new List<FolderColumn>());
        for (int i = 0; ; i++)
        {
            try { return await ReadAllRowsAsync(folder, viewId, attempts[i], ct); }
            catch (HttpRequestException) when (i + 1 < attempts.Count) { /* try a smaller column set */ }
        }
    }

    private static readonly FolderColumn[] BasicColumns =
    {
        new("DocNumber", "Document Number"), new("DocDescription", "Document Description"),
        new("UserRevisionNumber", "Revision Number"), new("SheetNo", "Sheet Number"),
        new("DocStatus", "Document Status"), new("FileName", "File Name"),
        new("CreatedOn", "Created On"), new("LastEditedOn", "Last Edited On"),
    };

    private async Task<FolderContents> ReadAllRowsAsync(SmartFolderInfo folder, string viewId, List<FolderColumn> columns, CancellationToken ct)
    {
        var folderString = $"{folder.FolderType}Atom{folder.FolderId}Atom{folder.FolderCriteriaId}Atom{folder.DisplayCount}AtomfalseAtom{folder.FolderSubType}";
        const int pageSize = 500;
        var docs = new List<WrenchDocument>();
        int total = 0;
        for (int pageNo = 1; ; pageNo++)
        {
            var req = new JsonObject
            {
                ["take"] = pageSize, ["skip"] = (pageNo - 1) * pageSize, ["page"] = pageNo, ["pageSize"] = pageSize,
                ["sort"] = new JsonArray(), ["group"] = new JsonArray(),
                ["FolderString"] = folderString, ["GetCount"] = true,
                ["IsFilterSavedAgainstView"] = false, ["IsFilterStripClear"] = false,
                ["ViewId"] = viewId, ["ShowGroupCount"] = false,
                ["visibleColumns"] = new JsonArray(columns.Select(c => (JsonNode)JsonValue.Create(c.Field)!).ToArray()),
            };
            using var json = await PostJsonAsync("/WrenchWeb/SmartFolder/GetSmartFolderObjectDetails", req, ct);
            total = json.RootElement.TryGetProperty("TotalCount", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            if (!json.RootElement.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array) break;

            int n = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (WrenchDocument.ReadId(row) == null) continue;
                docs.Add(WrenchDocument.FromRow(row, columns));
                n++;
            }
            if (n < pageSize || docs.Count >= total) break;
        }
        return new FolderContents(columns, docs, Math.Max(total, docs.Count));
    }

    private static string Unescape(string s)
    {
        try { return Regex.Unescape(s); } catch { return s; }
    }

    // ------------------------------------------------------------- download

    /// <summary>
    /// Downloads the current revision's file of a document into <paramref name="targetFolder"/>.
    /// Returns the saved path, or throws with the site's reason.
    /// </summary>
    public async Task<string> DownloadDocumentAsync(long docId, int folderType, string targetFolder, CancellationToken ct = default)
    {
        var ids = new JsonArray(new JsonObject
        {
            ["ObjectId"] = docId, ["ObjectType"] = folderType, ["ObjectRevisionNumber"] = -1, ["ObjectVersionNumber"] = -1,
        });
        (await PostJsonAsync("/WrenchWeb/Document/PrevalidateDownloadDocument", ids.DeepClone(), ct)).Dispose();

        var req = new JsonObject { ["objectOperationParameters"] = ids, ["ViewMode"] = 0, ["ApplyFormView"] = true };
        string serverPath;
        using (var res = await PostJsonAsync("/WrenchWeb/Document/DownloadDocument", req, ct))
        {
            var root = res.RootElement;
            serverPath = root.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(serverPath))
                throw new InvalidOperationException("EDMS refused the download: " + DescribeError(root));
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["fileDownload.filePath"] = serverPath });
        using var fileResponse = await _http.PostAsync("/WrenchWeb/FileManager/DownloadFileFromServerTempFolder", form, ct);
        fileResponse.EnsureSuccessStatusCode();

        var fileName = fileResponse.Content.Headers.ContentDisposition?.FileNameStar
                       ?? fileResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? Path.GetFileName(serverPath.Replace('\\', '/'));
        Directory.CreateDirectory(targetFolder);
        var path = Path.Combine(targetFolder, SafeFileName(fileName));
        await using (var fs = File.Create(path))
            await fileResponse.Content.CopyToAsync(fs, ct);
        return path;
    }

    private static string DescribeError(JsonElement root)
    {
        foreach (var key in new[] { "error", "Error", "warning" })
        {
            if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) continue;
            var parts = new List<string>();
            if (e.TryGetProperty("ErrorMessage", out var msg) && msg.ValueKind == JsonValueKind.String) parts.Add(msg.GetString()!);
            if (e.TryGetProperty("errorList", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("HeaderName", out var h) && h.GetString() == "Reason" &&
                        item.TryGetProperty("FieldValue", out var fv) && fv.ValueKind == JsonValueKind.Array)
                        parts.AddRange(fv.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (parts.Count > 0) return string.Join(" ", parts).Replace("\n", " ").Trim();
        }
        return "no file returned";
    }

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }))
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "_" : name;
    }

    // ------------------------------------------------------------- plumbing

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var r = await _http.GetAsync(url, ct);
        r.EnsureSuccessStatusCode();
        var html = await r.Content.ReadAsStringAsync(ct);
        ThrowIfLoggedOut(r, html);
        return html;
    }

    private async Task<string> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var r = await _http.PostAsync(url, content, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonDocument> PostJsonAsync(string url, JsonNode body, CancellationToken ct)
    {
        using var r = await _http.PostAsync(url, JsonContent.Create(body), ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        ThrowIfLoggedOut(r, text);
        if (!r.IsSuccessStatusCode)
            throw new HttpRequestException($"EDMS {url} returned {(int)r.StatusCode}: {Truncate(text, 300)}");
        return JsonDocument.Parse(text);
    }

    private void ThrowIfLoggedOut(HttpResponseMessage r, string body)
    {
        if (_ajaxToken == null) return; // still logging in
        var path = r.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        if (path.Contains("/AccessControl/", StringComparison.OrdinalIgnoreCase) || body.Contains("action=\"/WrenchWeb/AccessControl/Login\""))
            throw new WrenchLoginException("The EDMS session has expired. Please sign in again.");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    public void Dispose() => _http.Dispose();
}

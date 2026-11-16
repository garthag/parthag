using System.Text.Json;
using Eddms.Core;

/// <summary>
/// Offline database test (used by the Windows build): creates the schema, saves a sample folder
/// tree with rows shaped exactly like the EDMS grid returns, runs a second "sync" with a new
/// revision, and checks every register query.
/// </summary>
internal static class SelfTest
{
    private static readonly FolderColumn[] Columns =
    {
        new("DocNumber", "Document Number"), new("CHAR_124_000", "Document Type"), new("CHAR_132_000", "Discipline"),
        new("CHAR_114_000", "Package"), new("CHAR_127_000", "Scheduled Date of Submission"),
        new("CHAR_267_000", "Responded Date"), new("CHAR_268_000", "Vendor Submission date"),
    };

    private const string Row1 = """
        {"DocID":10204408,"DocNumber":"BSL-BF3-REV-SMS-D-R-C-1200","DocInternalRevisionNumber":0,"DocVersionNumber":1,
         "DocStatus":"Work In Progress","Attachments":"<img>","VendorDocNumber":"","DocDescription":"Foundation for walkway \nbetween buildings",
         "UserRevisionNumber":"0","SheetNo":"1","LifeCycleStatus":"Reference","InternalApprovalStatusCode":"KFRN",
         "InternalApprovalStatusDescripion":"Kept For Reference with Noting","CurrentStage":"VENDOR RESUBMISSION TO SAIL",
         "CurrentStageUser":"SMSBSLBF3","CreatedOn":"/Date(1751000519000)/","LastEditedOn":"/Date(1752669268000)/",
         "FileName":"BSL-BF3-REV-SMS-D-R-C-1200R=0_A_001.pdf","WorkflowTeam":"VENDOR-DRAWING-NEW",
         "LatestReleasedTransmittalReleasedOn":null,"FinalReleaseOn":null,"ProjectNumber":"4906",
         "CHAR_132_000":"CE","CHAR_124_000":"DE","CHAR_114_000":"Package 1",
         "CHAR_267_000":"/Date(1752729840000)/","CHAR_127_000":"/Date(1751221800000)/","CHAR_268_000":"/Date(1751265540000)/"}
        """;

    private const string Row2 = """
        {"DocID":10130796,"DocNumber":"BSL-BF3-REV-SMS-D-R-C-1025","DocInternalRevisionNumber":2,"DocVersionNumber":3,
         "DocStatus":"Released","Attachments":"","DocDescription":"Pile layout","UserRevisionNumber":"2","SheetNo":"1",
         "LifeCycleStatus":"Approval","InternalApprovalStatusCode":"KFR","InternalApprovalStatusDescripion":"Kept For Reference",
         "CurrentStage":null,"FileName":"BSL-BF3-REV-SMS-D-R-C-1025R=2_H_001.pdf","FinalReleaseOn":"/Date(1752729840000)/",
         "CHAR_132_000":"CE","CHAR_268_000":"/Date(1751265540000)/"}
        """;

    public static async Task<int> RunAsync(string connectionString)
    {
        var db = new EddmsDatabase(connectionString);
        Console.WriteLine("Creating schema...");
        await db.EnsureSchemaAsync();
        await db.EnsureSchemaAsync(); // must be re-runnable

        var root = new SmartFolderInfo { FolderId = 9_000_001, FolderName = "SELFTEST Vendor Drawings", HasChild = true, FullPath = "SELFTEST Vendor Drawings" };
        var civil = new SmartFolderInfo { FolderId = 9_000_002, FolderName = "01-Civil", ParentFolderId = root.FolderId, Depth = 1, FullPath = "SELFTEST Vendor Drawings / 01-Civil" };
        root.Children.Add(civil);
        var now = DateTime.Now;
        db.SaveFolderTree(root.SelfAndDescendants(), now);

        var docs = new[] { Row1, Row2 }.Select(r => WrenchDocument.FromRow(JsonDocument.Parse(r).RootElement, Columns)).ToList();
        db.SaveFolderContents(civil, new WrenchClient.FolderContents(Columns, docs, docs.Count), now);

        // Second sync: drawing 1200 moves to revision 1 -> revision register must keep revision 0.
        var row1Rev1 = Row1.Replace("\"DocInternalRevisionNumber\":0", "\"DocInternalRevisionNumber\":1")
                           .Replace("\"UserRevisionNumber\":\"0\"", "\"UserRevisionNumber\":\"1\"");
        var docs2 = new[] { row1Rev1, Row2 }.Select(r => WrenchDocument.FromRow(JsonDocument.Parse(r).RootElement, Columns)).ToList();
        db.SaveFolderContents(civil, new WrenchClient.FolderContents(Columns, docs2, docs2.Count), now.AddMinutes(1));
        db.SetDownloadedPath(10130796, 2, 3, @"C:\EDMS Drawings\test.pdf");

        int failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok) failures++;
        }

        var folders = db.LoadFolders();
        Check("folders saved with parent link", folders.Any(f => f.FolderId == civil.FolderId && f.ParentFolderId == root.FolderId && f.DocumentCount == 2));

        var all = db.LoadDocuments(root.FolderId, includeSubFolders: true, search: null);
        Check("documents of parent folder include sub-folders", all.Rows.Count == 2);
        Check("search filters documents", db.LoadDocuments(root.FolderId, true, "1025").Rows.Count == 1);
        Check("custom attribute mapped by caption (Discipline)", all.Rows.Cast<System.Data.DataRow>().All(r => (string)r["Discipline"] == "CE"));

        var sub = db.LoadRegister(EddmsDatabase.Register.Submission, "BSL-BF3-REV-SMS");
        Check("submission register has every revision (3 rows)", sub.Rows.Count == 3);
        Check("submission register computes days to respond",
            sub.Rows.Cast<System.Data.DataRow>().Any(r => r["Days To Respond"] is int d && d == 17));

        var rev = db.LoadRegister(EddmsDatabase.Register.Revision, "1200");
        Check("revision register keeps superseded revision",
            rev.Rows.Count == 2 && rev.Rows.Cast<System.Data.DataRow>().Count(r => (string)r["State"] == "Superseded") == 1);

        var pub = db.LoadRegister(EddmsDatabase.Register.Published, "BSL-BF3-REV-SMS");
        Check("published register lists released drawing with its local file",
            pub.Rows.Count == 1 && (string)pub.Rows[0]["Local File"] == @"C:\EDMS Drawings\test.pdf");

        var run = db.StartSyncRun("selftest");
        db.FinishSyncRun(run, true, 2, 2, "selftest");
        Check("sync history", db.LoadSyncHistory().Rows.Count > 0);

        Console.WriteLine(failures == 0 ? "All database checks passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}

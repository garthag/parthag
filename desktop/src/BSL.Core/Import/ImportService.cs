using System.Data;
using BSL.Core.Data;
using BSL.Core.Excel;
using Microsoft.Data.SqlClient;

namespace BSL.Core.Import;

public sealed record ImportSummary(string Text, int BatchId);

/// <summary>Writes the standardised workbooks into SQL Server.</summary>
public sealed class ImportService(Database db)
{
    public event Action<string>? Progress;
    void Say(string s) => Progress?.Invoke(s);

    // ------------------------------------------------------------------ BOM workbook
    /// <param name="replace">true: existing BOM lines of each imported package are deleted first.</param>
    public ImportSummary ImportBom(BomWorkbook wb, bool replace = true, IEnumerable<string>? onlySheets = null)
    {
        var sheets = onlySheets?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pkgs = wb.Packages.Where(p => sheets == null || sheets.Contains(p.SheetName)).ToList();
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();
        try
        {
            var batchId = Convert.ToInt32(Database.Scalar(cn, tx,
                "INSERT dbo.ImportBatch(ImportType, FileName, RowsRead, Notes) OUTPUT INSERTED.Id VALUES(N'BOM', @f, @n, @notes)",
                ("@f", wb.FileName), ("@n", pkgs.Sum(p => p.Rows.Count)),
                ("@notes", string.Join(Environment.NewLine, wb.Issues.Where(i => i.Severity == "Warning").Select(i => $"[{i.Sheet} r{i.Row}] {i.Message}").Take(500)))));

            var originators = Database.Query(cn, tx, "SELECT Id, ContractorCode FROM dbo.Contractor").Rows.Cast<DataRow>()
                                      .ToDictionary(r => (string)r[1], r => (int)r[0], StringComparer.OrdinalIgnoreCase);
            var drawingIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int lines = 0, drawingsNew = 0, regNew = 0;

            foreach (var p in pkgs)
            {
                Say($"Package {p.SheetName} …");
                var pkgId = Convert.ToInt32(Database.Scalar(cn, tx, """
                    MERGE dbo.Package t USING (SELECT @c AS PackageCode) s ON t.PackageCode = s.PackageCode
                    WHEN MATCHED THEN UPDATE SET PackageName = ISNULL(t.PackageName, @n), NoOfSets = @sets, SourceSheet = @c
                    WHEN NOT MATCHED THEN INSERT(PackageCode, PackageName, NoOfSets, SourceSheet) VALUES(@c, @n, @sets, @c)
                    OUTPUT INSERTED.Id;
                    """, ("@c", p.SheetName), ("@n", p.Title), ("@sets", p.NoOfSets)));
                if (replace) Database.Execute(cn, tx, "DELETE dbo.BomItem WHERE PackageId = @p", ("@p", pkgId));

                // drawings + register (sheet / revision)
                foreach (var g in p.Rows.Concat(p.DrawingOnlyRows).Where(r => r.DrawingNo != null).GroupBy(r => r.DrawingNo!, StringComparer.OrdinalIgnoreCase))
                {
                    var first = g.First();
                    var orig = Standardizer.Originator(g.Key);
                    var sheetsTotal = g.Select(r => r.SheetNo).Where(s => s != null && s.Contains('/'))
                                       .Select(s => int.TryParse(s!.Split('/')[1], out var n) ? n : (int?)null).Max();
                    var res = Database.Query(cn, tx, """
                        DECLARE @id INT = (SELECT Id FROM dbo.DrawingMaster WHERE DrawingNo = @no), @new BIT = 0;
                        IF @id IS NULL
                        BEGIN
                            INSERT dbo.DrawingMaster(DrawingNo, Title, PackageId, OriginatorId, TotalSheets, CurrentRevision, Status, DrawingType)
                            VALUES(@no, @title, @pkg, @orig, @sheets, @rev, N'IFC', N'Fabrication');
                            SET @id = SCOPE_IDENTITY(); SET @new = 1;
                        END
                        ELSE UPDATE dbo.DrawingMaster SET Title = ISNULL(Title, @title), PackageId = ISNULL(PackageId, @pkg),
                                 OriginatorId = ISNULL(OriginatorId, @orig), TotalSheets = ISNULL(TotalSheets, @sheets),
                                 CurrentRevision = ISNULL(@rev, CurrentRevision) WHERE Id = @id;
                        SELECT @id, @new;
                        """,
                        ("@no", Trunc(g.Key, 100)), ("@title", Trunc(first.DrawingDescription, 300)), ("@pkg", pkgId),
                        ("@orig", orig != null && originators.TryGetValue(orig, out var oid) ? oid : null),
                        ("@sheets", sheetsTotal), ("@rev", MaxRevision(g.Select(r => r.Revision))));
                    var did = (int)res.Rows[0][0];
                    if ((bool)res.Rows[0][1]) drawingsNew++;
                    drawingIds[g.Key] = did;

                    foreach (var sr in g.GroupBy(r => (r.SheetNo, r.Revision)))
                    {
                        var n = Database.Execute(cn, tx, """
                            IF NOT EXISTS (SELECT 1 FROM dbo.DrawingRegister WHERE DrawingId = @d AND ISNULL(SheetNo,N'') = ISNULL(@s,N'') AND ISNULL(Revision,N'') = ISNULL(@r,N''))
                                INSERT dbo.DrawingRegister(DrawingId, SheetNo, Revision, Purpose, Status, SourceRef) VALUES(@d, @s, @r, N'IFC', N'Current', @src);
                            """, ("@d", did), ("@s", Trunc(sr.Key.SheetNo, 20)), ("@r", Trunc(sr.Key.Revision, 10)), ("@src", Trunc(sr.First().DrawingRaw, 150)));
                        if (n > 0) regNew++;
                    }
                }
                // mark older revisions of the same sheet as superseded
                Database.Execute(cn, tx, """
                    UPDATE g SET Status = N'Superseded'
                    FROM dbo.DrawingRegister g JOIN dbo.DrawingMaster d ON d.Id = g.DrawingId
                    WHERE d.PackageId = @p AND g.Status = N'Current' AND EXISTS (
                        SELECT 1 FROM dbo.DrawingRegister x WHERE x.DrawingId = g.DrawingId AND ISNULL(x.SheetNo,N'') = ISNULL(g.SheetNo,N'')
                          AND x.Status <> N'Void'
                          AND (TRY_CAST(REPLACE(x.Revision, N'R', N'') AS INT) > TRY_CAST(REPLACE(g.Revision, N'R', N'') AS INT)
                               OR (TRY_CAST(REPLACE(x.Revision, N'R', N'') AS INT) IS NULL AND x.Revision > g.Revision)));
                    """, ("@p", pkgId));

                lines += BulkInsertBom(cn, tx, p.Rows, pkgId, batchId, drawingIds);
            }
            tx.Commit();
            var text = $"Imported {pkgs.Count} packages, {lines:N0} BOM lines, {drawingsNew} new drawings, {regNew} new register entries.";
            db.Audit("BomItem", null, "IMPORT", text);
            return new(text, batchId);
        }
        catch { tx.Rollback(); throw; }
    }

    static int BulkInsertBom(SqlConnection cn, SqlTransaction tx, List<StdBomRow> rows, int pkgId, int batchId, Dictionary<string, int> drawings)
    {
        var dt = new DataTable();
        (string Name, Type T)[] cols =
        [
            ("PackageId", typeof(int)), ("DrawingId", typeof(int)), ("SheetNo", typeof(string)), ("Revision", typeof(string)),
            ("DrawingDescription", typeof(string)), ("ItemDescription", typeof(string)), ("AssemblyMark", typeof(string)),
            ("PartMark", typeof(string)), ("ItemNo", typeof(string)), ("PartQty", typeof(decimal)), ("SectionDrg", typeof(string)),
            ("SectionStd", typeof(string)), ("LengthMm", typeof(decimal)), ("WidthMm", typeof(decimal)), ("ThicknessMm", typeof(decimal)),
            ("Qty", typeof(decimal)), ("QtyText", typeof(string)), ("SourceUnitWt", typeof(decimal)), ("UnitWeightKg", typeof(decimal)),
            ("TotalWeightKg", typeof(decimal)), ("MaterialGrade", typeof(string)), ("Remarks", typeof(string)), ("SectionChange", typeof(string)),
            ("DimensionStd", typeof(string)), ("RowType", typeof(string)), ("IsExcluded", typeof(bool)), ("QualityFlags", typeof(string)),
            ("SourceSheet", typeof(string)), ("SourceRow", typeof(int)), ("ImportBatchId", typeof(int))
        ];
        foreach (var (n, t) in cols) dt.Columns.Add(n, t);
        foreach (var r in rows)
        {
            dt.Rows.Add(pkgId, r.DrawingNo != null && drawings.TryGetValue(r.DrawingNo, out var d) ? d : DBNull.Value,
                N(Trunc(r.SheetNo, 20)), N(Trunc(r.Revision, 10)), N(Trunc(r.DrawingDescription, 300)), N(Trunc(r.ItemDescription, 300)),
                N(Trunc(r.AssemblyMark, 100)), N(Trunc(r.PartMark, 100)), N(Trunc(r.ItemNo, 50)), Dec(r.PartQty), N(Trunc(r.SectionDrg, 150)),
                N(Trunc(r.SectionStd, 100)), Dec(r.LengthMm), Dec(r.WidthMm), Dec(r.ThicknessMm), Dec(r.Qty), N(Trunc(r.QtyText, 30)),
                Dec(r.SourceUnitWt), Dec(r.UnitWeightKg), Dec(r.TotalWeightKg), N(Trunc(r.MaterialGrade, 100)), N(Trunc(r.Remarks, 400)),
                N(Trunc(r.SectionChange, 100)), N(Trunc(r.DimensionStd, 100)), r.RowType, r.IsExcluded, N(Trunc(r.FlagText, 400)),
                r.SourceSheet, r.SourceRow, batchId);
        }
        using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "dbo.BomItem", BatchSize = 5000, BulkCopyTimeout = 600 };
        foreach (DataColumn c in dt.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        bulk.WriteToServer(dt);
        return dt.Rows.Count;
    }

    // ------------------------------------------------------------------ Contractor workbook
    public sealed record ContractorImportOptions(bool Contractors = true, bool WorkOrders = true, bool Boq = true, bool RaBills = true,
                                                 bool DrawingScope = true, bool ReplaceBoq = true);

    public ImportSummary ImportContractors(ContractorWorkbook wb, ContractorImportOptions? opt = null)
    {
        opt ??= new();
        using var cn = db.Open();
        using var tx = cn.BeginTransaction();
        try
        {
            var batchId = Convert.ToInt32(Database.Scalar(cn, tx,
                "INSERT dbo.ImportBatch(ImportType, FileName, RowsRead, Notes) OUTPUT INSERTED.Id VALUES(N'CONTRACTOR', @f, @n, @notes)",
                ("@f", wb.FileName), ("@n", wb.WorkOrders.Count + wb.Boq.Count + wb.DrawingScope.Count),
                ("@notes", string.Join(Environment.NewLine, wb.Issues.Where(i => i.Severity == "Warning").Select(i => $"[{i.Sheet} r{i.Row}] {i.Message}").Take(500)))));
            var log = new List<string>();

            if (opt.Contractors)
            {
                foreach (var c in wb.Contractors)
                    Database.Execute(cn, tx, """
                        MERGE dbo.Contractor t USING (SELECT @code AS ContractorCode) s ON t.ContractorCode = s.ContractorCode
                        WHEN MATCHED THEN UPDATE SET Name = @name, ContactPerson = ISNULL(@cp, t.ContactPerson), ContactNo = ISNULL(@no, t.ContactNo),
                             Email = ISNULL(@mail, t.Email), PAN = ISNULL(@pan, t.PAN), GSTIN = ISNULL(@gst, t.GSTIN), ModifiedOn = SYSDATETIME()
                        WHEN NOT MATCHED THEN INSERT(ContractorCode, Name, ContractorType, ContactPerson, ContactNo, Email, PAN, GSTIN, State)
                             VALUES(@code, @name, N'Executing Agency', @cp, @no, @mail, @pan, @gst, CASE WHEN LEFT(@gst,2) = N'20' THEN N'Jharkhand' END);
                        """, ("@code", c.Code), ("@name", c.Name.Trim()), ("@cp", c.ContactPerson), ("@no", c.ContactNo), ("@mail", c.Email),
                        ("@pan", c.PAN), ("@gst", c.GSTIN));
                log.Add($"{wb.Contractors.Count} contractors");
            }

            var conIds = Database.Query(cn, tx, "SELECT ContractorCode, Id FROM dbo.Contractor").Rows.Cast<DataRow>()
                                 .ToDictionary(r => (string)r[0], r => (int)r[1], StringComparer.OrdinalIgnoreCase);
            if (opt.WorkOrders)
            {
                foreach (var w in wb.WorkOrders.Where(w => conIds.ContainsKey(w.ContractorCode)))
                    Database.Execute(cn, tx, """
                        MERGE dbo.WorkOrder t USING (SELECT @u AS UniqueNo) s ON t.UniqueNo = s.UniqueNo
                        WHEN MATCHED THEN UPDATE SET ContractorId = @c, ServiceType = @st, WorkCategory = @wc, WorkOrderNo = @wo, WODate = @dt,
                             WOValue = @v, ActualWorkDone = ISNULL(@done, t.ActualWorkDone), ActualPaid = ISNULL(@paid, t.ActualPaid),
                             Remarks = ISNULL(@rem, t.Remarks), ModifiedOn = SYSDATETIME()
                        WHEN NOT MATCHED THEN INSERT(UniqueNo, ContractorId, ServiceType, WorkCategory, WorkOrderNo, WODate, WOValue, ActualWorkDone, ActualPaid, Remarks)
                             VALUES(@u, @c, @st, @wc, @wo, @dt, @v, @done, @paid, @rem);
                        """, ("@u", w.UniqueNo), ("@c", conIds[w.ContractorCode]), ("@st", w.ServiceType), ("@wc", w.WorkCategory),
                        ("@wo", w.WorkOrderNo), ("@dt", w.WODate), ("@v", w.WOValue), ("@done", w.ActualWorkDone), ("@paid", w.ActualPaid), ("@rem", w.Remarks));
                log.Add($"{wb.WorkOrders.Count} work orders");
            }
            var woIds = Database.Query(cn, tx, "SELECT UniqueNo, Id FROM dbo.WorkOrder").Rows.Cast<DataRow>()
                                .ToDictionary(r => (string)r[0], r => (int)r[1], StringComparer.OrdinalIgnoreCase);

            var itemIds = new Dictionary<BoqRec, int>();
            if (opt.Boq && wb.Boq.Count > 0)
            {
                if (opt.ReplaceBoq)
                {
                    Database.Execute(cn, tx, "UPDATE dbo.DrawingMaster SET ContractItemId = NULL WHERE ContractItemId IS NOT NULL");
                    Database.Execute(cn, tx, "DELETE dbo.RaBillLine; DELETE dbo.RaBill; DELETE dbo.ContractItem;");
                }
                foreach (var b in wb.Boq)
                {
                    int? wo = b.WorkOrderUniqueNo != null && woIds.TryGetValue(b.WorkOrderUniqueNo, out var x) ? x : null;
                    itemIds[b] = Convert.ToInt32(Database.Scalar(cn, tx, """
                        INSERT dbo.ContractItem(TechSpecItem, GroupName, IsGroupHeader, Description, Unit, Qty, UnitWeightKg, R1WeightTon,
                               ExternalRate, InternalRate, WorkOrderId, AgencyRef, ActivityType, DrawingQty, Remarks, SortOrder)
                        OUTPUT INSERTED.Id
                        VALUES(@spec, @grp, @hdr, @desc, @unit, @qty, @uw, @r1, @ext, @int, @wo, @ag, @act, @dq, @rem, @sort);
                        """, ("@spec", b.TechSpecItem), ("@grp", Trunc(b.GroupName, 200)), ("@hdr", b.IsGroupHeader), ("@desc", Trunc(b.Description, 400)),
                        ("@unit", b.Unit), ("@qty", b.Qty), ("@uw", b.UnitWeightKg), ("@r1", b.R1WeightTon), ("@ext", b.ExternalRate),
                        ("@int", b.InternalRate), ("@wo", wo), ("@ag", b.AgencyRef), ("@act", b.ActivityType), ("@dq", b.DrawingQty),
                        ("@rem", Trunc(b.Remarks, 400)), ("@sort", b.SortOrder)));
                }
                log.Add($"{wb.Boq.Count} BOQ items");

                if (opt.RaBills)
                {
                    int lines = 0;
                    var bills = new Dictionary<(int, int?), int>();
                    foreach (var b in wb.Boq.Where(b => b.Ra.Count > 0))
                    {
                        int? wo = b.WorkOrderUniqueNo != null && woIds.TryGetValue(b.WorkOrderUniqueNo, out var x) ? x : null;
                        foreach (var (ra, q) in b.Ra)
                        {
                            if (!bills.TryGetValue((ra, wo), out var billId))
                            {
                                billId = Convert.ToInt32(Database.Scalar(cn, tx, """
                                    DECLARE @id INT = (SELECT Id FROM dbo.RaBill WHERE RaNo = @ra AND ((WorkOrderId IS NULL AND @wo IS NULL) OR WorkOrderId = @wo));
                                    IF @id IS NULL BEGIN INSERT dbo.RaBill(RaNo, WorkOrderId, Status) VALUES(@ra, @wo, N'Certified'); SET @id = SCOPE_IDENTITY(); END
                                    SELECT @id;
                                    """, ("@ra", ra), ("@wo", wo)));
                                bills[(ra, wo)] = billId;
                            }
                            Database.Execute(cn, tx, """
                                MERGE dbo.RaBillLine t USING (SELECT @b AS RaBillId, @i AS ContractItemId) s ON t.RaBillId = s.RaBillId AND t.ContractItemId = s.ContractItemId
                                WHEN MATCHED THEN UPDATE SET QtyTon = @q
                                WHEN NOT MATCHED THEN INSERT(RaBillId, ContractItemId, QtyTon) VALUES(@b, @i, @q);
                                """, ("@b", billId), ("@i", itemIds[b]), ("@q", q));
                            lines++;
                        }
                    }
                    log.Add($"{bills.Count} RA bills / {lines} RA lines");
                }
            }

            if (opt.DrawingScope && wb.DrawingScope.Count > 0)
            {
                foreach (var d in wb.DrawingScope)
                {
                    var no = d.DrawingNo ?? $"SCOPE-{d.SourceRow:0000}";
                    Database.Execute(cn, tx, """
                        MERGE dbo.DrawingMaster t USING (SELECT @no AS DrawingNo) s ON t.DrawingNo = s.DrawingNo
                        WHEN MATCHED THEN UPDATE SET Title = ISNULL(t.Title, @title), Area = ISNULL(t.Area, @area), SubArea = ISNULL(t.SubArea, @sub),
                             EstQty = @qty, EstWeightKg = @wt, Remarks = ISNULL(@rem, t.Remarks)
                        WHEN NOT MATCHED THEN INSERT(DrawingNo, Title, Area, SubArea, EstQty, EstWeightKg, Status, DrawingType, Remarks)
                             VALUES(@no, @title, @area, @sub, @qty, @wt, CASE WHEN @hasno = 1 THEN N'IFC' ELSE N'Scope (no drg no.)' END, N'Scope item', @rem);
                        """, ("@no", no), ("@title", Trunc(d.Description, 300)), ("@area", d.Area), ("@sub", Trunc(d.SubArea, 150)),
                        ("@qty", Trunc(d.Qty, 30)), ("@wt", d.WeightKg), ("@hasno", d.DrawingNo != null),
                        ("@rem", Trunc(string.Join("; ", new[] { d.Agency != null ? "Agency: " + d.Agency : null, d.RefNo != null ? "Ref: " + d.RefNo : null, d.Remarks }
                                                           .Where(s => s != null)), 400)));
                }
                log.Add($"{wb.DrawingScope.Count} drawing scope rows");
            }
            tx.Commit();
            var text = "Imported " + string.Join(", ", log) + ".";
            db.Audit("Contractor", null, "IMPORT", text);
            return new(text, batchId);
        }
        catch { tx.Rollback(); throw; }
    }

    /// <summary>Highest revision: numeric R-revisions compared as numbers (R10 &gt; R2), letters after.</summary>
    static string? MaxRevision(IEnumerable<string?> revs) =>
        revs.Where(r => r != null)
            .OrderBy(r => r!.Length > 1 && r[0] == 'R' && int.TryParse(r[1..], out var n) ? n : -1)
            .ThenBy(r => r, StringComparer.Ordinal).LastOrDefault();

    static object N(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : s;
    static object Dec(double? d) => d is { } v && !double.IsNaN(v) && Math.Abs(v) < 1e12 ? Math.Round((decimal)v, 4) : DBNull.Value;
    static string? Trunc(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..n];
}

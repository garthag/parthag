using ClosedXML.Excel;

namespace Bf3Billing.Core.Bsl;

/// <summary>
/// Small workbooks in the same layout as the BSL master BOM and contractor master (made-up rows),
/// used by the tests and the self test. Never shipped as data.
/// </summary>
public static class BslSample
{
    public static (string Bom, string Contractor) Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var bom = Path.Combine(dir, "sample-bom.xlsx");
        using (var wb = new XLWorkbook())
        {
            // Layout 1: title, blank, header on row 3, UNIT/TOTAL sub-header, total ×3 sets.
            var s = wb.Worksheets.Add("STOVE");
            s.Cell(1, 1).Value = "BSL_BF#3_STOVE_SHELL_BOM";
            string[] h = { "S.no", "DRG NUMBER", "Sheet", "Revision", "DRG Description", "Item Description", "MARK NO", "ITEM NO", "Section", "LENGTH (MM)", "WIDTH (MM)", "Thickness(MM)", "NO OF PCS.", "WEIGHT (Kg )", "", "MATERIAL GRADE" };
            for (var i = 0; i < h.Length; i++) s.Cell(3, i + 1).Value = h[i];
            s.Cell(4, 14).Value = "UNIT"; s.Cell(4, 15).Value = "TOTAL";
            void Row(IXLWorksheet w, int r, string drg, object sheet, string rev, string item, string sec, double len, double wid, double thk, double pcs, double unit, double total, string grade)
            {
                w.Cell(r, 1).Value = r; w.Cell(r, 2).Value = drg;
                if (sheet is DateTime d) w.Cell(r, 3).Value = d; else w.Cell(r, 3).Value = sheet.ToString();
                w.Cell(r, 4).Value = rev; w.Cell(r, 5).Value = "GA OF STOVE SHELL"; w.Cell(r, 6).Value = item; w.Cell(r, 9).Value = sec;
                w.Cell(r, 10).Value = len; w.Cell(r, 11).Value = wid; w.Cell(r, 12).Value = thk; w.Cell(r, 13).Value = pcs;
                w.Cell(r, 14).Value = unit; w.Cell(r, 15).Value = total; w.Cell(r, 16).Value = grade;
            }
            Row(s, 5, "16193442", "2/6", "A", "Dome", "PL THK 25", 2527, 403, 25, 10, 165.5, 1655, "15:2002 Gr.2");
            Row(s, 6, "16193442", "2/6", "A", "Shell", "PL THK 25", 3000, 500, 25, 4, 294.4, 1177.6, "IS 2002 Gr.2");
            Row(s, 7, "16193442", new DateTime(2026, 6, 3), "A", "Ring", "PL THK 20", 2000, 300, 20, 2, 94.2, 188.4, "IS2062");
            s.Cell(8, 13).Value = "TOTAL FOR 3 STOVES (MT)";
            s.Cell(8, 15).Value = 9.063; // as Excel saves it: the value of SUBTOTAL(9,O5:O7)/1000*3

            // Layout 2: header on row 2, drawing number with sheet and revision written in it.
            var t = wb.Worksheets.Add("SDD of Duct");
            t.Cell(1, 1).Value = "BSL_BF#3_SDD of Duct";
            for (var i = 0; i < h.Length; i++) t.Cell(2, i + 1).Value = h[i];
            t.Cell(3, 14).Value = "UNIT"; t.Cell(3, 15).Value = "TOTAL";
            Row(t, 4, "ERC_SMS_DSWG_F-0016_1 OF 1 - Rev 0", "", "", "BEAM", "NPB500X200X79.36", 6472, 0, 0, 1, 513.64, 513.64, "IS2062 E250 A");
            Row(t, 5, "ERC_SMS_DSWG_F-0017_1 OF 1 - Rev 0", "", "", "BEAM", "ISA100X75X10", 140, 0, 0, 4, 1.81, 7.25, "IS2062 E250 A");
            t.Cell(6, 13).Value = "TOTAL (MT)";
            t.Cell(6, 15).Value = 0.521;
            wb.SaveAs(bom);
        }

        var con = Path.Combine(dir, "sample-contractor.xlsx");
        using (var wb = new XLWorkbook())
        {
            var m = wb.Worksheets.Add("Contractor_Master");
            string[] mh = { "Unique_No ", "Contractor", "", "Code ", "Type of Service ", "Type", "Contact_Person", "Contact_No", "Mail_Id", "PAN", "GST", "Work_Order_No", "WO_Dated ", "Work_Order_Value", "Actual_Work_Done", "Actual_Paid", "Difference ", "Remarks " };
            for (var i = 0; i < mh.Length; i++) m.Cell(1, i + 1).Value = mh[i];
            object[][] rows =
            {
                new object[] { "BM05", "G.R ENTERPRISES", "", "GRE", "ERECTION ", "BSL_MECH_ERE_EQUIPMENT", "R.K. Singh", 9334240112d, "gre@example.com", "AABFG8971H", "20AABFG8971H1ZH", "MCK/WO-MECH/BSL/2024-25/006", "17.04.2024", 19350070d },
                new object[] { "BM07", "M.D. ESTHAPAN", "", "MDE", "ERECTION ", "BSL_MECH_ERE_EQUIPMENT", "A. S. Jose", 8606381887d, "mde@example.com", "ACAFM8878N", "20ACAFM8878N1ZP", "MCK/WO-MECH/BSL/2024-25/008", "17.04.2024", 19769050d },
                new object[] { "BM15", "NEW AGENCY", "", "NEW", "ERECTION ", "BSL_MECH_ERE_UTILITY", "", "", "", "", "", "MCK/WO-MECH/BSL/2025-26/030", "01.07.2025", 500000d },
            };
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    m.Cell(r + 2, c + 1).Value = rows[r][c] switch { double dv => dv, string sv => sv, _ => "" };

            var b = wb.Worksheets.Add("Sheet2");
            string[] bh = { "TECH. SPEC. ITEM", " DESCRIPTION", "M.U.", "Q.TY", "UNIT\nWEIGHT\n( Kg )", "TOTAL\nWEIGHT\n( Ton )", "R1 WEIGHT (Ton)", "Supply Unit Rate", "R1 Total Amount in Rs.", "Remarks", "Drawing_Qty ", "Unique_No ", "Activity_Type", "Work_Order_No", "Agency", "Agency Rate", "BALANCE" };
            for (var i = 0; i < bh.Length; i++) b.Cell(2, i + 1).Value = bh[i];
            for (var n = 1; n <= 5; n++) { b.Cell(2, 17 + n).Value = "RA-" + n; b.Cell(1, 22 + n).Value = "RA-0" + n; }
            b.Cell(3, 1).Value = 1200; b.Cell(3, 2).Value = "HOT STOVES"; b.Cell(3, 6).Value = 115;
            void Item(int r, string desc, string unit, double r1, double ext, object intRate, string uno, string act, params (int Ra, double Qty)[] ra)
            {
                b.Cell(r, 2).Value = desc; b.Cell(r, 3).Value = unit; b.Cell(r, 4).Value = 1; b.Cell(r, 7).Value = r1; b.Cell(r, 8).Value = ext;
                if (intRate is double ir) b.Cell(r, 16).Value = ir;
                b.Cell(r, 12).Value = uno; b.Cell(r, 13).Value = act;
                foreach (var (n, q) in ra) b.Cell(r, 17 + n).Value = q;
            }
            Item(4, "Dome shell with cylindrical connection", "set", 100, 17500, 8000d, "BM05", "ERECTION ", (1, 40), (2, 35));
            Item(5, "Platform hot stoves top", "nr", 60, 17500, 9000d, "BM05", "ERECTION ", (2, 81.98));       // above R1 scope
            Item(6, "Hot blast valves", "set", 30, 20000, null!, "BM07", "ERECTION ", (3, 12.5));                // no agency rate
            Item(7, "Hot blast valves", "set", 30, 20000, 7000d, "BM07", "ERECTION ", (4, 5));                   // same description, second row
            Item(8, "Anchor bolts", "nr", 2, 15000, 6000d, "BSL", "ERECTION ");

            var d = wb.Worksheets.Add("DRAWING ");
            string[] dh = { "Si.No", "SI.No", "Contract item No", "Area", "Drawing_No", "", "Sub Area", "Description", "Qty", "", "", "Weight in Kg ", "Remarks", "", "", "Agency" };
            for (var i = 0; i < dh.Length; i++) d.Cell(1, i + 1).Value = dh[i];
            d.Cell(2, 4).Value = "BF-BFP"; d.Cell(2, 7).Value = "FURNACE PROPER"; d.Cell(2, 8).Value = "Above Burden Probes"; d.Cell(2, 9).Value = "1+1"; d.Cell(2, 12).Value = 2488; d.Cell(2, 16).Value = "GRE";

            var x = wb.Worksheets.Add("REMARKS ");
            x.Cell(1, 1).Value = "ADDED_01"; x.Cell(1, 2).Value = "MDE"; x.Cell(1, 3).Value = "LOUVER VALVE AT WASTE GAS"; x.Cell(1, 4).Value = 6.3; x.Cell(1, 5).Value = "EXTRA NOT IN DOW";
            wb.SaveAs(con);
        }
        return (bom, con);
    }
}

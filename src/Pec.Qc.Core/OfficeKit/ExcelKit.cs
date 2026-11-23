using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace Pec.Qc.Core.OfficeKit;

/// <summary>Read, create and modify Excel workbooks (.xlsx/.xlsm) without Excel installed (ClosedXML, MIT). Formulas, styles, widths, merges, filters, freeze panes, sheets, rows and columns.</summary>
public static class ExcelKit
{
    public sealed record SheetInfo(string Name, int Rows, int Columns, string UsedRange, int Formulas, IReadOnlyList<string> FirstRow);

    public static IReadOnlyList<SheetInfo> Inspect(string path)
    {
        using var wb = Open(path);
        var list = new List<SheetInfo>();
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            if (used is null) { list.Add(new(ws.Name, 0, 0, "", 0, [])); continue; }
            var first = used.FirstRow().Cells().Select(c => c.GetFormattedString()).ToList();
            var formulas = used.CellsUsed().Count(c => c.HasFormula);
            list.Add(new(ws.Name, used.RowCount(), used.ColumnCount(), used.RangeAddress.ToStringRelative(), formulas, first));
        }
        return list;
    }

    /// <summary>Rows of one sheet as text (calculated values), optionally with the formulas, for a range such as A1:F40 or the whole used range.</summary>
    public static object Read(string path, string? sheet, string? range, int maxRows, bool formulas)
    {
        maxRows = Math.Clamp(maxRows, 1, 5000);
        using var wb = Open(path);
        var ws = Sheet(wb, sheet);
        var r = string.IsNullOrWhiteSpace(range) ? ws.RangeUsed() : ws.Range(range);
        if (r is null) return new { sheet = ws.Name, rows = Array.Empty<object>(), note = "The sheet is empty." };
        var rows = new List<object>();
        var truncated = false;
        foreach (var row in r.Rows())
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            var cells = row.Cells().Select(c => formulas && c.HasFormula ? "=" + c.FormulaA1 + "  →  " + Safe(c) : Safe(c)).ToList();
            rows.Add(new { row = row.RowNumber(), cells });
        }
        return new { sheet = ws.Name, range = r.RangeAddress.ToStringRelative(), truncated, rows };
    }

    private static string Safe(IXLCell c)
    {
        try { return c.GetFormattedString(); } catch (Exception) { return "#ERROR"; }
    }

    /// <summary>Creates a workbook from JSON: [{"name":"Data","rows":[["A","B"],[1,2]],"header":true,"widths":[20,10]}]. A text starting with = is a formula.</summary>
    public static string Create(string path, string sheetsJson, bool overwrite)
    {
        path = NormalizePath(path);
        if (File.Exists(path) && !overwrite) throw new IOException($"{path} already exists: pass overwrite=true to replace it, or use excel_edit to change it.");
        using var doc = JsonDocument.Parse(sheetsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'sheets' must be a JSON array of {name, rows}.");
        using var wb = new XLWorkbook();
        var n = 0;
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var name = Str(s, "name") ?? "Sheet" + (++n);
            var ws = wb.Worksheets.Add(SafeSheetName(name));
            if (s.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                var r = 1;
                foreach (var row in rows.EnumerateArray())
                {
                    var c = 1;
                    if (row.ValueKind == JsonValueKind.Array) foreach (var v in row.EnumerateArray()) SetValue(ws.Cell(r, c++), v);
                    else SetValue(ws.Cell(r, 1), row);
                    r++;
                }
                if (r > 1 && (!s.TryGetProperty("header", out var h) || h.ValueKind != JsonValueKind.False))
                {
                    var hr = ws.Row(1); hr.Style.Font.Bold = true; hr.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
                    ws.SheetView.FreezeRows(1);
                }
            }
            if (s.TryGetProperty("widths", out var w) && w.ValueKind == JsonValueKind.Array)
            {
                var i = 1; foreach (var x in w.EnumerateArray()) { if (x.ValueKind == JsonValueKind.Number) ws.Column(i).Width = x.GetDouble(); i++; }
            }
            else ws.Columns().AdjustToContents(1, 60);
        }
        if (wb.Worksheets.Count == 0) wb.Worksheets.Add("Sheet1");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
        return path;
    }

    /// <summary>Applies the operations in order and saves (a backup copy of the original is kept next to it). Returns what was done, one line per operation.</summary>
    public static (string Saved, IReadOnlyList<string> Done, string? Backup) Edit(string path, string opsJson, string? saveAs, bool backup = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        using var doc = JsonDocument.Parse(opsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'operations' must be a JSON array of {op, ...}.");
        using var wb = Open(path);
        var done = new List<string>();
        var i = 0;
        foreach (var op in doc.RootElement.EnumerateArray())
        {
            i++;
            try { done.Add(Apply(wb, op)); }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new ArgumentException($"Operation {i} ({Str(op, "op")}) failed: {e.Message}. Nothing was saved."); }
        }
        var target = string.IsNullOrWhiteSpace(saveAs) ? path : NormalizePath(saveAs!);
        string? bak = null;
        if (backup && File.Exists(target)) { bak = target + ".bak_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Copy(target, bak, true); }
        wb.SaveAs(target);
        return (target, done, bak);
    }

    private static string Apply(XLWorkbook wb, JsonElement op)
    {
        var kind = (Str(op, "op") ?? "").ToLowerInvariant();
        IXLWorksheet Ws() => Sheet(wb, Str(op, "sheet"));
        switch (kind)
        {
            case "set":
            {
                var ws = Ws(); var cell = Req(op, "cell");
                op.TryGetProperty("value", out var v); SetValue(ws.Cell(cell), v);
                return $"set {ws.Name}!{cell}";
            }
            case "formula":
            {
                var ws = Ws(); var cell = Req(op, "cell"); var f = Req(op, "formula");
                ws.Cell(cell).FormulaA1 = f.TrimStart('=');
                return $"formula {ws.Name}!{cell}";
            }
            case "setrange":
            {
                var ws = Ws(); var start = ws.Cell(Req(op, "start"));
                var r = start.Address.RowNumber; var rows = 0;
                foreach (var row in op.GetProperty("rows").EnumerateArray())
                {
                    var c = start.Address.ColumnNumber;
                    foreach (var v in row.EnumerateArray()) SetValue(ws.Cell(r, c++), v);
                    r++; rows++;
                }
                return $"setRange {ws.Name} from {start.Address} ({rows} rows)";
            }
            case "append":
            {
                var ws = Ws(); var r = (ws.LastRowUsed()?.RowNumber() ?? 0) + 1; var n = 0;
                foreach (var row in op.GetProperty("rows").EnumerateArray())
                {
                    var c = 1; foreach (var v in row.EnumerateArray()) SetValue(ws.Cell(r, c++), v);
                    r++; n++;
                }
                return $"append {n} rows to {ws.Name}";
            }
            case "clear": { var ws = Ws(); ws.Range(Req(op, "range")).Clear(); return "clear " + Req(op, "range"); }
            case "style":
            {
                var ws = Ws(); var rg = ws.Range(Req(op, "range"));
                if (Bool(op, "bold") is { } b) rg.Style.Font.Bold = b;
                if (Bool(op, "italic") is { } it) rg.Style.Font.Italic = it;
                if (Str(op, "fontColor") is { } fc) rg.Style.Font.FontColor = XLColor.FromHtml(fc);
                if (Str(op, "fill") is { } fl) rg.Style.Fill.BackgroundColor = XLColor.FromHtml(fl);
                if (op.TryGetProperty("fontSize", out var fs) && fs.ValueKind == JsonValueKind.Number) rg.Style.Font.FontSize = fs.GetDouble();
                if (Str(op, "numberFormat") is { } nf) rg.Style.NumberFormat.Format = nf;
                if (Str(op, "align") is { } al) rg.Style.Alignment.Horizontal = al.ToLowerInvariant() switch { "center" => XLAlignmentHorizontalValues.Center, "right" => XLAlignmentHorizontalValues.Right, _ => XLAlignmentHorizontalValues.Left };
                if (Bool(op, "wrap") is { } wr) rg.Style.Alignment.WrapText = wr;
                if (Bool(op, "border") == true) { rg.Style.Border.OutsideBorder = XLBorderStyleValues.Thin; rg.Style.Border.InsideBorder = XLBorderStyleValues.Thin; }
                return "style " + Req(op, "range");
            }
            case "width":
            {
                var ws = Ws(); var col = Req(op, "column");
                if (op.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) ws.Column(col).Width = w.GetDouble(); else ws.Column(col).AdjustToContents();
                return $"width column {col}";
            }
            case "merge": { var ws = Ws(); ws.Range(Req(op, "range")).Merge(); return "merge " + Req(op, "range"); }
            case "freeze":
            {
                var ws = Ws();
                var rows = op.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                var cols = op.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                ws.SheetView.FreezeRows(rows); ws.SheetView.FreezeColumns(cols);
                return $"freeze {rows} rows, {cols} columns";
            }
            case "autofilter": { var ws = Ws(); ws.Range(Req(op, "range")).SetAutoFilter(); return "autofilter " + Req(op, "range"); }
            case "insertrows": { var ws = Ws(); ws.Row(Int(op, "at")).InsertRowsAbove(Math.Max(1, Int(op, "count", 1))); return "insert rows"; }
            case "deleterows": { var ws = Ws(); ws.Rows(Int(op, "at"), Int(op, "at") + Math.Max(1, Int(op, "count", 1)) - 1).Delete(); return "delete rows"; }
            case "insertcolumns": { var ws = Ws(); ws.Column(Req(op, "at")).InsertColumnsBefore(Math.Max(1, Int(op, "count", 1))); return "insert columns"; }
            case "deletecolumns":
            {
                var ws = Ws(); var at = ws.Column(Req(op, "at")).ColumnNumber();
                ws.Columns(at, at + Math.Max(1, Int(op, "count", 1)) - 1).Delete(); return "delete columns";
            }
            case "addsheet": { var n = SafeSheetName(Req(op, "name")); wb.Worksheets.Add(n); return "add sheet " + n; }
            case "deletesheet": { var ws = Ws(); var n = ws.Name; ws.Delete(); return "delete sheet " + n; }
            case "renamesheet": { var ws = Ws(); var old = ws.Name; ws.Name = SafeSheetName(Req(op, "to")); return $"rename {old} → {ws.Name}"; }
            case "copysheet": { var ws = Ws(); ws.CopyTo(SafeSheetName(Req(op, "to"))); return "copy sheet " + ws.Name; }
            case "hyperlink": { var ws = Ws(); ws.Cell(Req(op, "cell")).SetHyperlink(new XLHyperlink(Req(op, "url"))); return "hyperlink " + Req(op, "cell"); }
            case "sort":
            {
                var ws = Ws(); var rg = ws.Range(Req(op, "range"));
                var col = Int(op, "column", 1); var desc = Bool(op, "descending") == true;
                rg.Sort(col, desc ? XLSortOrder.Descending : XLSortOrder.Ascending);
                return "sort " + Req(op, "range");
            }
            default:
                throw new ArgumentException("unknown op '" + kind + "'. Known: set, formula, setRange, append, clear, style, width, merge, freeze, autofilter, insertRows, deleteRows, insertColumns, deleteColumns, addSheet, deleteSheet, renameSheet, copySheet, hyperlink, sort.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static XLWorkbook Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".csv" or ".tsv") throw new NotSupportedException("That is a text file: read it with file_read, and write a real workbook with excel_write.");
        if (ext is ".xls") throw new NotSupportedException("The old .xls format cannot be edited here: convert it to .xlsx first (for example with LibreOffice or a Python script), or read it and write a new .xlsx with excel_write.");
        // open from a copy of the bytes so a workbook that Excel has open (locked) can still be read
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ms = new MemoryStream(); fs.CopyTo(ms); ms.Position = 0;
        return new XLWorkbook(ms);
    }

    private static string NormalizePath(string p)
    {
        var ext = Path.GetExtension(p).ToLowerInvariant();
        return ext is ".xlsx" or ".xlsm" ? p : p + ".xlsx";
    }

    private static IXLWorksheet Sheet(XLWorkbook wb, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return wb.Worksheets.First();
        if (wb.TryGetWorksheet(name, out var ws)) return ws;
        if (int.TryParse(name, out var n) && n >= 1 && n <= wb.Worksheets.Count) return wb.Worksheet(n);
        throw new ArgumentException($"There is no sheet '{name}'. Sheets: {string.Join(", ", wb.Worksheets.Select(s => s.Name))}.");
    }

    private static string SafeSheetName(string n)
    {
        var s = new string(n.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
        if (s.Length == 0) s = "Sheet";
        return s.Length > 31 ? s[..31] : s;
    }

    private static void SetValue(IXLCell cell, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: cell.Value = v.GetDouble(); break;
            case JsonValueKind.True: cell.Value = true; break;
            case JsonValueKind.False: cell.Value = false; break;
            case JsonValueKind.Null: case JsonValueKind.Undefined: cell.Clear(XLClearOptions.Contents); break;
            case JsonValueKind.String:
            {
                var t = v.GetString() ?? "";
                if (t.StartsWith('=') && t.Length > 1) cell.FormulaA1 = t[1..];
                else cell.Value = t;
                break;
            }
            default: cell.Value = v.GetRawText(); break;
        }
    }

    internal static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static bool? Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static string Req(JsonElement o, string name) => Str(o, name) is { Length: > 0 } s ? s : throw new ArgumentException("missing '" + name + "'");
    private static int Int(JsonElement o, string name, int def = -1)
    {
        if (o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (def >= 0) return def;
        throw new ArgumentException("missing '" + name + "'");
    }
}

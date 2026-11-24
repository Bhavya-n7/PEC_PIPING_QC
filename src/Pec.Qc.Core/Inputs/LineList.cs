using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Pec.Qc.Core.Inputs;

/// <summary>A value of the line list: its text as written, the number in it (when there is one) and the unit from the column header, for example psig.</summary>
public sealed record LlValue(string Text, double? Number, string? Unit);

public sealed record LineListEntry(string File, string Sheet, int Row, string LineNo, string Key, IReadOnlyDictionary<string, LlValue> Fields, IReadOnlyDictionary<string, string> Extra)
{
    public string? Text(string field) => Fields.TryGetValue(field, out var v) ? v.Text : null;
}

public sealed record LineListSheet(string File, string Sheet, int HeaderRow, IReadOnlyDictionary<string, string> Mapped, IReadOnlyList<string> Unmapped, int Rows);

/// <summary>
/// The project's line list(s) as data. Clients name their columns differently, so the header row is found by looking for known names (Line No, Size, Fluid, Class,
/// Design Pressure ...), every column that is not recognised is kept as an extra with its own header, and the units written in the header (psig, degF) travel with
/// the value. What could not be understood is reported, never guessed.
/// </summary>
public sealed class LineListSet
{
    public static readonly string[] Fields =
        ["LineNo", "Size", "Fluid", "Class", "DesignPressure", "DesignTemp", "OperPressure", "OperTemp", "TestPressure", "TestMedium", "Insulation", "InsulationThk",
         "Paint", "Pwht", "PidNo", "From", "To", "Phase", "Stress", "Remarks"];

    // normalised header (letters and digits only, units in brackets removed) -> field; first match wins
    private static readonly (string Field, Regex Rx)[] Rules =
    [
        ("LineNo", R("line(no|number|num|tag|id)?|pipelineno|pipelinenumber|pipeno|tagno|tagnumber|linenr")),
        ("Size", R("size|nps|nominalsize|linesize|dia|diameter|nominaldiameter|sizein|sizeinch|sizemm")),
        ("Fluid", R("fluid|fluidcode|service|fluidservice|servicecode|fluidname")),
        ("Class", R("pipingclass|pipeclass|class|spec|linespec|materialclass|pipingspec|pipingmaterialclass|specification|pms")),
        ("DesignPressure", R("designpressure|designpress|designpr|dp")),
        ("DesignTemp", R("designtemp|designtemperature|dt")),
        ("OperPressure", R("operatingpressure|operpressure|operatingpress|operpress|op")),
        ("OperTemp", R("operatingtemperature|operatingtemp|opertemp|ot")),
        ("TestPressure", R("testpressure|testpress|hydrotestpressure|hydrotest|tp")),
        ("TestMedium", R("testmedium|testfluid|testingmedium|testmedia|testmed")),
        ("InsulationThk", R("insulationthickness|insulationthk|insuthk|insulthk|thickness")),
        ("Insulation", R("insulation|insulationtype|insutype|insul|insulationcode")),
        ("Paint", R("paint|paintsystem|painting")),
        ("Pwht", R("pwht|postweldheattreatment")),
        ("PidNo", R("pid|pidno|pidnumber|pandid|pandidno|pandidnumber|pidref|pidnodrawing|drawingno|pidrefno")),
        ("From", R("from|fromequipment|start|origin")),
        ("To", R("to|toequipment|end|destination")),
        ("Phase", R("phase|fluidphase|state")),
        ("Stress", R("stresscriticality|stresscritical|stress|criticality")),
        ("Remarks", R("remarks|notes|comment|comments")),
    ];

    private static Regex R(string alternatives) => new($"^({alternatives})$", RegexOptions.Compiled);

    public IReadOnlyList<LineListEntry> Entries { get; }
    public IReadOnlyList<LineListSheet> Sheets { get; }
    public IReadOnlyList<string> Warnings { get; }

    private LineListSet(List<LineListEntry> entries, List<LineListSheet> sheets, List<string> warnings) { Entries = entries; Sheets = sheets; Warnings = warnings; }

    /// <summary>The key two spellings of one line number share: upper case, no leading slash, smart quotes made plain, no spaces round hyphens.</summary>
    public static string Normalize(string? line)
    {
        var s = (line ?? "").Trim().Replace('”', '"').Replace('“', '"').Replace('″', '"').Replace('’', '\'').TrimStart('/').ToUpperInvariant();
        s = Regex.Replace(s, @"\s*-\s*", "-");
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    /// <summary>Every line list file in the project's line-list kind (xlsx, xlsm, csv, tsv).</summary>
    public static LineListSet Load(InputsLibrary lib)
    {
        var files = lib.Inventory().Where(d => d.Kind == InputKind.LineList && d.Ext is ".xlsx" or ".xlsm" or ".csv" or ".tsv").Select(d => (Path.Combine(lib.Folder, d.RelPath), d.RelPath)).ToList();
        return LoadFiles(files);
    }

    public static LineListSet LoadFiles(IEnumerable<(string Full, string Rel)> files)
    {
        var entries = new List<LineListEntry>(); var sheets = new List<LineListSheet>(); var warnings = new List<string>();
        foreach (var (full, rel) in files)
        {
            try { foreach (var (name, rows) in ReadSheets(full)) ReadSheet(rel, name, rows, entries, sheets, warnings); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add($"{rel}: could not be read ({e.Message})"); }
        }
        return new LineListSet(entries, sheets, warnings);
    }

    private static string? FieldOf(string header, out string? unit)
    {
        unit = null;
        var m = Regex.Match(header, @"\(([^)]*)\)");
        if (m.Success) unit = m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : null;
        var key = Regex.Replace(Regex.Replace(header, @"\([^)]*\)", ""), @"[^A-Za-z0-9]", "").ToLowerInvariant();
        foreach (var (field, rx) in Rules) if (rx.IsMatch(key)) return field;
        return null;
    }

    private static void ReadSheet(string rel, string sheet, List<(int Row, string[] Cells)> rows, List<LineListEntry> entries, List<LineListSheet> sheets, List<string> warnings)
    {
        if (rows.Count == 0) return;
        // header = the row (among the first 30) that names the most known columns, and one of them is the line number
        var best = -1; var bestScore = 0;
        for (var i = 0; i < Math.Min(rows.Count, 30); i++)
        {
            var found = rows[i].Cells.Select(c => FieldOf(c, out _)).Where(f => f is not null).Distinct().ToList();
            if (found.Contains("LineNo") && found.Count > bestScore && found.Count >= 3) { best = i; bestScore = found.Count; }
        }
        if (best < 0)
        {
            if (rows.Count > 3) warnings.Add($"{rel} / {sheet}: no header row with a line number column and at least two other known columns was found; the sheet was not read as a line list.");
            return;
        }
        var header = rows[best].Cells;
        var map = new Dictionary<int, (string Field, string? Unit)>(); var extra = new Dictionary<int, string>(); var mapped = new Dictionary<string, string>();
        for (var c = 0; c < header.Length; c++)
        {
            var h = header[c].Trim();
            if (h.Length == 0) continue;
            var f = FieldOf(h, out var unit);
            if (f is not null && !mapped.ContainsKey(f)) { map[c] = (f, unit); mapped[f] = h; }
            else extra[c] = h;
        }
        var count = 0; var noLine = 0;
        for (var i = best + 1; i < rows.Count; i++)
        {
            var (rowNo, cells) = rows[i];
            if (cells.All(c => c.Trim().Length == 0)) continue;
            var lineCol = map.First(kv => kv.Value.Field == "LineNo").Key;
            var raw = lineCol < cells.Length ? cells[lineCol].Trim() : "";
            if (raw.Length == 0) { noLine++; continue; }
            var fields = new Dictionary<string, LlValue>();
            foreach (var (col, (field, unit)) in map)
            {
                var text = col < cells.Length ? cells[col].Trim() : "";
                if (text.Length == 0) continue;
                var num = Regex.Match(text, @"-?\d+(?:\.\d+)?");
                fields[field] = new LlValue(text, num.Success && double.TryParse(num.Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null, unit);
            }
            var ex = new Dictionary<string, string>();
            foreach (var (col, name) in extra) if (col < cells.Length && cells[col].Trim().Length > 0) ex[name] = cells[col].Trim();
            entries.Add(new LineListEntry(rel, sheet, rowNo, raw, Normalize(raw), fields, ex));
            count++;
        }
        if (noLine > 0) warnings.Add($"{rel} / {sheet}: {noLine} filled row(s) without a line number were skipped.");
        sheets.Add(new LineListSheet(rel, sheet, rows[best].Row, mapped, extra.Values.ToList(), count));
    }

    // ------------------------------------------------------------------ reading cells

    private static IEnumerable<(string Name, List<(int Row, string[] Cells)> Rows)> ReadSheets(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".csv" or ".tsv")
        {
            var delim = ext == ".tsv" ? '\t' : DetectDelimiter(path);
            var rows = new List<(int, string[])>(); var n = 0;
            foreach (var line in File.ReadLines(path)) { n++; rows.Add((n, SplitCsv(line, delim))); }
            yield return (Path.GetFileNameWithoutExtension(path), rows);
            yield break;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        foreach (var ws in wb.Worksheets)
        {
            var range = ws.RangeUsed();
            if (range is null) continue;
            var c0 = range.FirstColumn().ColumnNumber(); var cols = range.ColumnCount();
            var rows = new List<(int, string[])>();
            for (var r = range.FirstRow().RowNumber(); r <= range.LastRow().RowNumber(); r++)
                rows.Add((r, Enumerable.Range(c0, cols).Select(c => ws.Cell(r, c).GetFormattedString()).ToArray()));
            yield return (ws.Name, rows);
        }
    }

    private static char DetectDelimiter(string path)
    {
        var first = File.ReadLines(path).Take(5).ToList();
        var counts = new[] { ',', ';', '\t' }.Select(d => (d, first.Sum(l => l.Count(ch => ch == d)))).OrderByDescending(x => x.Item2).First();
        return counts.Item2 > 0 ? counts.d : ',';
    }

    private static string[] SplitCsv(string line, char delim)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQ) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') inQ = false; else sb.Append(ch); }
            else if (ch == '"') inQ = true;
            else if (ch == delim) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells.ToArray();
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Exact match on the normalised line number first; otherwise the lines that contain the text (for example only the area and class).</summary>
    public IReadOnlyList<LineListEntry> Find(string lineNo, int max = 10)
    {
        var key = Normalize(lineNo);
        if (key.Length == 0) return [];
        var exact = Entries.Where(e => e.Key == key).ToList();
        if (exact.Count > 0) return exact.Take(max).ToList();
        return Entries.Where(e => e.Key.Contains(key, StringComparison.Ordinal)).Take(max).ToList();
    }

    /// <summary>Line numbers that appear more than once in the line list (a data error worth reporting).</summary>
    public IReadOnlyList<(string Key, int Count)> Duplicates() =>
        Entries.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => (g.Key, g.Count())).OrderBy(x => x.Key).ToList();
}

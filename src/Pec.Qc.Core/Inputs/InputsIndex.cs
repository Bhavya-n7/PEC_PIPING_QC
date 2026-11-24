using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Sources;
using UglyToad.PdfPig;

namespace Pec.Qc.Core.Inputs;

public sealed record InputChunk(string File, string Kind, string Loc, int Page, string Text);
public sealed record InputHit(string File, string Kind, string Loc, int Page, double Score, string Text);
public sealed record InputFileState(string RelPath, long Size, long Ticks, int Chunks, string? Note);
public sealed record RefreshReport(int Files, int Added, int Updated, int Removed, int Chunks, IReadOnlyList<string> Unreadable, IReadOnlyList<string> NotIndexable);

/// <summary>
/// Search over one project's inputs folder (PMS, line list, P&amp;IDs, vendor data). Every hit says which file and where in it: the page of a PDF, or the sheet and rows of an
/// Excel file, so an answer can quote its source. The index is kept in this PC's data folder, is refreshed only for files that are new or changed, and a drawing with no
/// text layer (a scan) is reported as unreadable instead of being skipped silently.
/// </summary>
public sealed class InputsIndex
{
    private readonly InputsLibrary _lib;
    private readonly string _dir;
    private Bm25<InputChunk>? _bm25;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public InputsIndex(InputsLibrary lib, string? indexRoot = null)
    {
        _lib = lib;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(lib.Folder).ToLowerInvariant())))[..12].ToLowerInvariant();
        _dir = Path.Combine(indexRoot ?? Path.Combine(Settings.DataDirectory, "inputs-index"), hash);
    }

    private string ChunksFile => Path.Combine(_dir, "chunks.jsonl");
    private string StateFile => Path.Combine(_dir, "files.json");

    public bool Exists => File.Exists(ChunksFile);

    public IReadOnlyList<InputFileState> States()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<List<InputFileState>>(File.ReadAllText(StateFile), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    private List<InputChunk> ReadChunks()
    {
        var list = new List<InputChunk>();
        if (!File.Exists(ChunksFile)) return list;
        foreach (var line in File.ReadLines(ChunksFile))
            try { if (JsonSerializer.Deserialize<InputChunk>(line, Json) is { } c) list.Add(c); } catch (JsonException) { }
        return list;
    }

    /// <summary>Brings the index up to date: reads only new or changed files and drops the ones that are gone.</summary>
    public RefreshReport Refresh(Action<string>? progress = null)
    {
        Directory.CreateDirectory(_dir);
        var inventory = _lib.Inventory();
        var old = States().ToDictionary(s => s.RelPath, StringComparer.OrdinalIgnoreCase);
        var oldChunks = ReadChunks().GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var states = new List<InputFileState>();
        var chunks = new List<InputChunk>();
        int added = 0, updated = 0;
        var unreadable = new List<string>(); var notIndexable = new List<string>();

        foreach (var d in inventory)
        {
            var ticks = d.Modified.Ticks;
            old.TryGetValue(d.RelPath, out var prev);
            var hadChunks = oldChunks.TryGetValue(d.RelPath, out var keep);
            // unchanged = same size and time as when it was read, and what was read is still there
            if (prev is not null && prev.Size == d.Size && prev.Ticks == ticks && (hadChunks || prev.Chunks == 0))
            {
                states.Add(prev);
                if (keep is not null) chunks.AddRange(keep);
                if (prev.Note is not null) (prev.Note.StartsWith("not indexable") ? notIndexable : unreadable).Add($"{d.RelPath}: {prev.Note}");
                continue;
            }
            if (!d.Indexable)
            {
                var note = $"not indexable ({d.Ext})";
                states.Add(new InputFileState(d.RelPath, d.Size, ticks, 0, note)); notIndexable.Add($"{d.RelPath}: {note}");
                if (!old.ContainsKey(d.RelPath)) added++;
                continue;
            }
            progress?.Invoke($"Reading {d.RelPath}");
            var (list, noteRead) = Extract(Path.Combine(_lib.Folder, d.RelPath), d);
            chunks.AddRange(list);
            states.Add(new InputFileState(d.RelPath, d.Size, ticks, list.Count, noteRead));
            if (noteRead is not null) unreadable.Add($"{d.RelPath}: {noteRead}");
            if (old.ContainsKey(d.RelPath)) updated++; else added++;
        }
        var removed = old.Keys.Count(k => inventory.All(d => !d.RelPath.Equals(k, StringComparison.OrdinalIgnoreCase)));

        var tmp = ChunksFile + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            foreach (var c in chunks) w.WriteLine(JsonSerializer.Serialize(c));
        File.Move(tmp, ChunksFile, true);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        _bm25 = null;
        return new RefreshReport(inventory.Count, added, updated, removed, chunks.Count, unreadable, notIndexable);
    }

    public List<InputHit> Search(string query, InputKind? kind = null, int max = 6)
    {
        if (_bm25 is null)
        {
            var b = new Bm25<InputChunk>();
            foreach (var c in ReadChunks()) b.Add(c, c.File + " " + c.Text);        // the file name is part of what a chunk is about
            _bm25 = b;
        }
        var want = kind?.ToString();
        var raw = _bm25.Search(query, Math.Max(max * 8, 40));
        return raw.Where(h => want is null || h.Item.Kind == want).Take(max)
            .Select(h => new InputHit(h.Item.File, h.Item.Kind, h.Item.Loc, h.Item.Page, Math.Round(h.Score, 2), h.Item.Text)).ToList();
    }

    // ------------------------------------------------------------------ reading files into chunks

    private static (List<InputChunk>, string?) Extract(string path, InputDocument d)
    {
        var kind = d.Kind.ToString();
        var list = new List<InputChunk>();
        try
        {
            switch (d.Ext)
            {
                case ".pdf":
                {
                    using var pdf = PdfDocument.Open(path);
                    var pages = 0; var withText = 0;
                    foreach (var page in pdf.GetPages())
                    {
                        pages++;
                        var text = PageLines(page);
                        if (text.Trim().Length < 3) continue;
                        withText++;
                        foreach (var c in ChunkLines(text, 900)) list.Add(new InputChunk(d.RelPath, kind, $"page {page.Number}", page.Number, c));
                    }
                    return (list, pages > 0 && withText == 0 ? "no text layer (a scan?): it cannot be searched or read" : withText < pages ? $"{pages - withText} of {pages} pages have no text layer" : null);
                }
                case ".xlsx": case ".xlsm":
                    ExcelChunks(path, d, kind, list);
                    return (list, list.Count == 0 ? "no cell content found" : null);
                case ".txt": case ".md":
                    foreach (var c in ManualIndex.Chunk(File.ReadAllText(path))) list.Add(new InputChunk(d.RelPath, kind, "text", 1, c));
                    return (list, list.Count == 0 ? "empty" : null);
                default:     // docx, pptx, csv, tsv
                {
                    var t = OfficeText.Read(path, null, 200_000, 5000);
                    foreach (var c in ManualIndex.Chunk(t.Text)) list.Add(new InputChunk(d.RelPath, kind, "text", 1, c));
                    return (list, list.Count == 0 ? "no text found" : null);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return ([], "could not be read: " + e.Message);
        }
    }

    /// <summary>The text of a PDF page with the words of one row kept on one line; a wide gap becomes " | " so table cells stay apart.</summary>
    public static string PageLines(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return "";
        var sb = new StringBuilder();
        var rows = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= Math.Max(2.5, w.BoundingBox.Height * 0.4)) row.Add(w);
            else rows.Add([w]);
        }
        foreach (var row in rows)
        {
            double? prevRight = null;
            foreach (var w in row.OrderBy(w => w.BoundingBox.Left))
            {
                if (prevRight is not null) sb.Append(w.BoundingBox.Left - prevRight > 14 ? " | " : " ");
                sb.Append(w.Text);
                prevRight = w.BoundingBox.Right;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Groups whole lines into pieces of about <paramref name="size"/> characters: a table row is never cut in half.</summary>
    public static IEnumerable<string> ChunkLines(string text, int size)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = Regex.Replace(raw, @"[ \t]+", " ").Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0 && sb.Length + line.Length > size) { yield return sb.ToString().TrimEnd(); sb.Clear(); }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0) yield return sb.ToString().TrimEnd();
    }

    private static void ExcelChunks(string path, InputDocument d, string kind, List<InputChunk> list)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);     // readable while the user has it open in Excel
        using var wb = new XLWorkbook(fs);
        foreach (var ws in wb.Worksheets)
        {
            var range = ws.RangeUsed();
            if (range is null) continue;
            var first = range.FirstRow().RowNumber(); var last = Math.Min(range.LastRow().RowNumber(), first + 20000);
            var c0 = range.FirstColumn().ColumnNumber(); var c1 = range.LastColumn().ColumnNumber();
            string Cell(int r, int c) => ws.Cell(r, c).GetFormattedString().Trim();
            // the header row is the first row with at least two filled cells
            var header = first;
            for (var r = first; r <= Math.Min(last, first + 30); r++) if (Enumerable.Range(c0, c1 - c0 + 1).Count(c => Cell(r, c).Length > 0) >= 2) { header = r; break; }
            var names = Enumerable.Range(c0, c1 - c0 + 1).Select(c => Cell(header, c)).ToArray();
            const int group = 15;
            for (var start = header + 1; start <= last; start += group)
            {
                var end = Math.Min(last, start + group - 1);
                var sb = new StringBuilder();
                for (var r = start; r <= end; r++)
                {
                    var parts = new List<string>();
                    for (var c = c0; c <= c1; c++) { var v = Cell(r, c); if (v.Length > 0) parts.Add(names[c - c0].Length > 0 ? $"{names[c - c0]}={v}" : v); }
                    if (parts.Count > 0) sb.Append($"Row {r}: ").Append(string.Join("; ", parts)).Append('\n');
                }
                if (sb.Length > 0) list.Add(new InputChunk(d.RelPath, kind, $"sheet '{ws.Name}' rows {start}-{end}", 1, sb.ToString().TrimEnd()));
            }
            if (header == last && Enumerable.Range(c0, c1 - c0 + 1).Any(c => names[c - c0].Length > 0))
                list.Add(new InputChunk(d.RelPath, kind, $"sheet '{ws.Name}'", 1, string.Join("; ", names.Where(n => n.Length > 0))));
        }
    }

    // ------------------------------------------------------------------ reading a passage

    /// <summary>The text of one page of a PDF, or some rows of one sheet, or the start of any other indexable file. Only files inside the inputs folder.</summary>
    public (string Text, string Loc, string? Note) ReadPassage(string relPath, int? page = null, string? sheet = null, int fromRow = 1, int rows = 60, int maxChars = 8000)
    {
        var full = _lib.Resolve(relPath) ?? throw new FileNotFoundException("Not a file inside the project inputs folder: " + relPath);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        maxChars = Math.Clamp(maxChars, 500, 60_000);
        string Cut(string t) => t.Length <= maxChars ? t : t[..maxChars] + $"\n... (cut at {maxChars:N0} characters)";
        if (ext == ".pdf")
        {
            using var pdf = PdfDocument.Open(full);
            var n = Math.Clamp(page ?? 1, 1, pdf.NumberOfPages);
            var text = PageLines(pdf.GetPage(n));
            return (Cut(text), $"page {n} of {pdf.NumberOfPages}", text.Trim().Length < 3 ? "this page has no text layer" : null);
        }
        if (ext is ".xlsx" or ".xlsm")
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(fs);
            var ws = (sheet is null ? wb.Worksheets.First() : wb.Worksheets.FirstOrDefault(s => s.Name.Equals(sheet, StringComparison.OrdinalIgnoreCase))) ?? throw new ArgumentException($"No sheet '{sheet}'. Sheets: {string.Join(", ", wb.Worksheets.Select(s => s.Name))}");
            var range = ws.RangeUsed();
            if (range is null) return ("", $"sheet '{ws.Name}'", "the sheet is empty");
            var r0 = Math.Max(fromRow, range.FirstRow().RowNumber()); var r1 = Math.Min(range.LastRow().RowNumber(), r0 + Math.Clamp(rows, 1, 500) - 1);
            var sb = new StringBuilder();
            for (var r = r0; r <= r1; r++)
            {
                var cells = Enumerable.Range(range.FirstColumn().ColumnNumber(), range.ColumnCount()).Select(c => ws.Cell(r, c).GetFormattedString().Trim()).ToList();
                if (cells.Any(c => c.Length > 0)) sb.Append($"{r}\t").Append(string.Join("\t", cells)).Append('\n');
            }
            return (Cut(sb.ToString()), $"sheet '{ws.Name}' rows {r0}-{r1} of {range.LastRow().RowNumber()}", null);
        }
        if (ext is ".txt" or ".md") return (Cut(File.ReadAllText(full)), "text", null);
        var t = OfficeText.Read(full, sheet, maxChars, Math.Clamp(rows, 1, 500));
        return (t.Text, "text", t.Truncated ? "cut short" : null);
    }
}

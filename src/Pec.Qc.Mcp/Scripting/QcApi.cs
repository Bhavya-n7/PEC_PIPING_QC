using System.Text;
using ClosedXML.Excel;
using UglyToad.PdfPig;

namespace Pec.Qc.Mcp.Scripting;

/// <summary>
/// The only way a script written by Claude touches files: it can READ the files the user gave it as arguments (and what is in the project's inputs, models and reports folders) and WRITE
/// into one output folder. Everything else in the file system is closed to it (the policy also refuses System.IO.File and the like).
/// </summary>
public sealed class QcApi(IReadOnlyList<string> readableFiles, IReadOnlyList<string> readableFolders, string outDir)
{
    private readonly List<string> _logs = [];
    private readonly List<string> _written = [];
    private const int MaxOut = 2_000_000;

    public IReadOnlyList<string> Logs => _logs;
    public IReadOnlyList<string> Written => _written;

    private string Allowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is needed.");
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var ok = readableFiles.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))
              || readableFolders.Any(d => full.StartsWith(d.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
              || full.StartsWith(outDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!ok) throw new UnauthorizedAccessException($"A tool may only read the files it was given as arguments and the project's inputs, models and reports: {Path.GetFileName(full)} is not one of them.");
        if (!File.Exists(full)) throw new FileNotFoundException("The file does not exist: " + Path.GetFileName(full));
        return full;
    }

    public string ReadText(string path, int maxChars = 200_000) { var t = File.ReadAllText(Allowed(path)); return t.Length > maxChars ? t[..maxChars] : t; }

    public string[] ReadLines(string path, int maxLines = 5000) => File.ReadLines(Allowed(path)).Take(Math.Clamp(maxLines, 1, 100_000)).ToArray();

    /// <summary>The text of a PDF, one block per page, headed "--- page n ---".</summary>
    public string PdfText(string path, int fromPage = 1, int toPage = 9999, int maxChars = 200_000)
    {
        using var doc = PdfDocument.Open(Allowed(path));
        var sb = new StringBuilder();
        for (var p = Math.Max(1, fromPage); p <= Math.Min(doc.NumberOfPages, toPage) && sb.Length < maxChars; p++)
            sb.AppendLine($"--- page {p} ---").AppendLine(string.Join(" ", doc.GetPage(p).GetWords().Select(w => w.Text)));
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }

    /// <summary>The rows of an Excel sheet (the first when none is named) as text.</summary>
    public List<List<string>> ExcelRows(string path, string? sheet = null, int maxRows = 2000)
    {
        using var wb = new XLWorkbook(Allowed(path));
        var ws = string.IsNullOrWhiteSpace(sheet) ? wb.Worksheets.First() : wb.Worksheet(sheet);
        var cols = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        return ws.RowsUsed().Take(Math.Clamp(maxRows, 1, 50_000)).Select(r => Enumerable.Range(1, cols).Select(c => r.Cell(c).GetFormattedString()).ToList()).ToList();
    }

    private string OutPath(string fileName)
    {
        var name = new string(Path.GetFileName(fileName ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (name.Length == 0) throw new ArgumentException("Give a file name.");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is not (".txt" or ".csv" or ".json" or ".md" or ".xlsx" or ".tsv")) throw new ArgumentException("A tool may write .txt, .csv, .json, .md, .tsv or .xlsx files only.");
        Directory.CreateDirectory(outDir);
        return Path.Combine(outDir, name);
    }

    public string WriteText(string fileName, string text)
    {
        if ((text ?? "").Length > MaxOut) throw new ArgumentException("The output is too large (2 MB at most).");
        var p = OutPath(fileName); File.WriteAllText(p, text ?? "", new UTF8Encoding(false)); _written.Add(p); return p;
    }

    public string WriteExcel(string fileName, List<List<string>> rows, string sheetName = "Data")
    {
        var p = OutPath(fileName);
        using var wb = new XLWorkbook(); var ws = wb.AddWorksheet(string.IsNullOrWhiteSpace(sheetName) ? "Data" : sheetName[..Math.Min(30, sheetName.Length)]);
        for (var r = 0; r < Math.Min(rows.Count, 100_000); r++) for (var c = 0; c < rows[r].Count; c++) ws.Cell(r + 1, c + 1).Value = rows[r][c] ?? "";
        wb.SaveAs(p); _written.Add(p); return p;
    }

    public void Log(string message) { if (_logs.Count < 200) _logs.Add(message.Length > 500 ? message[..500] : message); }
}

/// <summary>What a script sees: its arguments (always text) and the <c>Qc</c> object.</summary>
public sealed class ScriptGlobals
{
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
    public QcApi Qc { get; init; } = null!;
}

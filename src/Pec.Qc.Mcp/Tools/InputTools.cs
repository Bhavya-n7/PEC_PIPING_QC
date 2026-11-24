using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class InputTools
{
    private static InputKind? Kind(string? k)
    {
        if (string.IsNullOrWhiteSpace(k)) return null;
        return InputKinds.Parse(k) ?? throw new McpException("kind must be pms, line-list, pid, vendor or other.");
    }

    private static string Short(string t, int n) => t.Length <= n ? t : t[..n] + "...";

    [McpServerTool(Name = "inputs_list", ReadOnly = true)]
    [Description("What the project's inputs folder holds (PMS, line list, P&IDs, vendor data, other) and what each QC type still needs: the files by kind, which kinds are present or missing for the isometric, P&ID and GA checks, and which files cannot be searched (scans, DWG). Uses the active project unless 'project' names another. Call it before a check to see what the check can be compared against.")]
    public static object List([Description("Project name; empty = the active project")] string? project = null, [Description("Only the coverage of this QC type: iso, pid or ga; empty = all three")] string? qcType = null, [Description("Most files to list, 1-500")] int maxFiles = 200)
    {
        var (lib, card) = Resolve.Inputs(project);
        var inv = lib.Inventory();
        var types = QcTypes.Parse(qcType) is { } one ? [one] : Enum.GetValues<QcType>().ToArray();
        var notes = new InputsIndex(lib).States().Where(s => s.Note is not null).Select(s => $"{s.RelPath}: {s.Note}").ToList();
        return new
        {
            project = card.Name, folder = lib.Folder, files = inv.Count,
            byKind = inv.GroupBy(d => d.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            coverage = types.ToDictionary(t => QcTypes.Code(t), t => lib.Coverage(t, inv).Select(c => new { kind = c.Kind.ToString(), label = InputKinds.Label(c.Kind), c.Present, c.Count, neededFor = c.Why })),
            missing = types.ToDictionary(t => QcTypes.Code(t), t => lib.Coverage(t, inv).Where(c => !c.Present).Select(c => InputKinds.Label(c.Kind))),
            list = inv.Take(Math.Clamp(maxFiles, 1, 500)).Select(d => new { file = d.RelPath, kind = d.Kind.ToString(), sizeKb = Math.Max(1, d.Size / 1024), modified = d.Modified.ToString("yyyy-MM-dd"), d.Indexable }),
            cannotBeRead = notes,
            note = inv.Count == 0 ? "The folder is empty. Put the PMS in pms, the line list in line-list, the P&IDs in pid and the vendor data in vendor (sub-folders are the standard layout; file names with 'PMS', 'line list', 'P&ID' or 'vendor' are recognised too)." : null,
        };
    }

    [McpServerTool(Name = "inputs_search", ReadOnly = true)]
    [Description("Search the project's inputs (PMS, line list, P&IDs, vendor data) and get the passages with the FILE and WHERE in it (page of a PDF, or sheet and rows of an Excel file), so an answer can quote its source. Line numbers and tags are found whole or by a part (B400A, 60A07900, V-101). kind narrows it: pms, line-list, pid, vendor, other. The index refreshes itself when files change. If nothing is found say so; do not say the project 'does not say' something unless every relevant file could be read (see cannotBeRead).")]
    public static object Search([Description("Words, a tag or a line number")] string query, [Description("pms | line-list | pid | vendor | other; empty = everything")] string? kind = null,
        [Description("Project name; empty = the active project")] string? project = null, [Description("Most passages, 1-15")] int max = 6)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("Give the words or the tag to search for.");
        var (lib, card) = Resolve.Inputs(project);
        var idx = new InputsIndex(lib);
        var rep = idx.Refresh();
        var hits = idx.Search(query, Kind(kind), Math.Clamp(max, 1, 15));
        return new
        {
            project = card.Name, query, found = hits.Count,
            hits = hits.Select(h => new { file = h.File, kind = h.Kind, where = h.Loc, h.Page, h.Score, text = Short(h.Text, 700) }),
            cannotBeRead = rep.Unreadable.Concat(rep.NotIndexable).ToList(),
            indexed = new { rep.Files, rep.Chunks, rep.Added, rep.Updated, rep.Removed },
            next = hits.Count == 0 ? "Nothing matched. Try other words, a part of the tag, or search without 'kind'. Check cannotBeRead for files that could not be searched." : "Read the whole page or rows with inputs_read before relying on a passage, and quote the file and where.",
        };
    }

    [McpServerTool(Name = "inputs_read", ReadOnly = true)]
    [Description("Read one page of a PDF in the project's inputs (the text with each table row on one line), or some rows of one sheet of an Excel file (with row numbers), or the start of a Word / text file. 'file' is the path inside the inputs folder as shown by inputs_list or inputs_search. Only files inside the inputs folder can be read.")]
    public static object Read([Description("File path relative to the inputs folder, e.g. pms\\PMS-ACME.pdf")] string file, [Description("PDF page number (1 = first)")] int? page = null,
        [Description("Excel sheet name; empty = the first sheet")] string? sheet = null, [Description("First row to read from an Excel sheet")] int fromRow = 1, [Description("How many rows, 1-500")] int rows = 60,
        [Description("Most characters to return, 500-60000")] int maxChars = 8000, [Description("Project name; empty = the active project")] string? project = null)
    {
        var (lib, card) = Resolve.Inputs(project);
        try
        {
            var (text, loc, note) = new InputsIndex(lib).ReadPassage(file, page, sheet, fromRow, rows, maxChars);
            return new { project = card.Name, file, where = loc, text, note };
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException or IOException or InvalidDataException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "line_list_summary", ReadOnly = true)]
    [Description("The project's line list as data: the files and sheets read, how many lines, which columns were recognised (and under what header) and which were not, lines listed twice, and what could not be understood. Tells you which fields of a line (size, fluid, class, design/operating/test pressure and temperature, insulation, P&ID number...) can be relied on before you use line_list_lookup.")]
    public static object LineListSummary([Description("Project name; empty = the active project")] string? project = null)
    {
        var (lib, card) = Resolve.Inputs(project);
        var ll = LineListSet.Load(lib);
        var recognised = ll.Sheets.SelectMany(s => s.Mapped.Keys).Distinct().ToList();
        return new
        {
            project = card.Name, lines = ll.Entries.Count,
            sheets = ll.Sheets.Select(s => new { file = s.File, sheet = s.Sheet, headerRow = s.HeaderRow, lines = s.Rows, recognised = s.Mapped, notRecognised = s.Unmapped }),
            fieldsNotInAnySheet = LineListSet.Fields.Where(f => !recognised.Contains(f)).ToList(),
            duplicates = ll.Duplicates().Select(d => new { line = d.Key, times = d.Count }),
            warnings = ll.Warnings,
            note = ll.Entries.Count == 0 ? "No line list was read. Put it (xlsx or csv) in the line-list folder of the inputs, or check warnings." : null,
        };
    }

    [McpServerTool(Name = "line_list_lookup", ReadOnly = true)]
    [Description("Look up a line in the project's line list by its line number (any spelling: leading slash, spaces, smart quotes) or by a part of it. Returns each match with the values (text as written, number and unit from the header) and WHERE it is: file, sheet and row. Use these as the source values when comparing an isometric or P&ID with the line list. No match means the line is not in the line list.")]
    public static object LineListLookup([Description("Line number or a part of it, e.g. 2\"-CD-60A07900-B400A-N-HS-017 or 60A07900")] string line, [Description("Project name; empty = the active project")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(line)) throw new McpException("Give the line number.");
        var (lib, card) = Resolve.Inputs(project);
        var ll = LineListSet.Load(lib);
        if (ll.Entries.Count == 0) throw new McpException("No line list could be read for this project (see line_list_summary).");
        var found = ll.Find(line);
        return new
        {
            project = card.Name, searched = line, normalised = LineListSet.Normalize(line), matches = found.Count,
            lines = found.Select(e => new
            {
                lineNo = e.LineNo, source = new { file = e.File, sheet = e.Sheet, row = e.Row },
                values = e.Fields.Where(kv => kv.Key != "LineNo").ToDictionary(kv => kv.Key, kv => kv.Value.Unit is null ? kv.Value.Text : $"{kv.Value.Text} ({kv.Value.Unit})"),
                numbers = e.Fields.Where(kv => kv.Value.Number is not null && kv.Key != "LineNo").ToDictionary(kv => kv.Key, kv => new { value = kv.Value.Number, unit = kv.Value.Unit }),
                extra = e.Extra,
            }),
            note = found.Count == 0 ? "This line is not in the line list: report it as a finding (the drawing has a line the line list does not)." : found.Count > 1 ? "More than one match: if they are the same line listed twice that is itself a data error." : null,
        };
    }
}

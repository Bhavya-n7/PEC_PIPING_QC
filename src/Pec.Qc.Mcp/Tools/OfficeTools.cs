using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.OfficeKit;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Excel, Word and PDF built in: read, create and change files with no Microsoft Office or Acrobat installed. Works on any path the user can reach.</summary>
[McpServerToolType]
public static class OfficeTools
{
    /// <summary>
    /// Replacing a file that already exists (overwrite=true, or saveAs onto another file) asks the user first: text read from a document must not be able to make the
    /// assistant overwrite the user's files on its own. Creating a new file and editing in place with a backup stay as they were.
    /// </summary>
    private static object? OverwriteGate(string tool, string? target, string ext, bool replacing)
    {
        if (!replacing || string.IsNullOrWhiteSpace(target)) return null;
        string full;
        try { full = Path.GetFullPath(target.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || (ext == ".xlsx" && target.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)) ? target : target + ext); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return File.Exists(full) ? Resolve.NeedApproval(tool, full.ToLowerInvariant(), $"Replace the existing file {Path.GetFileName(full)}", full) : null;
    }

    private static object Run(Func<object> f)
    {
        try { return f(); }
        catch (Exception e) when (e is FileNotFoundException or NotSupportedException or InvalidDataException or ArgumentException or IOException
            or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            throw new McpException(e.Message + (e is JsonException ? " (the JSON text is not valid: check quotes and commas)" : ""));
        }
    }

    // ------------------------------------------------------------------ Excel

    [McpServerTool(Name = "excel_inspect", ReadOnly = true)]
    [Description("List the sheets of an Excel workbook (.xlsx/.xlsm) with size, used range, number of formulas and the first row (headers). Start here before reading or editing a workbook.")]
    public static object ExcelInspect(string path) => Run(() => new { path, sheets = ExcelKit.Inspect(path) });

    [McpServerTool(Name = "excel_read", ReadOnly = true)]
    [Description("Read cells of a sheet as rows with calculated values. 'range' like A1:F40 (default: all used cells), 'sheet' name or number (default: first). formulas=true also shows each cell's formula.")]
    public static object ExcelRead(string path, string? sheet = null, string? range = null, [Description("1-5000")] int maxRows = 200, bool formulas = false) =>
        Run(() => ExcelKit.Read(path, sheet, range, maxRows, formulas));

    [McpServerTool(Name = "excel_write")]
    [Description("Create a NEW Excel workbook (no Excel needed). 'sheets' is JSON text: [{\"name\":\"Data\",\"rows\":[[\"Tag\",\"Qty\"],[\"P-101\",2],[\"Total\",\"=SUM(B2:B2)\"]],\"widths\":[20,10]}]. The first row becomes a bold frozen header (header:false to skip). A text starting with = is a formula. Refuses to replace an existing file unless overwrite=true; to change an existing workbook use excel_edit.")]
    public static object ExcelWrite(string path, [Description("JSON array of {name, rows[, header, widths]}")] string sheets, bool overwrite = false) =>
        OverwriteGate("excel_write", path, ".xlsx", overwrite) ?? Run(() => { var p = ExcelKit.Create(path, sheets, overwrite); return new { saved = p, next = "Check it with excel_read. Tell the user the full path." }; });

    [McpServerTool(Name = "excel_edit")]
    [Description("Change an existing workbook and save (a .bak_ copy of the original is kept). 'operations' is JSON text, a list applied in order; if one fails nothing is saved. Ops: {op:set,sheet,cell,value} {op:formula,sheet,cell,formula} {op:setRange,sheet,start,rows:[[..]]} {op:append,sheet,rows} {op:clear,sheet,range} {op:style,sheet,range,bold,italic,fill:\"#RRGGBB\",fontColor,fontSize,numberFormat,align,wrap,border} {op:width,sheet,column,width?} {op:merge,sheet,range} {op:freeze,sheet,rows,columns} {op:autofilter,sheet,range} {op:sort,sheet,range,column,descending} {op:insertRows|deleteRows,sheet,at,count} {op:insertColumns|deleteColumns,sheet,at(letter),count} {op:addSheet,name} {op:deleteSheet,sheet} {op:renameSheet,sheet,to} {op:copySheet,sheet,to} {op:hyperlink,sheet,cell,url}. saveAs writes a copy instead of changing the original. Charts and macros are not supported (macros in .xlsm are kept).")]
    public static object ExcelEdit(string path, [Description("JSON array of {op, ...}")] string operations, string? saveAs = null) =>
        OverwriteGate("excel_edit", saveAs, ".xlsx", true) ?? Run(() => { var (saved, done, bak) = ExcelKit.Edit(path, operations, saveAs); return new { saved, done, backup = bak, next = "Verify with excel_read (formulas=true to see the formulas)." }; });

    // ------------------------------------------------------------------ Word

    [McpServerTool(Name = "word_write")]
    [Description("Create a NEW Word document (.docx, no Word needed). 'blocks' is JSON text: [{\"type\":\"heading\",\"level\":1,\"text\":\"Title\"},{\"type\":\"paragraph\",\"text\":\"...\",\"bold\":true},{\"type\":\"bullets\",\"items\":[\"a\",\"b\"]},{\"type\":\"numbered\",\"items\":[..]},{\"type\":\"table\",\"rows\":[[\"H1\",\"H2\"],[\"a\",\"b\"]]},{\"type\":\"pagebreak\"}]. Read an existing .docx with file_read.")]
    public static object WordWrite(string path, [Description("JSON array of blocks")] string blocks, bool overwrite = false) =>
        OverwriteGate("word_write", path, ".docx", overwrite) ?? Run(() => new { saved = WordKit.Create(path, blocks, overwrite), next = "Check it with file_read. Tell the user the full path." });

    [McpServerTool(Name = "word_edit")]
    [Description("Change an existing .docx and save (a .bak_ copy of the original is kept). 'operations' is JSON text: {op:replace,find,replace} (also in headers and footers, even when Word split the text), {op:append,blocks:[..same blocks as word_write..]}, {op:deleteParagraph,contains}, {op:setCell,table,row,column,text}. saveAs writes a copy.")]
    public static object WordEdit(string path, [Description("JSON array of {op, ...}")] string operations, string? saveAs = null) =>
        OverwriteGate("word_edit", saveAs, ".docx", true) ?? Run(() => { var (saved, done, bak) = WordKit.Edit(path, operations, saveAs); return new { saved, done, backup = bak, next = "Verify with file_read." }; });

    // ------------------------------------------------------------------ PDF

    [McpServerTool(Name = "pdf_read", ReadOnly = true)]
    [Description("Read the text of a PDF page by page. 'pages' like 1-3,7 (default all). A scanned PDF has no text: the result says so; then read the pages as images or use OCR.")]
    public static object PdfRead(string path, string? pages = null, [Description("500-300000")] int maxChars = 40000) => Run(() => PdfKit.Read(path, pages, maxChars));

    [McpServerTool(Name = "pdf_write")]
    [Description("Create a NEW PDF (A4, text wraps, new pages as needed). 'blocks' is JSON text, same shape as word_write: heading, paragraph, bullets, numbered, table, pagebreak. For a designed layout write HTML and print it with a browser, or use a Python library.")]
    public static object PdfWrite(string path, [Description("JSON array of blocks")] string blocks, string? title = null, bool overwrite = false) =>
        OverwriteGate("pdf_write", path, ".pdf", overwrite) ?? Run(() => new { saved = PdfKit.Create(path, blocks, overwrite, title), next = "Check it with pdf_read. Tell the user the full path." });

    [McpServerTool(Name = "pdf_find", ReadOnly = true)]
    [Description("Find where a text is on a PDF page: returns every occurrence with its box [x0,y0,x1,y1] in PDF points (origin bottom left, the same space pdf_markup uses). A phrase of several words is found when the words follow each other on a line. Use it to put a cloud or highlight exactly on an item.")]
    public static object PdfFind(string path, string text, string? pages = null) => Run(() =>
    {
        var hits = PdfMarkup.Find(path, text, pages);
        return new { count = hits.Count, hits = hits.Take(200).Select(h => new { page = h.Page, text = h.Text, rect = new[] { Math.Round(h.Box.X0, 1), Math.Round(h.Box.Y0, 1), Math.Round(h.Box.X1, 1), Math.Round(h.Box.Y1, 1) } }) };
    });

    [McpServerTool(Name = "pdf_markup")]
    [Description("QC markup on a PDF drawing as real, editable annotations (Bluebeam / Acrobat); the drawing itself is never changed, the result is a NEW file. 'items' is JSON text: [{\"page\":1,\"type\":\"cloud\",\"find\":\"PSV-512F\",\"comment\":\"SET PRESSURE TO BE 150 PSIG AS PER RAW\",\"color\":\"red\"}, {\"type\":\"cloud\",\"rect\":[x0,y0,x1,y1],\"comment\":\"...\",\"color\":\"blue\"}, {\"type\":\"highlight\",\"find\":\"LINE-1234\",\"all\":true}, {\"type\":\"legend\",\"lines\":[{\"text\":\"RED = CORRECTION\",\"color\":\"red\"},{\"text\":\"OK: ...\",\"color\":\"green\"}]}]. A cloud with a comment gets a small boxed comment with a leader arrow, placed automatically in free space (not on text, linework or other comments); 'at':[x,y] forces a position. Colours: red, blue, green, yellow, black. 'find' (text on the page; 'all':true marks every occurrence, 'occurrence':n one) or 'rect' (PDF points, origin bottom left, as pdf_find returns). Comments are written in capitals. The result lists warnings (text not found, no free space): fix each, and check the result by looking at the pages.")]
    public static object PdfMarkupTool(string path, string output, [Description("JSON array of markup items")] string items, bool overwrite = false) =>
        OverwriteGate("pdf_markup", output, ".pdf", overwrite) ?? Run(() => PdfMarkup.Apply(path, output, items, overwrite));

    [McpServerTool(Name = "pdf_zones", ReadOnly = true)]
    [Description("Plan how to LOOK at a drawing page like a checker: the sheet size class (A0-A4 from the real page size), the sheet's own border grid zones (columns and rows read from the tick marks of the border; ISO 5457 default if there are none), and a set of review tiles that follow the grid, with the zoom (dpi) that suits the sheet size and, for a scan, its own resolution. Same tiles on the client's drawing and on ours, even when the sheets differ in size (an A3 scan against an A0 redraw): compare tile with tile. 'cols' / 'rows' give the sheet's real labels, for example \"17-1\" and \"K-A\" (read them from the border). Each tile has a rect and dpi for pdf_render. 'across' / 'down' force the same tile count on both drawings of a pair. Every rectangle is in PDF points of the page as displayed, origin bottom left.")]
    public static object PdfZonesTool(string path, int page = 1, string? cols = null, string? rows = null, [Description("tiles across; 0 = by sheet size. Give the same on both drawings of a pair")] int across = 0, [Description("tiles down; 0 = by sheet size")] int down = 0) => Run(() => PdfZones.MakePlan(path, page, cols, rows, across, down));

    [McpServerTool(Name = "pdf_render")]
    [Description("Draw a PDF page (with its annotations) as a PNG so you can LOOK at it with the Read tool. 'rect' [x0,y0,x1,y1] (PDF points, origin bottom left, as pdf_find gives) crops to a zone: use it with dpi 200-300 to check one zone of a large drawing at high zoom. Without rect the whole page is drawn (dpi 100 is enough for an overview). Saves the PNG next to 'output'.")]
    public static object PdfRenderTool(string path, int page, string output, [Description("JSON [x0,y0,x1,y1] or empty for the whole page")] string? rect = null, int dpi = 100, bool overwrite = false) =>
        OverwriteGate("pdf_render", output, ".png", overwrite) ?? Run(() =>
        {
            var r = string.IsNullOrWhiteSpace(rect) ? null : JsonSerializer.Deserialize<double[]>(rect);
            var (saved, w, h) = PdfZones.Render(path, page, r, dpi, output, overwrite);
            return new { saved, width = w, height = h, next = "Look at it with the Read tool." };
        });

    [McpServerTool(Name = "pdf_merge")]
    [Description("Join PDF files (all pages, in the order given) into a new PDF. 'files' is JSON text: [\"a.pdf\",\"b.pdf\"].")]
    public static object PdfMerge([Description("JSON array of file paths")] string files, string output, bool overwrite = false) =>
        OverwriteGate("pdf_merge", output, ".pdf", overwrite) ?? Run(() =>
        {
            var list = JsonSerializer.Deserialize<List<string>>(files) ?? [];
            var (saved, pages) = PdfKit.Merge(list, output, overwrite); return new { saved, pages };
        });

    [McpServerTool(Name = "pdf_extract")]
    [Description("Copy chosen pages (for example 1-3,7) of a PDF into a new PDF.")]
    public static object PdfExtract(string path, string pages, string output, bool overwrite = false) =>
        OverwriteGate("pdf_extract", output, ".pdf", overwrite) ?? Run(() => { var (saved, n) = PdfKit.Extract(path, pages, output, overwrite); return new { saved, pages = n }; });
}

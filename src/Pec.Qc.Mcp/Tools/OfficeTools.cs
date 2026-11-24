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

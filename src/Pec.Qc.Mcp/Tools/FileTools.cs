using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Sources;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class FileTools
{
    [McpServerTool(Name = "file_read", ReadOnly = true)]
    [Description("Read the TEXT of a Word (.docx), Excel (.xlsx/.xlsm), PowerPoint (.pptx), CSV or TSV file, for example a file the user attached in the chat (its path is given in the message). Excel returns each sheet as tab-separated rows (give 'sheet' as a name or number to read one). Old .doc/.xls/.ppt files must be saved in the new format first. Images, PDFs and plain text are read with the Read tool instead.")]
    public static object Read(string path, string? sheet = null, [Description("Characters to return, 500-200000")] int maxChars = 30000,
        [Description("Rows per sheet, 1-5000")] int maxRows = 200)
    {
        try
        {
            var r = OfficeText.Read(path, sheet, maxChars, maxRows);
            return new { kind = r.Kind, truncated = r.Truncated, sheets = r.Sheets, text = r.Text,
                note = r.Truncated ? "Cut short: ask for one sheet, fewer rows or more characters if you need more." : null };
        }
        catch (Exception e) when (e is FileNotFoundException or NotSupportedException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(e.Message);
        }
    }
}

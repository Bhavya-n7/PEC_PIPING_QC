using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Iso;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Reading a piping isometric PDF as data.</summary>
[McpServerToolType]
public static partial class IsoTools
{
    internal static string ResolvePdf(string file, string? project)
    {
        var f = (file ?? "").Trim().Trim('"');
        if (f.Length == 0) throw new McpException("Give the isometric PDF (a full path, or a file in the project's inputs folder).");
        if (File.Exists(f)) return Path.GetFullPath(f);
        try
        {
            var (lib, _) = Resolve.Inputs(project);
            var hit = Directory.EnumerateFiles(lib.Folder, "*.*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".pdf" or ".dxf" or ".dwg").FirstOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        catch (McpException) { }
        throw new McpException($"The file does not exist: {f}");
    }

    [McpServerTool(Name = "iso_read", ReadOnly = true)]
    [Description("Read a piping isometric (spool drawing) PDF as data: for every sheet the line number, sheet x of n, revision, the title-block parameters (test / design / operating pressure and temperature, test medium, paint, insulation, phase, P&ID no., stress criticality, piping class, PWHT), the BOM (section, category, item, description, size, quantity), the cut pipe lengths, the E/N/U coordinate labels (inches) with CONT. ON / FROM connections and the CONT. DRG sheet links. Warnings say what could not be read. Use it to answer a question about the drawing or to see what iso_check was given. A drawing that is a scan has no text and is reported as unreadable.")]
    public static object Read([Description("Path of the isometric PDF, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null,
        [Description("Only this sheet (page number); empty = all")] int? sheet = null)
    {
        var path = ResolvePdf(file, project);
        List<IsoSheet> sheets;
        try { sheets = IsoReader.Read(path); }
        catch (Exception e) when (e is not McpException) { throw new McpException($"The PDF cannot be read: {e.Message}"); }
        if (sheet is { } n) sheets = sheets.Where(s => s.Page == n).ToList();
        return new
        {
            file = path, sheets = sheets.Count,
            result = sheets.Select(s => new
            {
                page = s.Page, line = s.LineNo, sheet = s.Sheet, of = s.Sheets, revision = s.Revision, titleBlock = s.Params,
                bom = s.Bom.Select(b => new { b.Section, b.Category, item = b.PtNo, b.Description, b.Size, quantity = b.QtyText, value = b.Qty, unit = b.Category == "PIPE" ? "in" : "no." }).ToList(),
                cutPipe = s.Cuts.Select(c => new { c.Piece, length = c.LengthText, inches = c.LengthIn, c.Size }).ToList(),
                coordinates = s.Coords.Select(c => new { e = Units.FormatInches(c.E), n = Units.FormatInches(c.N), u = Units.FormatInches(c.U), connection = c.Cont, c.ContLine }).ToList(),
                links = s.Links.Select(l => $"CONT. {l.Kind} DRG {l.Drg}").ToList(), s.Warnings,
            }).ToList(),
            next = "Give the user what they asked for. Report the warnings: a warning means that part was not read, not that it is correct.",
        };
    }
}

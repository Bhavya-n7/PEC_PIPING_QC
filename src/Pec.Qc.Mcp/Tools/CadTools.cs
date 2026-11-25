using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Cad;

namespace Pec.Qc.Mcp.Tools;

/// <summary>CAD drawings (DXF, and DWG through AutoCAD's console): what is in them as data.</summary>
[McpServerToolType]
public static class CadTools
{
    private static string Resolve_(string file, string? project)
    {
        var f = (file ?? "").Trim().Trim('"');
        if (f.Length == 0) throw new McpException("Give the DXF or DWG (a full path, or a file in the project's inputs folder).");
        if (File.Exists(f)) return Path.GetFullPath(f);
        try
        {
            var (lib, _) = Resolve.Inputs(project);
            var hit = Directory.EnumerateFiles(lib.Folder, "*.*", SearchOption.AllDirectories).FirstOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p) is ".dxf" or ".dwg" or ".DXF" or ".DWG");
            if (hit is not null) return hit;
        }
        catch (McpException) { }
        throw new McpException($"The file does not exist: {f}");
    }

    [McpServerTool(Name = "cad_read", ReadOnly = true)]
    [Description("Read a CAD drawing as data: DXF directly; DWG is converted to DXF first with AutoCAD's console (needs AutoCAD on this PC, takes 10-60 s, works on a copy). Returns the units, drawing extents, entity counts by type and by layer, the layers, the block definitions, every TEXT / MTEXT / attribute / dimension text with layer and position, every block insert with its attributes (tags, descriptions) and the line length per layer. Geometry is summarised, not reproduced: for how it looks, export a PDF and read that. Use it to answer a question about the drawing, to compare tags or notes with other documents, or to check a drawing's content against the project inputs. 'contains' keeps only texts with these words.")]
    public static object Read([Description("Path of the DXF/DWG, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null,
        [Description("Only texts containing these words (case-insensitive)")] string? contains = null, [Description("Texts to return, 1-2000")] int maxTexts = 300)
    {
        var path = Resolve_(file, project);
        string dxf = path; string? converted = null;
        if (Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
        {
            try { dxf = converted = DwgConverter.ToDxf(path, Path.Combine(Settings.DataDirectory, "cad-cache")); }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException) { throw new McpException(e.Message); }
        }
        else if (!Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase)) throw new McpException("cad_read reads .dxf and .dwg files only.");
        DxfDrawing d;
        try { d = DxfReader.Read(dxf); } catch (InvalidDataException e) { throw new McpException(e.Message); }
        var texts = d.Texts.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(contains)) texts = texts.Where(t => t.Text.Contains(contains.Trim(), StringComparison.OrdinalIgnoreCase));
        var all = texts.ToList(); var n = Math.Clamp(maxTexts, 1, 2000);
        return new
        {
            file = path, convertedFrom = converted is null ? null : "DWG (through AutoCAD)", version = d.Version, units = d.Units, extents = d.Extents.Length == 4 ? new { minX = d.Extents[0], minY = d.Extents[1], maxX = d.Extents[2], maxY = d.Extents[3] } : null,
            entityCounts = d.EntityCounts, entitiesByLayer = d.EntitiesByLayer, lineLengthByLayer = d.LineLengthByLayer.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)), layers = d.Layers, blockDefinitions = d.BlockDefinitions,
            textCount = all.Count, texts = all.Take(n).Select(t => new { t.Layer, t.Kind, t.Text, x = Math.Round(t.X, 1), y = Math.Round(t.Y, 1), t.Height }),
            insertCount = d.Inserts.Count, inserts = d.Inserts.Take(n).Select(i => new { i.Block, i.Layer, x = Math.Round(i.X, 1), y = Math.Round(i.Y, 1), attributes = i.Attributes }),
            warnings = d.Warnings.Distinct().ToList(),
            next = "Quote what the file says with its layer. Say what this does NOT cover: how the drawing looks, anything drawn as geometry instead of text, and blocks without attributes.",
        };
    }
}

using Pec.Qc.Core.Pid;

namespace Pec.Qc.Core.Cad;

/// <summary>
/// Turns the texts of a DXF / DWG into the placed text lines the P&amp;ID and GA readers work on (the same lines they get from a PDF page), so the same reading and the same checks apply.
/// The drawing's extent is fitted to the reference sheet (A3 in points, y down) so zones and move tolerances mean the same as for a PDF. Block attributes become texts at the block's place.
/// </summary>
public static class CadLines
{
    public static bool IsCad(string path) => Path.GetExtension(path) is { } e && (e.Equals(".dxf", StringComparison.OrdinalIgnoreCase) || e.Equals(".dwg", StringComparison.OrdinalIgnoreCase));

    public static DxfDrawing Drawing(string path)
    {
        var dxf = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? DwgConverter.ToDxf(path, Path.Combine(Settings.DataDirectory, "cad-cache")) : path;
        return DxfReader.Read(dxf);
    }

    public static List<PidReader.RawLine> FromFile(string path) => From(Drawing(path));

    public static List<PidReader.RawLine> From(DxfDrawing d)
    {
        var items = d.Texts.Where(t => !t.Kind.StartsWith("ATTDEF", StringComparison.Ordinal) && t.Text.Length > 0).ToList();
        if (items.Count == 0) return [];
        var xs = items.Select(t => t.X).Concat(d.Extents.Length == 4 ? [d.Extents[0], d.Extents[2]] : []).ToList();
        var ys = items.Select(t => t.Y).Concat(d.Extents.Length == 4 ? [d.Extents[1], d.Extents[3]] : []).ToList();
        double minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max();
        var w = Math.Max(maxX - minX, 1); var h = Math.Max(maxY - minY, 1);
        var s = Math.Min((PidReader.RefWidth - 40) / w, (PidReader.RefHeight - 40) / h);       // 20 points margin each side
        var lines = new List<PidReader.RawLine>();
        foreach (var t in items.OrderBy(t => -t.Y).ThenBy(t => t.X))
        {
            // text height on the reference sheet: the drawing's own height when it has one, else a normal 8 points
            var hh = t.Height > 0 ? Math.Clamp(t.Height * s, 3, 40) : 8;
            var len = t.Text.Length * hh * 0.6;
            var rot = ((t.Rotation % 360) + 360) % 360;
            var vertical = Math.Abs(rot - 90) < 10 || Math.Abs(rot - 270) < 10;
            var x = 20 + (t.X - minX) * s; var y = 20 + (maxY - t.Y) * s;                         // y down, like a PDF page
            foreach (var part in t.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((p, k) => (p, k)))
            {
                var l = part.p.Length * hh * 0.6; var yy = y + part.k * hh * 1.4;
                lines.Add(vertical ? new PidReader.RawLine(part.p, x + part.k * hh * 1.4, y - l, x + part.k * hh * 1.4 + hh, y, true)
                                   : new PidReader.RawLine(part.p, x, yy - hh, x + l, yy, false));
            }
        }
        return lines;
    }
}

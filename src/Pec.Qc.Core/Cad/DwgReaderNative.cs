using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Pec.Qc.Core.Cad;

/// <summary>
/// Reads a DWG directly (no AutoCAD needed) with a library built into the product, into the same <see cref="DxfDrawing"/> the DXF reader gives. Opens the file read-only.
/// Newer or unusual DWG files may not be understood: the caller then falls back to AutoCAD's console when it is installed.
/// </summary>
public static class DwgReaderNative
{
    public static DxfDrawing Read(string path)
    {
        CadDocument doc;
        using (var r = new DwgReader(path)) doc = r.Read();
        var d = new DxfDrawing(doc.Header.Version.ToString(), doc.Header.InsUnits.ToString().ToLowerInvariant(), [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], [], [], [], []);
        foreach (var l in doc.Layers) d.Layers.Add(l.Name);
        foreach (var b in doc.BlockRecords) if (!b.Name.StartsWith('*')) d.BlockDefinitions.Add(b.Name);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Ext(double x, double y) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        foreach (var e in doc.Entities)
        {
            var type = e.ObjectName; var layer = e.Layer?.Name ?? "";
            d.EntityCounts[type] = d.EntityCounts.GetValueOrDefault(type) + 1; d.EntitiesByLayer[layer] = d.EntitiesByLayer.GetValueOrDefault(layer) + 1;
            switch (e)
            {
                case Line l:
                    d.LineLengthByLayer[layer] = d.LineLengthByLayer.GetValueOrDefault(layer) + Math.Sqrt(Math.Pow(l.EndPoint.X - l.StartPoint.X, 2) + Math.Pow(l.EndPoint.Y - l.StartPoint.Y, 2) + Math.Pow(l.EndPoint.Z - l.StartPoint.Z, 2));
                    Ext(l.StartPoint.X, l.StartPoint.Y); Ext(l.EndPoint.X, l.EndPoint.Y); break;
                case TextEntity t when !string.IsNullOrEmpty(t.Value):
                    d.Texts.Add(new DxfText(layer, DxfReader.Clean(t.Value), t.InsertPoint.X, t.InsertPoint.Y, t.Height, "TEXT", t.Rotation * 180 / Math.PI)); Ext(t.InsertPoint.X, t.InsertPoint.Y); break;
                case MText m when !string.IsNullOrEmpty(m.Value):
                    d.Texts.Add(new DxfText(layer, DxfReader.Clean(m.Value), m.InsertPoint.X, m.InsertPoint.Y, m.Height, "MTEXT", m.Rotation * 180 / Math.PI)); Ext(m.InsertPoint.X, m.InsertPoint.Y); break;
                case Insert i:
                {
                    var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in i.Attributes)
                    {
                        var v = DxfReader.Clean(a.Value ?? "");
                        if (a.Tag.Length > 0) attrs[a.Tag] = v;
                        if (v.Length > 0) d.Texts.Add(new DxfText(a.Layer?.Name ?? layer, v, a.InsertPoint.X, a.InsertPoint.Y, a.Height, "ATTRIB:" + a.Tag, a.Rotation * 180 / Math.PI));
                    }
                    d.Inserts.Add(new DxfBlockUse(i.Block?.Name ?? "", layer, i.InsertPoint.X, i.InsertPoint.Y, attrs)); Ext(i.InsertPoint.X, i.InsertPoint.Y); break;
                }
                case Dimension dim:
                    if (!string.IsNullOrEmpty(dim.Text)) d.Texts.Add(new DxfText(layer, DxfReader.Clean(dim.Text), dim.TextMiddlePoint.X, dim.TextMiddlePoint.Y, 0, "DIMENSION"));
                    else d.Warnings.Add("A dimension has its measured value only (no override text): the number is not in the file as text.");
                    break;
                case Circle c: Ext(c.Center.X, c.Center.Y); break;
            }
        }
        if (minX <= maxX) d = d with { Extents = [Math.Round(minX, 3), Math.Round(minY, 3), Math.Round(maxX, 3), Math.Round(maxY, 3)] };
        if (d.Inserts.Count > 0 && d.Inserts.All(a => a.Attributes.Count == 0))
            d.Warnings.Add("Blocks are in the drawing but none has attributes: tags may be drawn as geometry, which is not read.");
        return d;
    }
}

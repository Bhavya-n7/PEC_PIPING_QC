using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Cad;

public sealed record DxfText(string Layer, string Text, double X, double Y, double Height, string Kind, double Rotation = 0);
public sealed record DxfBlockUse(string Block, string Layer, double X, double Y, Dictionary<string, string> Attributes);
public sealed record DxfDrawing(string Version, string Units, double[] Extents, Dictionary<string, int> EntityCounts, Dictionary<string, int> EntitiesByLayer,
    Dictionary<string, double> LineLengthByLayer, List<string> Layers, List<string> BlockDefinitions, List<DxfText> Texts, List<DxfBlockUse> Inserts, List<string> Warnings);

/// <summary>
/// Reads an ASCII DXF drawing: what is in model space (counts by type and layer), every text (TEXT, MTEXT, attributes, dimension texts), every block insert with its attributes
/// (tags, line numbers, instrument data), line lengths and the drawing extents. Geometry is summarised, not reproduced. A DWG is converted to DXF first (see <see cref="DwgConverter"/>).
/// </summary>
public static class DxfReader
{
    private static readonly string[] Units = ["unitless", "inches", "feet", "miles", "mm", "cm", "m", "km"];

    public static DxfDrawing Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 22 && Encoding.ASCII.GetString(bytes, 0, 22).StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            throw new InvalidDataException("This is a binary DXF. Save it as an ASCII DXF (or open it in AutoCAD and export a PDF).");
        var probe = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4000));
        var version = Regex.Match(probe, @"\$ACADVER\s*\r?\n\s*1\s*\r?\n\s*(AC\d+)").Groups[1].Value;
        var utf8 = string.CompareOrdinal(version, "AC1021") >= 0 && version.Length > 0 || version.Length == 0;
        var text = (utf8 ? new UTF8Encoding(false) : Encoding.Latin1).GetString(bytes);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var pairs = new List<(int Code, string Value)>(lines.Length / 2);
        for (var i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) pairs.Add((c, lines[i + 1].Trim('\r')));
            else { i--; }                                          // out of step: resynchronise
        if (pairs.Count == 0 || !pairs.Any(p => p.Code == 0 && p.Value == "SECTION")) throw new InvalidDataException("This does not look like a DXF file (no sections).");

        var d = new DxfDrawing(version, "unknown", [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], [], [], [], []);
        string section = "";
        var i2 = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Extend(double x, double y) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }

        while (i2 < pairs.Count)
        {
            var (code, val) = pairs[i2];
            if (code == 0 && val == "SECTION") { section = i2 + 1 < pairs.Count && pairs[i2 + 1].Code == 2 ? pairs[i2 + 1].Value : ""; i2 += 2; continue; }
            if (code == 0 && val == "ENDSEC") { section = ""; i2++; continue; }
            if (section == "HEADER" && code == 9 && val == "$INSUNITS" && i2 + 1 < pairs.Count && int.TryParse(pairs[i2 + 1].Value, out var u))
                d = d with { Units = u >= 0 && u < Units.Length ? Units[u] : u switch { 14 => "dm", _ => "code " + u } };
            if (code == 0 && (section == "TABLES" || section == "BLOCKS" || section == "ENTITIES"))
            {
                var type = val; var j = i2 + 1; var props = new List<(int Code, string Value)>();
                while (j < pairs.Count && pairs[j].Code != 0) { props.Add(pairs[j]); j++; }
                string P(int c) => props.FirstOrDefault(p => p.Code == c).Value ?? "";
                double N(int c) => double.TryParse(P(c), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                if (section == "TABLES" && type == "LAYER" && P(2).Length > 0 && !d.Layers.Contains(P(2))) d.Layers.Add(P(2));
                else if (section == "BLOCKS" && type == "BLOCK" && P(2).Length > 0 && !P(2).StartsWith('*')) d.BlockDefinitions.Add(P(2));
                else if (section == "ENTITIES")
                {
                    var layer = P(8); var x = N(10); var y = N(20);
                    if (type is not ("SEQEND" or "VERTEX")) { d.EntityCounts[type] = d.EntityCounts.GetValueOrDefault(type) + 1; d.EntitiesByLayer[layer] = d.EntitiesByLayer.GetValueOrDefault(layer) + 1; }
                    switch (type)
                    {
                        case "LINE":
                            d.LineLengthByLayer[layer] = d.LineLengthByLayer.GetValueOrDefault(layer) + Math.Sqrt(Math.Pow(N(11) - x, 2) + Math.Pow(N(21) - y, 2) + Math.Pow(N(31) - N(30), 2));
                            Extend(x, y); Extend(N(11), N(21)); break;
                        case "TEXT": if (P(1).Length > 0) d.Texts.Add(new DxfText(layer, Clean(P(1)), x, y, N(40), "TEXT", N(50))); Extend(x, y); break;
                        case "MTEXT":
                            d.Texts.Add(new DxfText(layer, Clean(string.Concat(props.Where(p => p.Code == 3).Select(p => p.Value)) + P(1)), x, y, N(40), "MTEXT", N(50))); Extend(x, y); break;
                        case "ATTRIB" or "ATTDEF":
                            if (P(1).Length > 0) d.Texts.Add(new DxfText(layer, Clean(P(1)), x, y, N(40), type == "ATTRIB" ? "ATTRIB:" + P(2) : "ATTDEF:" + P(2), N(50))); break;
                        case "DIMENSION": if (P(1).Length > 0) d.Texts.Add(new DxfText(layer, Clean(P(1)), N(11), N(21), 0, "DIMENSION")); else d.Warnings.Add("A dimension has its measured value only (no override text): the number is not in the file as text."); break;
                        case "INSERT":
                        {
                            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            var k = j;                                   // the ATTRIBs that follow, up to SEQEND
                            if (P(66).Trim() == "1")
                                while (k < pairs.Count && !(pairs[k].Code == 0 && pairs[k].Value == "SEQEND"))
                                {
                                    if (pairs[k].Code == 0 && pairs[k].Value == "ATTRIB")
                                    {
                                        var m = k + 1; string tag = "", v2 = "";
                                        while (m < pairs.Count && pairs[m].Code != 0) { if (pairs[m].Code == 2) tag = pairs[m].Value; else if (pairs[m].Code == 1) v2 = Clean(pairs[m].Value); m++; }
                                        if (tag.Length > 0) attrs[tag] = v2;
                                    }
                                    k++;
                                }
                            d.Inserts.Add(new DxfBlockUse(P(2), layer, x, y, attrs)); Extend(x, y); break;
                        }
                        case "CIRCLE" or "ARC" or "POINT" or "LWPOLYLINE" or "SOLID" or "ELLIPSE": Extend(x, y); break;
                    }
                }
                i2 = j; continue;
            }
            i2++;
        }
        if (minX <= maxX) d = d with { Extents = [Math.Round(minX, 3), Math.Round(minY, 3), Math.Round(maxX, 3), Math.Round(maxY, 3)] };
        if (d.EntityCounts.ContainsKey("INSERT") && d.Inserts.Count > 0 && d.Inserts.All(a => a.Attributes.Count == 0))
            d.Warnings.Add("Blocks are in the drawing but none has attributes: tags may be drawn as geometry, which is not read.");
        return d;
    }

    /// <summary>Plain text of a DXF string: MTEXT formatting codes, %%-symbols and unicode escapes resolved.</summary>
    public static string Clean(string s)
    {
        s = Regex.Replace(s, @"\\U\+([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = s.Replace("\\P", "\n").Replace("\\~", " ").Replace("%%d", "°").Replace("%%D", "°").Replace("%%p", "±").Replace("%%P", "±").Replace("%%c", "Ø").Replace("%%C", "Ø");
        s = Regex.Replace(s, @"\\[fFcCtTqQwWhH][^;]*;", "");           // font / colour / height / width settings
        s = Regex.Replace(s, @"\\[LlOoKk]", "");                        // underline, overline, strike
        s = Regex.Replace(s, @"\\S([^^;]*)\^([^;]*);", "$1/$2");       // stacked text
        return s.Replace("{", "").Replace("}", "").Replace("\\\\", "\\").Trim();
    }
}

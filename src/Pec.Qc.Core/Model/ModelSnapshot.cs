using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Model;

public sealed record ModelItem(string Name, string Type, IReadOnlyDictionary<string, string> Attrs)
{
    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : "";
    public string Spec => Attr("SPRE");

    /// <summary>The positions this item has (position, arrive and leave points), in mm.</summary>
    public IEnumerable<(double E, double N, double U)> Points()
    {
        foreach (var k in new[] { "POS", "APOS", "LPOS" })
            if (Units.ParsePosition(Attr(k)) is { } p) yield return p;
    }
}

public sealed record ModelBranch(string Name, IReadOnlyDictionary<string, string> Attrs, IReadOnlyList<ModelItem> Items)
{
    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : "";
    public (double E, double N, double U)? Head => Units.ParsePosition(Attr("HPOS"));
    public (double E, double N, double U)? Tail => Units.ParsePosition(Attr("TPOS"));
    public string ShortName => Name.Contains('/') ? Name[(Name.LastIndexOf('/') + 1)..] : Name;
}

public sealed record ModelLine(string Name, IReadOnlyDictionary<string, string> Attrs, IReadOnlyList<ModelBranch> Branches, string Path)
{
    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : "";

    public IEnumerable<ModelItem> Items => Branches.SelectMany(b => b.Items);

    public IEnumerable<(double E, double N, double U)> EndPoints()
    {
        foreach (var b in Branches)
        {
            if (b.Head is { } h) yield return h;
            if (b.Tail is { } t) yield return t;
        }
    }
}

/// <summary>A pipe that occurs more than once in the Navisworks tree. SameFile: the copies are in one appended model file (a real duplicate); otherwise they are in different appended files (normal in a federated model).</summary>
public sealed record ModelDuplicate(int Count, bool Identical, bool SameFile, IReadOnlyList<string> Paths);

/// <summary>
/// What the Navisworks export wrote: every PIPE with its BRAN and components and their AVEVA attributes. The product reads this file, never the model itself, so the
/// export is done once and shared. The model is in mm, Pa and degC.
/// </summary>
public sealed partial class ModelSnapshot
{
    public string Exported { get; private set; } = "";
    public string Source { get; private set; } = "";
    public int TotalItems { get; private set; }
    public Dictionary<string, ModelLine> Lines { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ModelDuplicate> Duplicates { get; } = new(StringComparer.Ordinal);
    public List<(string Name, (double E, double N, double U)? Pos)> Nozzles { get; } = [];
    private List<(string Line, (double E, double N, double U) P)>? _allEnds;

    public static string NormLine(string? name) => (name ?? "").Trim().TrimStart('/').Replace('”', '"').ToUpperInvariant();

    public static ModelSnapshot Load(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));

    public static ModelSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var snap = new ModelSnapshot
        {
            Exported = Str(root, "exported"), Source = Str(root, "source"),
            TotalItems = root.TryGetProperty("totalItems", out var ti) && ti.TryGetInt32(out var n) ? n : 0,
        };
        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            foreach (var ln in lines.EnumerateArray()) snap.AddLine(ln);
        if (root.TryGetProperty("nozzles", out var nz) && nz.ValueKind == JsonValueKind.Array)
            foreach (var z in nz.EnumerateArray())
                snap.Nozzles.Add((Str(z, "name"), Units.ParsePosition(Attrs(z).GetValueOrDefault("POS"))));
        return snap;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static Dictionary<string, string> Attrs(JsonElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        return d;
    }

    private static string SourceFile(string path) { var parts = path.Split(" > "); return parts.Length > 1 ? parts[1] : path; }

    private static string Flat(IReadOnlyDictionary<string, string> a) => string.Join("|", a.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));

    private static string Signature(ModelLine l) =>
        Flat(l.Attrs) + "##" + string.Join(";", l.Branches.Select(b => b.Name + "{" + Flat(b.Attrs) + "}" + string.Join(",", b.Items.Select(i => i.Name + ":" + i.Type + ":" + Flat(i.Attrs)))));

    private void AddLine(JsonElement ln)
    {
        var branches = new List<ModelBranch>();
        if (ln.TryGetProperty("branches", out var bs) && bs.ValueKind == JsonValueKind.Array)
            foreach (var b in bs.EnumerateArray())
            {
                var items = new List<ModelItem>();
                if (b.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                    foreach (var i in its.EnumerateArray()) items.Add(new ModelItem(Str(i, "name"), Str(i, "type"), Attrs(i)));
                branches.Add(new ModelBranch(Str(b, "name"), Attrs(b), items));
            }
        var line = new ModelLine(NormLine(Str(ln, "name")), Attrs(ln), branches, Str(ln, "path"));
        if (Lines.TryGetValue(line.Name, out var first))
        {
            Duplicates.TryGetValue(line.Name, out var d);
            var paths = d?.Paths.ToList() ?? [first.Path];
            paths.Add(line.Path);
            var files = paths.Select(SourceFile).ToList();
            var identical = (d?.Identical ?? true) && Signature(first) == Signature(line);
            Duplicates[line.Name] = new ModelDuplicate(paths.Count, identical, files.Distinct().Count() < files.Count, paths);
            return; // the first record is kept
        }
        Lines[line.Name] = line;
    }

    public ModelLine? Find(string? name) => Lines.GetValueOrDefault(NormLine(name));

    /// <summary>Every branch head and tail in the whole model, plus the nozzles: used to say which other line a coordinate belongs to.</summary>
    public IReadOnlyList<(string Line, (double E, double N, double U) P)> AllEndPoints()
    {
        if (_allEnds is null)
        {
            var l = new List<(string, (double, double, double))>();
            foreach (var ln in Lines.Values) foreach (var p in ln.EndPoints()) l.Add((ln.Name, p));
            foreach (var (n, p) in Nozzles) if (p is { } q) l.Add((n, q));
            _allEnds = l;
        }
        return _allEnds;
    }

    /// <summary>Straight pipe between the fittings, along every branch, from each item's arrive and leave points (mm).</summary>
    public static double PipeLengthMm(ModelLine line)
    {
        var total = 0.0;
        foreach (var b in line.Branches)
        {
            var cur = b.Head;
            foreach (var it in b.Items)
            {
                if (it.Type is "ATTA" or "WELD") continue; // no length, does not move the cursor
                var arrive = Units.ParsePosition(it.Attr("APOS")) ?? Units.ParsePosition(it.Attr("POS"));
                var leave = Units.ParsePosition(it.Attr("LPOS")) ?? Units.ParsePosition(it.Attr("POS"));
                if (cur is { } c && arrive is { } a) total += Units.Dist(c, a);
                if (leave is { } l) cur = l;
            }
            if (cur is { } c2 && b.Tail is { } t) total += Units.Dist(c2, t);
        }
        return total;
    }

    // a size token at the end of a catalogue spec, e.g. /B400A/EL90_BW-50, /B400A/TEE-50-25
    [GeneratedRegex(@"[-_](\d+(?:-\d+)*)$")]
    private static partial Regex SpecSize();

    /// <summary>The NPS sizes encoded in a spec reference, or null when the suffix is a catalogue index (CAP-7, BEND-2).</summary>
    public static string[]? SpecSizes(string? spec)
    {
        var m = SpecSize().Match(spec ?? "");
        if (!m.Success) return null;
        var toks = m.Groups[1].Value.Split('-');
        return toks.All(Units.DnToNps.ContainsKey) ? toks.Select(t => Units.DnToNps[t]).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray() : null;
    }
}

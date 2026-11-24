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

/// <summary>
/// A piece of equipment (AVEVA EQUI) with its extent. <see cref="BboxMm"/> is the Navisworks bounding box in the same world coordinates as the pipes (min E, N, U, max E, N, U): it is
/// reliable. The AVEVA POS attribute is only used as the origin when it lies inside that box (for some items it is a local or meaningless value).
/// </summary>
public sealed record ModelEquipment(string Name, string Tag, IReadOnlyDictionary<string, string> Attrs, string Path, double[]? BboxMm)
{
    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : "";
    public bool HasBox => BboxMm is { Length: 6 };
    public (double E, double N, double U)? Center => HasBox ? ((BboxMm![0] + BboxMm[3]) / 2, (BboxMm[1] + BboxMm[4]) / 2, (BboxMm[2] + BboxMm[5]) / 2) : null;

    /// <summary>The AVEVA origin when it is inside the box (a tolerance of 1 m), else null.</summary>
    public (double E, double N, double U)? Origin
    {
        get
        {
            if (Units.ParsePosition(Attr("POS")) is not { } p || !HasBox) return null;
            var b = BboxMm!;
            return p.E >= b[0] - 1000 && p.E <= b[3] + 1000 && p.N >= b[1] - 1000 && p.N <= b[4] + 1000 && p.U >= b[2] - 1000 && p.U <= b[5] + 1000 ? p : null;
        }
    }

    /// <summary>The distance in mm from a point to the box (0 inside it).</summary>
    public double DistanceToBox((double E, double N, double U) p)
    {
        if (!HasBox) return double.PositiveInfinity;
        var b = BboxMm!;
        double D(double v, double lo, double hi) => v < lo ? lo - v : v > hi ? v - hi : 0;
        var dx = D(p.E, b[0], b[3]); var dy = D(p.N, b[1], b[4]); var dz = D(p.U, b[2], b[5]);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
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
    /// <summary>Equipment by name, first record kept. Empty for a snapshot exported before equipment was read: <see cref="HasEquipment"/> is false and the model must be exported again.</summary>
    public Dictionary<string, ModelEquipment> Equipment { get; } = new(StringComparer.Ordinal);
    public bool HasEquipment { get; private set; }
    /// <summary>How many items of each AVEVA type the model has (from the export).</summary>
    public Dictionary<string, int> TypeCounts { get; } = new(StringComparer.Ordinal);
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
        if (root.TryGetProperty("equipment", out var eqs) && eqs.ValueKind == JsonValueKind.Array)
        {
            snap.HasEquipment = true;
            foreach (var e in eqs.EnumerateArray())
            {
                var name = Str(e, "name");
                if (name.Length == 0 || snap.Equipment.ContainsKey(name)) continue;
                double[]? box = null;
                if (e.TryGetProperty("bboxMm", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() == 6) box = bb.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                snap.Equipment[name] = new ModelEquipment(name, TagOf(name), Attrs(e), Str(e, "path"), box);
            }
        }
        if (root.TryGetProperty("typeCounts", out var tc) && tc.ValueKind == JsonValueKind.Object)
            foreach (var p in tc.EnumerateObject()) if (p.Value.TryGetInt32(out var n2)) snap.TypeCounts[p.Name] = n2;
        return snap;
    }

    /// <summary>The comparison key of a tag: upper case without hyphens, underscores, slashes and spaces (60-D-302 and 60D302 are the same tag).</summary>
    public static string TagKey(string? tag) => Regex.Replace((tag ?? "").ToUpperInvariant(), @"[\s\-_/]", "");

    /// <summary>The tag part of an equipment name: "/60D302_CONDENSATE_POT" gives "60D302".</summary>
    public static string TagOf(string name)
    {
        var n = name.TrimStart('/');
        var us = n.IndexOf('_');
        return us > 0 ? n[..us] : n;
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

    /// <summary>Equipment by tag (any spelling of it), or by the leading plant code of a longer name such as 60D301A-ADSORBER-A.</summary>
    public IReadOnlyList<ModelEquipment> FindEquipment(string? tag)
    {
        var k = TagKey(tag);
        if (k.Length == 0) return [];
        var exact = Equipment.Values.Where(e => TagKey(e.Tag) == k).ToList();
        return exact.Count > 0 ? exact : Equipment.Values.Where(e => TagKey(e.Tag).StartsWith(k, StringComparison.Ordinal) && TagKey(e.Tag).Length <= k.Length + 1).ToList();
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

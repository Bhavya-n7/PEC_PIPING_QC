using System.Text.RegularExpressions;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Core.Iso;

/// <summary>What the project's own documents can say about a line: the line list, and a search over the PMS and P&amp;IDs. Everything is optional; what is missing is reported.</summary>
public sealed record IsoEvidence(LineListSet? LineList = null, InputsIndex? Index = null, IReadOnlyList<InputDocument>? Inventory = null);

public sealed record IsoLineResult(string LineNo, string File, int Sheets, IReadOnlyList<Finding> Findings)
{
    public FindingStatus Worst => Findings.Count == 0 ? FindingStatus.Ok : Findings.Max(f => f.Status == FindingStatus.Info ? FindingStatus.Ok : f.Status);
    public string Verdict => QcReport.Verdict(Findings);
}

/// <summary>
/// The isometric check: sheets of one line against the Navisworks model snapshot and the project inputs. Every FAIL carries the value on the drawing, the value in the
/// source and where that came from; what cannot be decided is a REVIEW; what cannot be verified at all is an INFO. Nothing is reported as OK that was not compared.
/// </summary>
public sealed class IsoChecker(ModelSnapshot snap, string modelName, IsoRules rules, IsoEvidence? evidence = null)
{
    private readonly string _modelDoc = "Navisworks model " + modelName;
    private string _isoFile = "";
    private string _lineNo = "";

    // ----------------------------------------------------------------------------- helpers
    private Finding F(string check, FindingStatus st, string msg, string drawing = "", string model = "", int sheet = 0, string where = "", SourceRef? src = null)
    {
        var source = src ?? (model.Length > 0 ? new SourceRef(_modelDoc, where, "/" + ModelSnapshot.NormLine(_lineNo)) : null);
        return new Finding(check, st, _lineNo, msg, drawing, model, source, sheet > 0 ? $"sheet {sheet}" : "");
    }

    /// <summary>A FAIL about the drawing alone (the sheets disagree with each other): the source is the drawing itself.</summary>
    private Finding Internal(string check, string msg, string drawing, string expected, int sheet = 0) =>
        F(check, FindingStatus.Fail, msg, drawing, expected, sheet, src: new SourceRef(_isoFile, sheet > 0 ? $"sheet {sheet}" : "all sheets"));

    private static string Norm(string? s) => Regex.Replace((s ?? "").Trim(), @"\s+", " ").ToUpperInvariant();
    private static bool Empty(string? s) => Norm(s) is "" or "NA" or "N/A" or "-" or "?" or "NONE";
    private static double? Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    // ----------------------------------------------------------------------------- driver
    /// <summary>One PDF may hold several lines: the sheets are grouped by the line number of the title block.</summary>
    public List<IsoLineResult> CheckPdf(IReadOnlyList<IsoSheet> sheets)
    {
        var results = new List<IsoLineResult>();
        var groups = new Dictionary<string, List<IsoSheet>>();
        foreach (var s in sheets)
        {
            if (s.LineNo.Length > 0)
            {
                var key = ModelSnapshot.NormLine(s.LineNo);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = [];
                g.Add(s);
            }
            else
            {
                _isoFile = s.File; _lineNo = "(unreadable)";
                var fs = s.Warnings.Select(w => F("parse", FindingStatus.Review, w, sheet: s.Page)).ToList();
                fs.Add(F("parse", FindingStatus.Review, "no line number found on the sheet: it cannot be checked", sheet: s.Page));
                results.Add(new IsoLineResult("(unreadable)", s.File, 1, fs));
            }
        }
        foreach (var g in groups.Values) results.Add(CheckLine(g));
        return results;
    }

    public IsoLineResult CheckLine(IReadOnlyList<IsoSheet> input)
    {
        var sheets = input.OrderBy(s => s.Sheet).ThenBy(s => s.Page).ToList();
        _isoFile = sheets[0].File; _lineNo = sheets[0].LineNo;
        var fs = new List<Finding>();
        foreach (var s in sheets) foreach (var w in s.Warnings) fs.Add(F("parse", FindingStatus.Review, w, sheet: s.Page));
        var line = snap.Find(_lineNo);
        fs.AddRange(Identity(line, sheets));
        if (line is not null)
        {
            fs.AddRange(TitleBlock(line, sheets));
            fs.AddRange(Bom(line, sheets));
            fs.AddRange(Coordinates(line, sheets));
            fs.AddRange(Lengths(line, sheets));
        }
        fs.AddRange(LineListCheck(sheets));
        fs.AddRange(PmsCheck(sheets));
        fs.AddRange(PidCheck(sheets));
        fs.AddRange(VendorCheck(sheets));
        return new IsoLineResult(_lineNo, _isoFile, sheets.Count, fs);
    }

    // ----------------------------------------------------------------------------- identity
    private List<Finding> Identity(ModelLine? line, List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        if (snap.Duplicates.TryGetValue(ModelSnapshot.NormLine(_lineNo), out var dup))
        {
            var files = string.Join(" | ", dup.Paths.Select(p => p.Split(" > ") is { Length: > 1 } a ? a[1] : p).Distinct());
            if (dup.SameFile)
                o.Add(F("identity", dup.Identical ? FindingStatus.Review : FindingStatus.Fail, $"the line occurs {dup.Count} times in the same model file ({(dup.Identical ? "identical copies" : "DIFFERENT versions")}); duplicated pipes can double-count material",
                    _lineNo, $"{dup.Count} copies: {files}", where: "model tree"));
            else if (!dup.Identical)
                o.Add(F("identity", FindingStatus.Review, "the line is in several appended model files and the copies differ", model: files, where: "model tree"));
            else o.Add(F("identity", FindingStatus.Info, "the line is also present in other appended model file(s), identical", model: files, where: "model tree"));
        }
        var names = sheets.Select(s => s.LineNo).Distinct().ToList();
        if (names.Count > 1) o.Add(Internal("identity", "the sheets carry different line numbers", string.Join(", ", names.OrderBy(x => x)), "one line number on every sheet"));
        if (line is null)
        {
            o.Add(F("identity", FindingStatus.Fail, "the line is not in the model", _lineNo, "not in the exported model", where: "all PIPE items"));
            return o;
        }
        o.Add(F("identity", FindingStatus.Ok, "the line exists in the model", _lineNo, "/" + line.Name));
        var total = sheets.Max(s => s.Sheets);
        var nums = sheets.Select(s => s.Sheet).Order().ToList();
        if (total > 0 && !nums.SequenceEqual(Enumerable.Range(1, total)))
            o.Add(Internal("identity", $"the sheet numbers do not cover 1..{total}", string.Join(",", nums), string.Join(",", Enumerable.Range(1, total))));
        else if (total > 0) o.Add(F("identity", FindingStatus.Ok, $"all {total} sheets are present", src: new SourceRef(_isoFile)));
        var revs = sheets.Select(s => s.Revision).Where(r => r.Length > 0).Distinct().ToList();
        if (revs.Count > 1) o.Add(Internal("identity", "the sheets have different revisions", string.Join(", ", revs.Order()), "the same revision on every sheet"));
        foreach (var s in sheets)
        {
            foreach (var l in s.Links)
                if (!(l.Drg >= 1 && l.Drg <= (total > 0 ? total : l.Drg)) || l.Drg == s.Sheet)
                    o.Add(F("identity", FindingStatus.Review, $"CONT. {l.Kind} DRG {l.Drg} does not point to another sheet of 1..{total}", sheet: s.Page));
            var kinds = s.Links.Select(l => l.Kind).ToHashSet();
            if (total > 0 && s.Sheet > 1 && !kinds.Contains("FROM")) o.Add(F("identity", FindingStatus.Review, "no 'CONT. FROM DRG' link on a continuation sheet", sheet: s.Page));
            if (total > 0 && s.Sheet < total && !kinds.Contains("ON")) o.Add(F("identity", FindingStatus.Review, "no 'CONT. ON DRG' link although more sheets follow", sheet: s.Page));
        }
        var m = Regex.Match(_lineNo, @"^(\d+(?:\s?\d/\d)?)""-([A-Z0-9]+)-([A-Z0-9]+)-([A-Z0-9]+)-");
        if (m.Success)
        {
            var cls = m.Groups[4].Value;
            var pspe = line.Attr("PSPE").TrimStart('/');
            if (pspe.Length > 0 && pspe != cls) o.Add(F("identity", FindingStatus.Fail, "the piping class in the line number differs from the model class (PSPE)", cls, pspe, where: "PIPE PSPE"));
            var bore = Units.ParseQuantity(line.Attr("BORE"));
            if (bore is { } b && Units.DnToNps.TryGetValue(((int)b.Value).ToString(), out var nps) && nps.Replace(" ", "") != m.Groups[1].Value.Replace(" ", ""))
                o.Add(F("identity", FindingStatus.Fail, "the line size in the line number differs from the model bore", m.Groups[1].Value, nps, where: "PIPE BORE"));
        }
        return o;
    }

    // ----------------------------------------------------------------------------- title block
    private static double? ToPsi(string v) => Units.ParseQuantity(v) is { } q ? (q.Unit.StartsWith("pa", StringComparison.OrdinalIgnoreCase) ? Units.PaToPsi(q.Value) : q.Value) : null;
    private static double? ToDegF(string v) => Units.ParseQuantity(v) is { } q ? (q.Unit.Equals("degC", StringComparison.OrdinalIgnoreCase) || q.Unit.Equals("c", StringComparison.OrdinalIgnoreCase) ? Units.CToF(q.Value) : q.Value) : null;

    // iso label -> (kind, [(level, attribute)])
    private static readonly (string Label, string Kind, (string Level, string Attr)[] Sources)[] ParamMap =
    [
        ("TEST PRESS. (psig)", "psi", [("BRAN", "TPRESS"), ("PIPE", "TPRESS")]),
        ("DESIGN PRESS. (psig)", "psi", [("BRAN", "PRES"), ("PIPE", "PRES")]),
        ("DESIGN TEMP. (deg F)", "degf", [("BRAN", "TEMP"), ("PIPE", "TEMP")]),
        ("OPER PRESS. (psig)", "psi", [("PIPE", ":OPEPRESSURE")]),
        ("OPER TEMP. (deg F)", "str", [("PIPE", ":OPETEMPERATURE")]),
        ("TEST MEDIUM", "str", [("PIPE", ":TESTINGMEDIUM")]),
        ("PAINT SYSTEM", "str", [("PIPE", ":PAINTSYSTEM")]),
        ("INSU. TYPE", "str", [("PIPE", ":INSUTYPE")]),
        ("INSU. THK", "str", [("PIPE", ":INSUTHK")]),
        ("PHASE", "str", [("PIPE", ":PHASE")]),
        ("P&ID NO", "str", [("PIPE", ":P&ID-NO")]),
        ("STRESS CRITICALITY", "str", [("PIPE", ":STRESSCRITICALITY")]),
        ("CALC NO", "str", [("PIPE", ":CALC-NO")]),
        ("PIPNG CLASS", "str", [("PIPE", "PSPE")]),
        ("PWHT", "str", [("PIPE", ":PWHT")]),
    ];

    private List<Finding> TitleBlock(ModelLine line, List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        foreach (var (label, _, _) in ParamMap)
        {
            var vals = sheets.Where(s => s.Params.ContainsKey(label)).Select(s => s.Params[label]).Distinct().ToList();
            if (vals.Count > 1) o.Add(Internal("title-block", $"{label} differs between sheets", string.Join(" / ", vals.Order()), "the same value on every sheet"));
        }
        var ps = sheets.Select(s => s.Params).FirstOrDefault(p => p.Count > 0);
        if (ps is null) { o.Add(F("title-block", FindingStatus.Review, "no title-block parameters could be read")); return o; }
        foreach (var (label, kind, sources) in ParamMap)
        {
            if (!ps.TryGetValue(label, out var isoV)) continue;
            var mvals = new List<(string Where, string Attr, string Value)>();
            foreach (var (level, attr) in sources)
            {
                if (level == "PIPE") { if (line.Attr(attr).Length > 0) mvals.Add(("PIPE", attr, line.Attr(attr))); }
                else foreach (var b in line.Branches) if (b.Attr(attr).Length > 0) mvals.Add((b.ShortName, attr, b.Attr(attr)));
            }
            if (mvals.Count == 0)
            {
                o.Add(Empty(isoV) ? F("title-block", FindingStatus.Ok, $"{label}: empty in both", isoV, src: new SourceRef(_modelDoc))
                    : F("title-block", FindingStatus.Review, $"{label}: the value is on the drawing but not set in the model", isoV, "(blank)", where: "PIPE " + sources[^1].Attr));
                continue;
            }
            var good = new List<string>(); var bad = new List<string>();
            foreach (var (where, attr, mv) in mvals)
            {
                bool ok; string shown;
                if (kind == "psi" && Num(isoV) is { } ip && ToPsi(mv) is { } mp) { ok = Math.Abs(mp - ip) <= rules.PressureTolPsi; shown = $"{mp:0.0} psig"; }
                else if (kind == "degf" && Num(isoV) is { } it && ToDegF(mv) is { } mt) { ok = Math.Abs(mt - it) <= rules.TempTolF; shown = $"{mt:0.0} F"; }
                else { ok = Norm(isoV).TrimStart('/') == Norm(mv).TrimStart('/') || (Empty(isoV) && Empty(mv)); shown = mv; }
                (ok ? good : bad).Add($"{where}={shown}");
            }
            var where0 = $"{mvals[0].Where} {mvals[0].Attr}";
            if (bad.Count == 0) o.Add(F("title-block", FindingStatus.Ok, $"{label} matches", isoV, string.Join("; ", good), where: where0));
            else if (good.Count > 0) o.Add(F("title-block", FindingStatus.Review, $"{label}: the drawing agrees with some model values but not all (the model is inconsistent)", isoV, string.Join("; ", good.Concat(bad)), where: where0));
            else o.Add(F("title-block", FindingStatus.Fail, $"{label} differs from the model", isoV, string.Join("; ", bad), where: where0));
        }
        return o;
    }

    // ----------------------------------------------------------------------------- BOM
    internal static (string Cat, string Var) IsoCategory(BomRow r)
    {
        var d = r.Description.ToLowerInvariant();
        if (r.Category == "PIPE" || d.StartsWith("seamless, pipe") || d.StartsWith("pipe")) return ("pipe", "");
        if (r.Category == "SUPPORTS") { var m = Regex.Match(r.Description, @"^([A-Z]{2}\d{2})\b"); return ("support", m.Success ? m.Groups[1].Value : r.Description[..Math.Min(20, r.Description.Length)]); }
        if (d.Contains("elbow")) { var m = Regex.Match(d, @"(\d+)\s*deg"); return ("elbow", m.Success ? m.Groups[1].Value : ""); }
        if (d.Contains("blind flange") || d.Contains("spectacle")) return ("blind", "");
        if (d.Contains("flange")) return ("flange", "");
        foreach (var (key, cat) in new[] { ("tee", "tee"), ("reducer", "reducer"), ("coupling", "coupling"), ("gasket", "gasket"), ("stud", "bolts"), ("bolt", "bolts"), ("valve", "valve") })
            if (Regex.IsMatch(d, $@"\b{key}\b")) return (cat, "");
        if (Regex.IsMatch(d, @"\bcap\b")) return ("cap", "");
        if (Regex.IsMatch(d, @"(weld|sock|thread|nip)olet|\bolet\b")) return ("olet", "");
        return ("other", r.Description[..Math.Min(30, r.Description.Length)]);
    }

    private static readonly Dictionary<string, string> ModelType = new()
        { ["FLAN"] = "flange", ["FBLI"] = "blind", ["TEE"] = "tee", ["REDU"] = "reducer", ["CAP"] = "cap", ["COUP"] = "coupling", ["OLET"] = "olet", ["VALV"] = "valve", ["BEND"] = "bend" };

    private (string Cat, string Var, string[]? Sizes)? ModelCategory(ModelItem it)
    {
        var t = it.Type;
        if (t == "WELD") return null;
        if (t == "ATTA")
        {
            if (rules.IsIgnoredAtta(it.Spec)) return null;
            return rules.SupportMap.TryGetValue(it.Spec, out var code) ? ("support", code, null) : ("support?", it.Spec, null);
        }
        if (t == "ELBO") return ("elbow", it.Spec.Contains("EL90", StringComparison.OrdinalIgnoreCase) ? "90" : it.Spec.Contains("EL45", StringComparison.OrdinalIgnoreCase) ? "45" : "", ModelSnapshot.SpecSizes(it.Spec));
        if (ModelType.TryGetValue(t, out var c)) return (c, "", ModelSnapshot.SpecSizes(it.Spec));
        return ($"other:{t}", it.Spec, null);
    }

    private static string SizeKey(IEnumerable<string> s) => string.Join("x", s);
    private static string FmtSizes(Dictionary<string, int> c) => string.Join(", ", c.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}:{k.Value}"));

    private List<Finding> Bom(ModelLine line, List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        var isoCnt = new Dictionary<(string, string), int>();
        var isoSize = new Dictionary<(string, string), Dictionary<string, int>>();
        foreach (var s in sheets)
            foreach (var r in s.Bom)
            {
                var (cat, v) = IsoCategory(r);
                if (r.Qty is null) continue;
                if (cat == "pipe")
                {
                    var bore = Units.ParseQuantity(line.Attr("BORE"));
                    if (bore is { } b && Units.DnToNps.TryGetValue(((int)b.Value).ToString(), out var nps) && !Units.NpsFromText(r.Size).SequenceEqual(Units.NpsFromText(nps)))
                        o.Add(F("bom", FindingStatus.Fail, "the pipe size on the BOM differs from the line bore", r.Size, nps, s.Page, "PIPE BORE"));
                    continue;
                }
                if (cat == "other") { o.Add(F("bom", FindingStatus.Review, "BOM item not recognised: check it by hand", $"#{r.PtNo} {r.Description[..Math.Min(50, r.Description.Length)]}", sheet: s.Page)); continue; }
                var key = (cat, v);
                isoCnt[key] = isoCnt.GetValueOrDefault(key) + (int)r.Qty.Value;
                if (!isoSize.TryGetValue(key, out var sz)) isoSize[key] = sz = [];
                var sk = SizeKey(Units.NpsFromText(r.Size));
                sz[sk] = sz.GetValueOrDefault(sk) + (int)r.Qty.Value;
            }
        var modCnt = new Dictionary<(string, string), int>();
        var modSize = new Dictionary<(string, string), Dictionary<string, int>>();
        var sizeKnown = new Dictionary<(string, string), bool>();
        var ignored = 0;
        foreach (var it in line.Items)
        {
            if (it.Type == "ATTA" && rules.IsIgnoredAtta(it.Spec)) ignored++;
            if (ModelCategory(it) is not { } mc) continue;
            var key = (mc.Cat, mc.Var);
            modCnt[key] = modCnt.GetValueOrDefault(key) + 1;
            if (!sizeKnown.ContainsKey(key)) sizeKnown[key] = true;
            if (mc.Sizes is null) sizeKnown[key] = false;
            else
            {
                if (!modSize.TryGetValue(key, out var sz)) modSize[key] = sz = [];
                var sk = SizeKey(mc.Sizes);
                sz[sk] = sz.GetValueOrDefault(sk) + 1;
            }
        }
        if (ignored > 0) o.Add(F("bom", FindingStatus.Info, $"{ignored} class-spec attachment(s) in the model (flow arrows, dummy legs, points) are not counted as supports", model: ignored.ToString(), where: "ATTA items"));
        // gaskets and bolts are erection materials derived by the iso tool and not modelled
        foreach (var (key, n) in isoCnt.OrderBy(k => k.Key))
            if (key.Item1 is "gasket" or "bolts") o.Add(F("bom", FindingStatus.Info, $"{key.Item1}: {n} on the drawing, not modelled, so not verifiable", n.ToString()));
        // Supports: which model attachment is which iso support code comes from the client's support collection (the support map). While the model has supports whose spec
        // is not in the map, the codes cannot be compared one by one: only the TOTAL number of supports is, and a person is told which specs to add to the map.
        var unmapped = modCnt.Where(k => k.Key.Item1 == "support?").ToList();
        if (unmapped.Count > 0)
        {
            var isoTotal = isoCnt.Where(k => k.Key.Item1 == "support").Sum(k => k.Value);
            var modTotal = modCnt.Where(k => k.Key.Item1 is "support" or "support?").Sum(k => k.Value);
            var specs = string.Join(", ", unmapped.OrderBy(k => k.Key.Item2, StringComparer.Ordinal).Select(k => $"{k.Key.Item2} x{k.Value}"));
            o.Add(F("bom", FindingStatus.Review, "which support is which could not be checked: these model support specs are not in the support map (add them from the client's support collection: iso-rules.json)", string.Join(", ", isoCnt.Where(k => k.Key.Item1 == "support").Select(k => $"{k.Key.Item2} x{k.Value}")), specs, where: "ATTA SPRE"));
            o.Add(isoTotal == modTotal
                ? F("bom", FindingStatus.Ok, "the total number of supports matches", isoTotal.ToString(), modTotal.ToString(), where: "ATTA items that are supports")
                : F("bom", FindingStatus.Fail, "the total number of supports differs", isoTotal.ToString(), modTotal.ToString(), where: "ATTA items that are supports"));
        }
        foreach (var key in isoCnt.Keys.Union(modCnt.Keys).OrderBy(k => k))
        {
            var (cat, v) = key;
            if (cat is "gasket" or "bolts") continue;
            var label = $"{cat} {v}".Trim();
            if (cat == "support?" || (cat == "support" && unmapped.Count > 0)) continue;
            if (cat.StartsWith("other:", StringComparison.Ordinal)) { o.Add(F("bom", FindingStatus.Review, $"the model has {modCnt[key]} item(s) of type {cat[6..]} with no BOM rule", model: v)); continue; }
            var iN = isoCnt.GetValueOrDefault(key); var mN = modCnt.GetValueOrDefault(key);
            if (iN != mN) { o.Add(F("bom", FindingStatus.Fail, $"{label}: the quantity differs", iN.ToString(), mN.ToString(), where: "components of the line")); continue; }
            o.Add(F("bom", FindingStatus.Ok, $"{label}: the quantity matches", iN.ToString(), mN.ToString(), where: "components of the line"));
            if (sizeKnown.GetValueOrDefault(key) && modSize.TryGetValue(key, out var ms) && ms.Count > 0 && isoSize.TryGetValue(key, out var isz) && isz.Count > 0
                && !(isz.Count == ms.Count && isz.All(k => ms.TryGetValue(k.Key, out var n) && n == k.Value)))
                o.Add(F("bom", FindingStatus.Fail, $"{label}: the sizes differ", FmtSizes(isz), FmtSizes(ms), where: "component spec references"));
        }
        return o;
    }

    // ----------------------------------------------------------------------------- coordinates
    private static IEnumerable<(string Label, (double E, double N, double U) P)> LabelledPoints(ModelLine line)
    {
        foreach (var b in line.Branches)
        {
            if (b.Head is { } h) yield return ($"{b.ShortName} head", h);
            if (b.Tail is { } t) yield return ($"{b.ShortName} tail", t);
            foreach (var it in b.Items) foreach (var p in it.Points()) yield return (it.Name.Contains(" of ") ? it.Name.Split(" of ")[0] : it.Name, p);
        }
    }

    private static (double D, string Label)? Nearest((double E, double N, double U) pt, IEnumerable<(string Label, (double E, double N, double U) P)> pts)
    {
        (double, string)? best = null;
        foreach (var (lab, p) in pts) { var d = Units.Dist(pt, p); if (best is null || d < best.Value.Item1) best = (d, lab); }
        return best;
    }

    private static string Txt(CoordLabel c) => $"E {Units.FormatInches(c.E)} N {Units.FormatInches(c.N)} U {Units.FormatInches(c.U)}";

    private List<Finding> Coordinates(ModelLine line, List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        var tol = rules.CoordTolMm;
        var own = LabelledPoints(line).ToList();
        foreach (var s in sheets)
            foreach (var c in s.Coords)
            {
                var pt = (Units.InToMm(c.E), Units.InToMm(c.N), Units.InToMm(c.U));
                var txt = Txt(c);
                if (c.ContLine.Length > 0)
                {
                    var other = snap.Find(c.ContLine);
                    if (other is null) { o.Add(F("coordinates", FindingStatus.Fail, $"the CONT. {c.Cont} line is not in the model", c.ContLine, "not in the exported model", s.Page, "all PIPE items")); continue; }
                    var n = Nearest(pt, LabelledPoints(other));
                    if (n is { } nn && nn.D <= tol) o.Add(F("coordinates", FindingStatus.Ok, $"the connection to {c.ContLine} matches ({nn.Label})", txt, sheet: s.Page, src: new SourceRef(_modelDoc, "/" + other.Name)));
                    else o.Add(F("coordinates", FindingStatus.Fail, $"the coordinate does not match any point of {c.ContLine}", txt, n is { } n2 ? $"nearest {n2.Label} at {n2.D:0.0} mm" : "no point", s.Page, "/" + other.Name));
                    continue;
                }
                var near = Nearest(pt, own);
                if (near is { } a && a.D <= tol) { o.Add(F("coordinates", FindingStatus.Ok, $"matches a model point ({a.Label})", txt, sheet: s.Page, src: new SourceRef(_modelDoc, "/" + line.Name))); continue; }
                // close to this line's own start or end but outside the tolerance: almost certainly a wrong label
                var en = Nearest(pt, own.Where(p => p.Label.EndsWith(" head") || p.Label.EndsWith(" tail")));
                if (en is { } e && e.D <= rules.EndpointNearMm)
                {
                    o.Add(F("coordinates", FindingStatus.Fail, $"the coordinate is {e.D:0.0} mm from the model {e.Label} point (tolerance {tol} mm)", txt, e.Label, s.Page, "/" + line.Name));
                    continue;
                }
                var anyN = snap.AllEndPoints().Select(x => (D: Units.Dist(pt, x.P), x.Line)).OrderBy(x => x.D).Cast<(double D, string Line)?>().FirstOrDefault();
                if (anyN is { } an && an.D <= tol) o.Add(F("coordinates", FindingStatus.Review, $"the point is not on this line but matches an end point of {an.Line}", txt, sheet: s.Page));
                else o.Add(F("coordinates", FindingStatus.Review, "no model point within the tolerance (a tee branch end, or an error)", txt, near is { } n3 ? $"nearest {n3.Label} at {n3.D:0.0} mm" : "", s.Page, "/" + line.Name));
            }
        var shown = sheets.SelectMany(s => s.Coords).Select(c => (Units.InToMm(c.E), Units.InToMm(c.N), Units.InToMm(c.U))).ToList();
        foreach (var (lab, p) in own.Where(p => p.Label.EndsWith(" head") || p.Label.EndsWith(" tail")))
            if (!shown.Any(q => Units.Dist(p, q) <= tol))
                o.Add(F("coordinates", FindingStatus.Review, $"the model {lab} point has no coordinate label on the drawing", model: $"E {Units.FormatInches(Units.MmToIn(p.E))} N {Units.FormatInches(Units.MmToIn(p.N))} U {Units.FormatInches(Units.MmToIn(p.U))}", where: lab));
        return o;
    }

    // ----------------------------------------------------------------------------- lengths
    private List<Finding> Lengths(ModelLine line, List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        var pipeTotal = 0.0;
        foreach (var s in sheets)
        {
            var pipe = s.Bom.Where(r => IsoCategory(r).Cat == "pipe" && r.Qty is not null).ToList();
            var q = pipe.Sum(r => r.Qty!.Value);
            pipeTotal += q;
            if (s.Cuts.Count > 0 && pipe.Count > 0)
            {
                var cuts = s.Cuts.Sum(c => c.LengthIn ?? 0);
                var tol = rules.CutSumTolIn * Math.Max(1, s.Cuts.Count);
                if (Math.Abs(cuts - q) <= tol) o.Add(F("lengths", FindingStatus.Ok, "the sum of the cut pipe lengths equals the BOM pipe quantity", Units.FormatFeetInches(q), sheet: s.Page, src: new SourceRef(_isoFile, $"sheet {s.Page}")));
                else o.Add(F("lengths", FindingStatus.Fail, "the sum of the cut pipe lengths differs from the BOM pipe quantity", $"cuts {Units.FormatFeetInches(cuts)}", $"BOM {Units.FormatFeetInches(q)}", s.Page,
                    src: new SourceRef(_isoFile, $"sheet {s.Page}, BOM")));
            }
            else if (pipe.Count > 0) o.Add(F("lengths", FindingStatus.Review, "there is pipe on the BOM but no cut pipe table could be read", sheet: s.Page));
        }
        var mlen = Units.MmToIn(ModelSnapshot.PipeLengthMm(line));
        if (pipeTotal > 0)
        {
            var diff = pipeTotal - mlen;
            var d = Units.FormatFeetInches(pipeTotal); var m = Units.FormatFeetInches(mlen);
            if (Math.Abs(diff) <= rules.PipeLengthTolIn) o.Add(F("lengths", FindingStatus.Ok, $"the total pipe length matches the model (difference {diff:+0.00;-0.00} in)", d, m, where: "pipe between fittings, from arrive / leave points"));
            else o.Add(F("lengths", FindingStatus.Fail, $"the total pipe length differs from the model by {diff:+0.00;-0.00} in", d, m, where: "pipe between fittings, from arrive / leave points"));
        }
        return o;
    }

    // ----------------------------------------------------------------------------- line list
    private static double? ListPsi(LlValue v)
    {
        if (v.Number is not { } n) return null;
        return (v.Unit ?? "").ToLowerInvariant().Replace(" ", "") switch
        {
            "psig" or "psi" or "psia" or "psi(g)" => n, "barg" or "bar" or "barg." or "bar(g)" => n * 14.503773773, "kpa" or "kpag" or "kpa(g)" => n * 0.145037738,
            "mpa" or "mpag" => n * 145.037738, "pa" => Units.PaToPsi(n), "kg/cm2" or "kg/cm2g" or "kgf/cm2" => n * 14.2233, _ => null,
        };
    }

    private static double? ListDegF(LlValue v)
    {
        if (v.Number is not { } n) return null;
        return (v.Unit ?? "").ToLowerInvariant().Replace(" ", "").Replace("deg", "").Replace("Â°", "") switch { "f" => n, "c" => Units.CToF(n), "k" => Units.CToF(n - 273.15), _ => null };
    }

    private List<Finding> LineListCheck(List<IsoSheet> sheets)
    {
        var o = new List<Finding>();
        var ll = evidence?.LineList;
        if (ll is null || ll.Entries.Count == 0) { o.Add(F("line-list", FindingStatus.Info, "no line list was available: the title block was not compared with it", src: null)); return o; }
        var key = LineListSet.Normalize(_lineNo);
        var found = ll.Find(_lineNo).Where(e => e.Key == key).ToList();
        if (found.Count == 0) { o.Add(F("line-list", FindingStatus.Review, "the line is not in the line list (or is spelled differently there)", _lineNo, "(not found)", src: new SourceRef("line list"))); return o; }
        var e = found[0];
        var src = new SourceRef(e.File, $"sheet {e.Sheet}, row {e.Row}", e.LineNo);
        if (found.Count > 1) o.Add(F("line-list", FindingStatus.Review, $"the line is in the line list {found.Count} times; the first entry was used", model: string.Join("; ", found.Select(x => $"{x.File} {x.Sheet} row {x.Row}")), src: src));
        var ps = sheets.Select(s => s.Params).FirstOrDefault(p => p.Count > 0) ?? [];

        void Text(string label, string field)
        {
            if (!ps.TryGetValue(label, out var iv) || !e.Fields.TryGetValue(field, out var lv)) return;
            if (Empty(iv) && Empty(lv.Text)) return;
            if (Norm(iv).TrimStart('/') == Norm(lv.Text).TrimStart('/')) o.Add(F("line-list", FindingStatus.Ok, $"{label} matches the line list", iv, lv.Text, src: src));
            else o.Add(F("line-list", FindingStatus.Fail, $"{label} differs from the line list", iv, lv.Text, src: src));
        }
        void Pressure(string label, string field)
        {
            if (!ps.TryGetValue(label, out var iv) || !e.Fields.TryGetValue(field, out var lv)) return;
            if (Num(iv) is not { } ip) return;
            if (ListPsi(lv) is not { } lp)
            {
                o.Add(F("line-list", FindingStatus.Review, $"{label}: the line list value '{lv.Text}' has no unit the check knows (psig, barg, kPa, MPa...): compare by hand", iv, lv.Text, src: src));
                return;
            }
            if (Math.Abs(lp - ip) <= Math.Max(rules.PressureTolPsi, Math.Abs(lp) * 0.005)) o.Add(F("line-list", FindingStatus.Ok, $"{label} matches the line list", iv, $"{lp:0.#} psig", src: src));
            else o.Add(F("line-list", FindingStatus.Fail, $"{label} differs from the line list", iv + " psig", $"{lp:0.#} psig ({lv.Text} {lv.Unit})".Trim(), src: src));
        }
        void Temp(string label, string field)
        {
            if (!ps.TryGetValue(label, out var iv) || !e.Fields.TryGetValue(field, out var lv)) return;
            if (Num(iv) is not { } it) return;
            if (ListDegF(lv) is not { } lt)
            {
                o.Add(F("line-list", FindingStatus.Review, $"{label}: the line list value '{lv.Text}' has no unit the check knows (degF, degC, K): compare by hand", iv, lv.Text, src: src));
                return;
            }
            if (Math.Abs(lt - it) <= Math.Max(rules.TempTolF, 0.5)) o.Add(F("line-list", FindingStatus.Ok, $"{label} matches the line list", iv, $"{lt:0.#} F", src: src));
            else o.Add(F("line-list", FindingStatus.Fail, $"{label} differs from the line list", iv + " F", $"{lt:0.#} F ({lv.Text} {lv.Unit})".Trim(), src: src));
        }
        Text("PIPNG CLASS", "Class"); Text("P&ID NO", "PidNo"); Text("INSU. TYPE", "Insulation"); Text("TEST MEDIUM", "TestMedium"); Text("PHASE", "Phase"); Text("PWHT", "Pwht");
        Pressure("DESIGN PRESS. (psig)", "DesignPressure"); Pressure("TEST PRESS. (psig)", "TestPressure"); Pressure("OPER PRESS. (psig)", "OperPressure");
        Temp("DESIGN TEMP. (deg F)", "DesignTemp");
        // the size in the line number against the line list size
        var m = Regex.Match(_lineNo, @"^(\d+(?:\s?\d/\d)?)""");
        if (m.Success && e.Fields.TryGetValue("Size", out var sv) && sv.Text.Length > 0)
        {
            var a = m.Groups[1].Value.Replace(" ", ""); var b = sv.Text.Replace("\"", "").Replace(" ", "");
            if (a != b && !(Num(a) is { } an && Num(b) is { } bn && Math.Abs(an - bn) < 0.001))
                o.Add(F("line-list", FindingStatus.Review, "the size in the line number differs from the line list size (check the unit: inch or mm)", a + "\"", sv.Text, src: src));
        }
        return o;
    }

    // ----------------------------------------------------------------------------- PMS, P&ID, vendor data
    private bool Has(InputKind k) => evidence?.Inventory?.Any(d => d.Kind == k) == true;

    private List<Finding> PmsCheck(List<IsoSheet> sheets)
    {
        var cls = sheets.Select(s => s.Params.GetValueOrDefault("PIPNG CLASS") ?? "").FirstOrDefault(c => c.Length > 0) ?? "";
        if (!Has(InputKind.Pms) || evidence?.Index is null) return [F("pms", FindingStatus.Info, "no PMS in the project inputs: the piping class, rating and material were not checked")];
        if (cls.Length == 0) return [F("pms", FindingStatus.Review, "the piping class could not be read from the title block, so the PMS was not consulted")];
        var hits = evidence.Index.Search(cls, InputKind.Pms, 5);
        var hit = hits.FirstOrDefault(h => h.Text.Contains(cls, StringComparison.OrdinalIgnoreCase));
        var o = new List<Finding>();
        o.Add(hit is not null
            ? F("pms", FindingStatus.Ok, $"class {cls} is in the PMS", cls, $"found in {hit.File}, {hit.Loc}", src: new SourceRef(hit.File, hit.Loc, cls))
            : F("pms", FindingStatus.Review, $"class {cls} was not found in the text of the PMS (a different spelling, a scan, or a class the PMS does not have)", cls, "(not found)", src: new SourceRef("PMS")));
        o.Add(F("pms", FindingStatus.Info, "rating, material and schedule against the PMS class sheet were not verified: the PMS tables are not read as data yet"));
        return o;
    }

    private List<Finding> PidCheck(List<IsoSheet> sheets)
    {
        var pidNo = sheets.Select(s => s.Params.GetValueOrDefault("P&ID NO") ?? "").FirstOrDefault(c => c.Length > 0) ?? "";
        if (!Has(InputKind.Pid) || evidence?.Index is null) return [F("pid", FindingStatus.Info, "no P&ID in the project inputs: the line, its P&ID sheet and its end points were not checked")];
        var o = new List<Finding>();
        var sheetFile = pidNo.Length == 0 ? null : evidence.Inventory!.FirstOrDefault(d => d.Kind == InputKind.Pid && d.RelPath.Contains(pidNo, StringComparison.OrdinalIgnoreCase));
        var key = LineListSet.Normalize(_lineNo);
        var hits = evidence.Index.Search(_lineNo, InputKind.Pid, 8).Where(h => LineListSet.Normalize(h.Text).Contains(key, StringComparison.Ordinal)).ToList();
        var onSheet = sheetFile is null ? null : hits.FirstOrDefault(h => h.File.Equals(sheetFile.RelPath, StringComparison.OrdinalIgnoreCase));
        if (onSheet is not null) o.Add(F("pid", FindingStatus.Ok, "the line is on the P&ID named in the title block", _lineNo, $"found in {onSheet.File}, {onSheet.Loc}", src: new SourceRef(onSheet.File, onSheet.Loc, _lineNo)));
        else if (hits.Count > 0) o.Add(F("pid", FindingStatus.Review, $"the line is on {hits[0].File} ({hits[0].Loc}) but the title block names P&ID {pidNo}", pidNo, hits[0].File, src: new SourceRef(hits[0].File, hits[0].Loc, _lineNo)));
        else if (sheetFile is null) o.Add(F("pid", FindingStatus.Review, pidNo.Length == 0 ? "the title block has no P&ID number and the line was not found in any P&ID text" : $"P&ID {pidNo} is not among the project inputs: the line could not be checked against it", pidNo));
        else o.Add(F("pid", FindingStatus.Review, $"the line number was not found in the text of P&ID {sheetFile.RelPath} (text search only: it may be drawn as graphics or spelled differently)", _lineNo, "(not found)", src: new SourceRef(sheetFile.RelPath)));
        o.Add(F("pid", FindingStatus.Info, "the from / to end points and the valves of the line were not compared with the P&ID (done by the P&ID check)"));
        return o;
    }

    private List<Finding> VendorCheck(List<IsoSheet> sheets)
    {
        var valves = sheets.SelectMany(s => s.Bom).Where(r => IsoCategory(r).Cat == "valve" || r.Category == "VALVES" || r.Category == "INSTRUMENTS").ToList();
        if (valves.Count == 0) return [];
        return [F("vendor", FindingStatus.Info, $"{valves.Count} valve / instrument item(s) on the BOM: face-to-face dimensions against vendor data were not verified")];
    }
}

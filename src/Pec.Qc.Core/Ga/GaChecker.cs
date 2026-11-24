using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;
using Pec.Qc.Core.Model;
using Pec.Qc.Core.Pid;

namespace Pec.Qc.Core.Ga;

public sealed record GaOptions
{
    /// <summary>A coordinate on the drawing may be this far outside the equipment's extent (mm) and still be on it.</summary>
    public double BoxToleranceMm { get; init; } = 100;
    /// <summary>A coordinate or elevation belongs to the tag nearest to it if that is within this many points (A3 frame).</summary>
    public double NearPt { get; init; } = 120;
}

/// <summary>
/// The GA check: equipment tags and coordinates against the Navisworks model, tags against the P&amp;IDs, vendor data presence, and the completeness of the drawing.
/// A tag that is one character away from a model tag, or a coordinate that is outside the equipment's extent, is a FAIL with both values; what cannot be decided is a
/// REVIEW. Dimensions, equipment sizes against vendor data, nozzles and clearances are NOT checked and are listed as not verified.
/// </summary>
public sealed class GaChecker(ModelSnapshot snap, string modelName, GaOptions? options = null, IsoEvidence? evidence = null)
{
    private readonly GaOptions _o = options ?? new GaOptions();
    private readonly string _modelDoc = "Navisworks model " + modelName;

    public IReadOnlyList<Finding> Check(IReadOnlyList<GaSheet> sheets)
    {
        var f = new List<Finding>();
        var file = sheets.Count > 0 ? sheets[0].File : "(drawing)";
        foreach (var s in sheets)
        {
            if (!s.HasText) { f.Add(new Finding("drawing", FindingStatus.Review, $"sheet {s.Page}", "the sheet has no text (a scan or an image): it cannot be checked")); continue; }
            foreach (var w in s.Warnings) f.Add(new Finding("parse", FindingStatus.Review, $"sheet {s.Page}", w));
            Tags(s, f);
            Positions(s, f);
        }
        Completeness(sheets.Where(x => x.HasText).ToList(), f);
        LineNumbers(sheets, f);
        f.Add(new Finding("dimensions", FindingStatus.Info, file, "dimensions on the drawing, equipment sizes against the model extents, nozzle positions and orientations against the vendor data were NOT checked"));
        f.Add(new Finding("clearances", FindingStatus.Info, file, "access and clearances are a judgement for the engineer: not checked"));
        return f;
    }

    private static string Edge(double[] b) => $"E {b[0]:0}..{b[3]:0}, N {b[1]:0}..{b[4]:0}, U {b[2]:0}..{b[5]:0} mm";
    private SourceRef ModelSrc(ModelEquipment e) => new(_modelDoc, e.Path.Split(" > ").LastOrDefault() ?? "", e.Name);

    // ----------------------------------------------------------------------------- GA-05 / GA-06
    private static void Completeness(IReadOnlyList<GaSheet> sheets, List<Finding> f)
    {
        if (sheets.Count == 0) return;
        var file = sheets[0].File;
        void Item(Func<GaSheet, bool> has, string what, string detail)
        {
            var missing = sheets.Where(s => !has(s)).Select(s => s.Page).ToList();
            f.Add(missing.Count == 0
                ? new Finding("drawing", FindingStatus.Ok, file, $"{what} found on every sheet", Source: new SourceRef(file, $"{sheets.Count} sheet(s)"))
                : new Finding("drawing", FindingStatus.Review, file, $"{what} was not found as text on sheet(s) {string.Join(", ", missing)}: {detail}"));
        }
        Item(s => s.Scale.Length > 0, "the scale", "look at the title block (the text 'SCALE 1:n' is expected)");
        Item(s => s.HasNorthArrow, "a north arrow", "an arrow drawn as a symbol is not read: look at the drawing");
        Item(s => s.Revision.Length > 0, "the revision", "look at the title block (the text 'REV x' is expected; a title block drawn as lines is not read)");
    }

    // ----------------------------------------------------------------------------- GA-01 / GA-04 / GA-03
    private void Tags(GaSheet s, List<Finding> f)
    {
        var subject = $"sheet {s.Page}";
        var seen = new HashSet<string>();
        var modelKeys = snap.Equipment.Values.Select(e => ModelSnapshot.TagKey(e.Tag)).Distinct().ToList();
        foreach (var t in s.Tags)
        {
            if (!seen.Add(t.Key)) continue;
            var eq = snap.FindEquipment(t.Text);
            if (!snap.HasEquipment)
            {
                if (seen.Count == 1) f.Add(new Finding("equipment", FindingStatus.Review, subject, "the exported model has no equipment (it was exported before equipment was read): export the model again with model_export to check tags and positions"));
            }
            else if (eq.Count > 0)
                f.Add(new Finding("equipment", FindingStatus.Ok, t.Text, $"the tag is equipment in the model ({eq[0].Name})", t.Text, eq[0].Name, ModelSrc(eq[0]), subject));
            else
            {
                var close = modelKeys.Select(k => (K: k, D: PidComparer.Edit(t.Key, k))).Where(x => x.D == 1 && x.K.Length >= 5).OrderBy(x => x.K).FirstOrDefault();
                if (close.K is not null)
                {
                    var near = snap.Equipment.Values.First(e => ModelSnapshot.TagKey(e.Tag) == close.K);
                    f.Add(new Finding("equipment", FindingStatus.Fail, t.Text, "the tag is not in the model but one character away from a model tag (a typo?)", t.Text, near.Tag, ModelSrc(near), subject));
                }
                else f.Add(new Finding("equipment", FindingStatus.Review, t.Text, "the tag is not equipment in the model (it may be a pipe, an instrument, a note or a tag the model names differently)", t.Text, "(not in the model)", new SourceRef(_modelDoc), subject));
            }
            if (evidence?.Index is { } idx && evidence.Inventory is { } inv)
            {
                if (inv.Any(d => d.Kind == InputKind.Pid)) Presence(idx, InputKind.Pid, "tags", "P&ID", t, subject, f);
                if (inv.Any(d => d.Kind == InputKind.Vendor)) Presence(idx, InputKind.Vendor, "vendor", "vendor data", t, subject, f);
            }
        }
        // a token that is not shaped like a tag but is one character from a model tag: a retyped tag (60D3O2 for 60D302)
        foreach (var t in s.Loose)
        {
            if (!seen.Add(t.Key) || !snap.HasEquipment || snap.FindEquipment(t.Text).Count > 0) continue;
            var close = modelKeys.Select(k => (K: k, D: PidComparer.Edit(t.Key, k))).Where(x => x.D == 1 && x.K.Length >= 5).OrderBy(x => x.K).FirstOrDefault();
            if (close.K is null) continue;
            var near = snap.Equipment.Values.First(e => ModelSnapshot.TagKey(e.Tag) == close.K);
            f.Add(new Finding("equipment", FindingStatus.Fail, t.Text, "the text is not a model tag but one character away from one (a typo?)", t.Text, near.Tag, ModelSrc(near), subject));
        }
        if (s.Tags.Count == 0 && s.Loose.Count == 0 && s.Lines.Count == 0) f.Add(new Finding("equipment", FindingStatus.Review, subject, "no equipment tag was read on this sheet (the tags may be in a form the reader does not know)"));
        if (evidence?.Inventory is not null && evidence.Inventory.All(d => d.Kind != InputKind.Pid)) f.Add(new Finding("tags", FindingStatus.Info, subject, "no P&ID in the project inputs: the tags and services were not checked against the P&IDs"));
        if (evidence?.Inventory is not null && evidence.Inventory.All(d => d.Kind != InputKind.Vendor)) f.Add(new Finding("vendor", FindingStatus.Info, subject, "no vendor data in the project inputs: sizes and nozzles were not checked"));
        if (evidence is null) f.Add(new Finding("tags", FindingStatus.Info, subject, "the project inputs were not available: P&ID and vendor data were not consulted"));
    }

    private static void Presence(InputsIndex idx, InputKind kind, string check, string noun, GaTag t, string subject, List<Finding> f)
    {
        var hit = idx.Search(t.Text, kind, 8).FirstOrDefault(h => ModelSnapshot.TagKey(h.Text).Contains(t.Key, StringComparison.Ordinal));
        f.Add(hit is not null
            ? new Finding(check, FindingStatus.Ok, t.Text, $"the tag is in the {noun} ({hit.File}, {hit.Loc})", t.Text, $"{hit.File}, {hit.Loc}", new SourceRef(hit.File, hit.Loc, t.Text), subject)
            : new Finding(check, FindingStatus.Review, t.Text, $"the tag was not found in the text of the {noun} (a scan, a different spelling, or it is not there)", t.Text, "(not found)", new SourceRef(noun), subject));
    }

    // ----------------------------------------------------------------------------- line numbers on a piping plan
    private static double? NpsValue(string nps)
    {
        var parts = nps.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = 0;
        foreach (var p in parts)
        {
            if (p.Contains('/')) { var fr = p.Split('/'); if (fr.Length == 2 && double.TryParse(fr[0], out var a) && double.TryParse(fr[1], out var b) && b != 0) total += a / b; else return null; }
            else if (double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) total += v; else return null;
        }
        return total;
    }

    /// <summary>
    /// The line numbers printed on a piping plan, once each for the whole drawing, against the model's pipes, the line list and the P&amp;IDs, and the drawings it refers to
    /// against the project inputs. A line number is compared as the same line only when it matches in full; sizes are compared in inches. Nothing is concluded from a line
    /// number appearing with different sizes (branches normally have another size) or from an elevation being outside the sheet's plan range (vertical pipes).
    /// </summary>
    private void LineNumbers(IReadOnlyList<GaSheet> sheets, List<Finding> f)
    {
        var lines = sheets.SelectMany(s => s.Lines).GroupBy(l => l.Key).Select(g => g.First()).ToList();
        var file = sheets.Count > 0 ? sheets[0].File : "(drawing)";
        if (lines.Count > 0)
        {
            var model = lines.Select(l => (L: l, M: snap.Find(l.Text))).ToList();
            var found = model.Count(x => x.M is not null);
            if (snap.Lines.Count > 0)
            {
                if (found == 0)
                    f.Add(new Finding("lines", FindingStatus.Review, file, $"none of the {lines.Count} line numbers on this drawing is a pipe of the exported model: is it the right model, or does the model name its lines differently?", $"{lines.Count} line numbers", "0 found", new SourceRef(_modelDoc)));
                else
                {
                    f.Add(new Finding("lines", FindingStatus.Ok, file, $"{found} of {lines.Count} line numbers are pipes of the model", $"{lines.Count}", $"{found}", new SourceRef(_modelDoc)));
                    foreach (var (l, m) in model)
                    {
                        if (m is null) { f.Add(new Finding("lines", FindingStatus.Review, l.Text, "the line number is not a pipe of the exported model", l.Text, "(not in the model)", new SourceRef(_modelDoc), file)); continue; }
                        if (Units.ParseQuantity(m.Attr("BORE")) is { } b && Units.DnToNps.TryGetValue(((int)b.Value).ToString(), out var nps) && NpsValue(nps) is { } mv && l.SizeIn is { } ds && Math.Abs(mv - ds) > 0.01)
                            f.Add(new Finding("lines", FindingStatus.Fail, l.Text, "the size in the line number differs from the model bore", l.SizeIn + "\"", nps + "\"", new SourceRef(_modelDoc, "PIPE BORE", "/" + m.Name), file));
                    }
                }
            }
            else if (snap.HasEquipment || snap.TotalItems > 0) f.Add(new Finding("lines", FindingStatus.Info, file, "the exported model has no pipes: the line numbers were not compared with it"));
            if (evidence?.LineList is { Entries.Count: > 0 } ll)
            {
                var ok = 0;
                foreach (var l in lines)
                {
                    var e = ll.Find(l.Text).FirstOrDefault(x => x.Key == LineListSet.Normalize(l.Text));
                    if (e is null) { f.Add(new Finding("line-list", FindingStatus.Review, l.Text, "the line number is not in the line list (or is spelled differently there)", l.Text, "(not found)", new SourceRef("line list"), file)); continue; }
                    var src = new SourceRef(e.File, $"sheet {e.Sheet}, row {e.Row}", e.LineNo);
                    if (l.SizeIn is { } ds && e.Fields.TryGetValue("Size", out var sv) && sv.Number is { } ln && Math.Abs(ln - ds) > 0.01 && Math.Abs(ln - ds * 25.4) > 0.5)
                        f.Add(new Finding("line-list", FindingStatus.Fail, l.Text, "the size in the line number differs from the line list size", l.SizeIn + "\"", sv.Text + (sv.Unit is null ? "" : " " + sv.Unit), src, file));
                    else ok++;
                }
                if (ok > 0) f.Add(new Finding("line-list", FindingStatus.Ok, file, $"{ok} of {lines.Count} line numbers are in the line list with the same size", $"{lines.Count}", $"{ok}", new SourceRef("line list")));
            }
            else if (evidence is not null) f.Add(new Finding("line-list", FindingStatus.Info, file, "no line list in the project inputs: the line numbers were not compared with it"));
            if (evidence?.Index is { } idx && evidence.Inventory is { } inv && inv.Any(d => d.Kind == InputKind.Pid))
            {
                var onPid = 0;
                foreach (var l in lines)
                {
                    var hit = idx.Search(l.Text, InputKind.Pid, 6).FirstOrDefault(h => LineListSet.Normalize(h.Text).Contains(LineListSet.Normalize(l.Text), StringComparison.Ordinal));
                    if (hit is not null) onPid++;
                    else f.Add(new Finding("tags", FindingStatus.Review, l.Text, "the line number was not found in the text of the P&IDs (a scan, a different spelling, or it is not there)", l.Text, "(not found)", new SourceRef("P&ID"), file));
                }
                if (onPid > 0) f.Add(new Finding("tags", FindingStatus.Ok, file, $"{onPid} of {lines.Count} line numbers are in the P&IDs", $"{lines.Count}", $"{onPid}", new SourceRef("P&ID")));
            }
        }
        var refs = sheets.SelectMany(s => s.References).Distinct().ToList();
        if (refs.Count > 0 && evidence?.Inventory is { } inv2)
        {
            var absent = refs.Where(r => !inv2.Any(d => d.RelPath.Contains(r, StringComparison.OrdinalIgnoreCase) || d.RelPath.Contains(r[2..], StringComparison.OrdinalIgnoreCase))).ToList();
            f.Add(new Finding("drawing", FindingStatus.Info, file, absent.Count == 0 ? $"all {refs.Count} drawings this one continues on are in the project inputs" : $"{absent.Count} of the {refs.Count} drawings this one continues on are not in the project inputs (matchlines and CONT. ON DRAWING were not followed): {string.Join(", ", absent.Take(12))}"));
        }
    }

    // ----------------------------------------------------------------------------- GA-02
    private void Positions(GaSheet s, List<Finding> f)
    {
        var subject = $"sheet {s.Page}";
        if (!snap.HasEquipment) return;
        double Dist(double x, double y, GaTag t) => Math.Sqrt((x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y));
        ModelEquipment? Owner(double x, double y, out GaTag? tag)
        {
            tag = null; var best = double.MaxValue;
            foreach (var t in s.Tags) { var d = Dist(x, y, t); if (d < best && d <= _o.NearPt && snap.FindEquipment(t.Text).Count > 0) { best = d; tag = t; } }
            return tag is null ? null : snap.FindEquipment(tag.Text)[0];
        }
        foreach (var c in s.Coords)
        {
            var eq = Owner(c.X, c.Y, out var tag);
            var txt = $"E {c.E:0} N {c.N:0} U {c.U:0} mm" + (c.Imperial ? "" : " (taken as mm)");
            if (eq is null || !eq.HasBox) { f.Add(new Finding("coordinates", FindingStatus.Info, subject, $"a coordinate label ({txt}) is not next to any equipment tag that is in the model: not compared")); continue; }
            var d = eq.DistanceToBox((c.E, c.N, c.U));
            var o = eq.Origin is { } org ? Units.Dist((c.E, c.N, c.U), org) : double.PositiveInfinity;
            if (d <= _o.BoxToleranceMm || o <= _o.BoxToleranceMm)
                f.Add(new Finding("coordinates", FindingStatus.Ok, tag!.Text, $"the coordinate is on the equipment in the model", txt, Edge(eq.BboxMm!), ModelSrc(eq), subject));
            else
                f.Add(new Finding("coordinates", FindingStatus.Fail, tag!.Text, $"the coordinate is {d:0} mm outside the equipment's extent in the model (tolerance {_o.BoxToleranceMm:0} mm)", txt, Edge(eq.BboxMm!) + (eq.Origin is { } og ? $"; origin E {og.E:0} N {og.N:0} U {og.U:0}" : ""), ModelSrc(eq), subject));
        }
        foreach (var e in s.Elevations)
        {
            var eq = Owner(e.X, e.Y, out var tag);
            if (eq is null || !eq.HasBox) continue;
            var lo = eq.BboxMm![2] - _o.BoxToleranceMm; var hi = eq.BboxMm[5] + _o.BoxToleranceMm;
            if (e.Mm >= lo && e.Mm <= hi) f.Add(new Finding("elevations", FindingStatus.Ok, tag!.Text, "the elevation is within the equipment's height range in the model", $"{e.Raw} = {e.Mm:0} mm", $"U {eq.BboxMm[2]:0}..{eq.BboxMm[5]:0} mm", ModelSrc(eq), subject));
            else f.Add(new Finding("elevations", FindingStatus.Review, tag!.Text, "the elevation is outside the equipment's height range in the model (what the elevation refers to, such as the top of a support, is not known: check it)", $"{e.Raw} = {e.Mm:0} mm", $"U {eq.BboxMm[2]:0}..{eq.BboxMm[5]:0} mm", ModelSrc(eq), subject));
        }
    }
}


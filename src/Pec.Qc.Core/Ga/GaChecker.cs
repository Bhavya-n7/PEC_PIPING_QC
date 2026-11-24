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
            Completeness(s, f);
            Tags(s, f);
            Positions(s, f);
        }
        f.Add(new Finding("dimensions", FindingStatus.Info, file, "dimensions on the drawing, equipment sizes against the model extents, nozzle positions and orientations against the vendor data were NOT checked"));
        f.Add(new Finding("clearances", FindingStatus.Info, file, "access and clearances are a judgement for the engineer: not checked"));
        return f;
    }

    private static string Edge(double[] b) => $"E {b[0]:0}..{b[3]:0}, N {b[1]:0}..{b[4]:0}, U {b[2]:0}..{b[5]:0} mm";
    private SourceRef ModelSrc(ModelEquipment e) => new(_modelDoc, e.Path.Split(" > ").LastOrDefault() ?? "", e.Name);

    // ----------------------------------------------------------------------------- GA-05 / GA-06
    private static void Completeness(GaSheet s, List<Finding> f)
    {
        var subject = $"sheet {s.Page}";
        void Item(bool present, string what, string review) => f.Add(present ? new Finding("drawing", FindingStatus.Ok, subject, $"{what} found", Source: new SourceRef(s.File, subject)) : new Finding("drawing", FindingStatus.Review, subject, review));
        Item(s.Scale.Length > 0, "the scale (" + s.Scale + ")", "no scale was found on the sheet (text 'SCALE 1:n'): look at the title block");
        Item(s.HasNorthArrow, "a north arrow", "no north arrow was found as text (an arrow drawn as a symbol is not read): look at the drawing");
        Item(s.Revision.Length > 0, "the revision (" + s.Revision + ")", "no revision was found (text 'REV x'): look at the title block");
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
        if (s.Tags.Count == 0 && s.Loose.Count == 0) f.Add(new Finding("equipment", FindingStatus.Review, subject, "no equipment tag was read on this sheet (the tags may be in a form the reader does not know)"));
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


using Pec.Qc.Core.Findings;

namespace Pec.Qc.Core.Pid;

/// <summary>How tolerant the comparison is. The move tolerance is in points of the reference frame (A3 landscape, 1190 x 842).</summary>
public sealed record PidOptions
{
    /// <summary>An item that is the same on both drawings but sits further from its original place than this is reported (a REVIEW: a redraw can legitimately shift things).</summary>
    public double MoveTolerance { get; init; } = 30.0;
    /// <summary>Two texts that differ by at most this many characters (and are not equal) are one item changed, not one missing and one extra.</summary>
    public int MaxEdits { get; init; } = 2;
}

/// <summary>
/// The P&amp;ID check: the redrawn drawing against the client's original, sheet by sheet. Everything on the original must be on the redrawn drawing. A missing or changed
/// line number, tag, label or note is a FAIL with both texts; something only on the redrawn drawing, a different count, or an item that moved is a REVIEW. What the text
/// cannot show (symbols, routing, flow direction, line type) is always reported as not verified.
/// </summary>
public sealed class PidComparer(PidOptions? options = null)
{
    private readonly PidOptions _o = options ?? new PidOptions();

    public IReadOnlyList<Finding> Compare(IReadOnlyList<PidSheet> original, IReadOnlyList<PidSheet> redrawn)
    {
        var f = new List<Finding>();
        var origName = original.Count > 0 ? original[0].File : "(original)";
        var redName = redrawn.Count > 0 ? redrawn[0].File : "(redrawn)";
        if (original.Count != redrawn.Count)
            f.Add(new Finding("sheets", FindingStatus.Fail, redName, $"the redrawn drawing has {redrawn.Count} sheet(s), the original has {original.Count}", $"{redrawn.Count} sheets", $"{original.Count} sheets", new SourceRef(origName, "all sheets")));
        else f.Add(new Finding("sheets", FindingStatus.Ok, redName, $"both have {original.Count} sheet(s)", $"{redrawn.Count}", $"{original.Count}", new SourceRef(origName, "all sheets")));
        for (var i = 0; i < Math.Min(original.Count, redrawn.Count); i++) CompareSheet(original[i], redrawn[i], f);
        f.Add(new Finding("graphics", FindingStatus.Info, redName, "symbols, line routing, flow arrows, line types and connections between items were NOT compared: only the text was. A person checks them on the drawing"));
        return f;
    }

    private static string Place(PidItem i) => $"zone {i.Zone}";

    private void CompareSheet(PidSheet o, PidSheet r, List<Finding> f)
    {
        var subject = $"sheet {o.Page}";
        if (!o.HasText || !r.HasText)
        {
            var which = !o.HasText && !r.HasText ? "both drawings have" : !o.HasText ? "the original has" : "the redrawn drawing has";
            f.Add(new Finding("sheets", FindingStatus.Review, subject, $"{which} no text on this sheet (a scan or an image): it cannot be compared"));
            return;
        }
        foreach (var w in o.Warnings.Concat(r.Warnings)) f.Add(new Finding("parse", FindingStatus.Review, subject, w));
        var pairs = new List<(PidItem O, PidItem R)>(); var missing = new List<PidItem>(); var extras = new List<PidItem>();
        foreach (var kind in new[] { PidKind.Line, PidKind.Tag, PidKind.Label }) Match(o, r, kind, pairs, missing, extras);
        var src = (PidItem i) => new SourceRef(o.File, $"sheet {o.Page}, {Place(i)}", i.Text);
        // a missing item and an extra one that look alike are one item that was changed (even when the changed text no longer looks like a tag)
        foreach (var m in missing.ToList())
        {
            var cand = extras.Select(e => (E: e, D: Edit(m.Key, e.Key))).Where(x => x.D > 0 && x.D <= _o.MaxEdits && (m.Kind != PidKind.Label || m.Key.Length >= 4)).OrderBy(x => x.D).FirstOrDefault();
            if (cand.E is null) continue;
            extras.Remove(cand.E); missing.Remove(m);
            f.Add(new Finding(Check(m.Kind), FindingStatus.Fail, subject, $"the {Noun(m.Kind)} is different on the redrawn drawing{LookAlike(m.Text, cand.E.Text)}", cand.E.Text, m.Text, src(m), Place(cand.E)));
        }
        foreach (var m in missing)
            f.Add(new Finding(Check(m.Kind), FindingStatus.Fail, subject, $"the {Noun(m.Kind)} is on the original but not on the redrawn drawing", "(not on the redrawn drawing)", m.Text, src(m), $"original {Place(m)}"));
        foreach (var e in extras)
            f.Add(new Finding(Check(e.Kind), FindingStatus.Review, subject, $"the {Noun(e.Kind)} is on the redrawn drawing but not on the original (an addition, or a copy that is wrong)", e.Text, "(not on the original)", new SourceRef(o.File, $"sheet {o.Page}"), Place(e)));
        foreach (var (po, pr) in pairs)
            if (Dist(po, pr) > _o.MoveTolerance)
                f.Add(new Finding("position", FindingStatus.Review, subject, $"the {Noun(po.Kind)} is in a different place ({Dist(po, pr):0} pt, tolerance {_o.MoveTolerance:0})", $"{pr.Text} at {Place(pr)}", $"{po.Text} at {Place(po)}", src(po), Place(pr)));
        foreach (var kind in new[] { PidKind.Line, PidKind.Tag, PidKind.Label })
        {
            var total = o.Items.Count(i => i.Kind == kind); var other = r.Items.Count(i => i.Kind == kind);
            if (total > 0 && !missing.Any(m => m.Kind == kind) && !extras.Any(e => e.Kind == kind) && pairs.Count(p => p.O.Kind == kind) == total)
                f.Add(new Finding(Check(kind), FindingStatus.Ok, subject, $"all {total} {Noun(kind)}(s) of the original are on the redrawn drawing", $"{other}", $"{total}", new SourceRef(o.File, $"sheet {o.Page}")));
            else if (total == 0 && other == 0)
                f.Add(new Finding(Check(kind), FindingStatus.Info, subject, $"no {Noun(kind)} was read on this sheet of either drawing"));
        }
        CompareNotes(o, r, f);
    }

    private static string Check(PidKind k) => k switch { PidKind.Line => "lines", PidKind.Tag => "tags", _ => "labels" };
    private static string Noun(PidKind k) => k switch { PidKind.Line => "line number", PidKind.Tag => "tag", _ => "label" };

    /// <summary>Pairs the equal items of one kind (nearest first); what is left over on the original is missing, on the redrawn drawing extra.</summary>
    private static void Match(PidSheet o, PidSheet r, PidKind kind, List<(PidItem, PidItem)> pairs, List<PidItem> missing, List<PidItem> extras)
    {
        var rLeft = r.Items.Where(i => i.Kind == kind).GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var g in o.Items.Where(i => i.Kind == kind).GroupBy(i => i.Key))
        {
            var rs = rLeft.GetValueOrDefault(g.Key) ?? [];
            var os = g.ToList();
            foreach (var oItem in os.ToList())
            {
                if (rs.Count == 0) break;
                var best = rs.OrderBy(x => Dist(oItem, x)).First();
                pairs.Add((oItem, best)); rs.Remove(best); os.Remove(oItem);
            }
            missing.AddRange(os);
            rLeft[g.Key] = rs;
        }
        extras.AddRange(rLeft.Values.SelectMany(x => x));
    }

    private void CompareNotes(PidSheet o, PidSheet r, List<Finding> f)
    {
        var subject = $"sheet {o.Page}";
        var on = o.Items.Where(i => i.Kind == PidKind.Note).ToList();
        var rn = r.Items.Where(i => i.Kind == PidKind.Note).ToList();
        var rWords = Words(string.Join(" ", rn.Select(n => n.Text).Concat(r.Items.Where(i => i.Kind == PidKind.Label).Select(l => l.Text))));
        var rLeft = rn.ToList();
        var ok = 0;
        foreach (var n in on)
        {
            var exact = rLeft.FirstOrDefault(x => x.Key == n.Key);
            if (exact is not null) { rLeft.Remove(exact); ok++; continue; }
            var src = new SourceRef(o.File, $"sheet {o.Page}, {Place(n)}", n.Text);
            var near = rLeft.Select(x => (X: x, Sim: Similarity(n.Key, x.Key))).OrderByDescending(x => x.Sim).FirstOrDefault();
            if (near.X is not null && near.Sim >= 0.85)
            {
                rLeft.Remove(near.X);
                f.Add(new Finding("notes", FindingStatus.Fail, subject, "the note is worded differently on the redrawn drawing", near.X.Text, n.Text, src, Place(near.X)));
                continue;
            }
            // the same words, wrapped or split differently
            var words = Words(n.Text);
            var absent = words.Where(w => !rWords.Remove(w)).ToList();
            if (absent.Count == 0) { ok++; f.Add(new Finding("notes", FindingStatus.Info, subject, "the note's words are all on the redrawn drawing but not as one note (line breaks differ): read it on the drawing", n.Text, n.Text, src, Place(n))); }
            else f.Add(new Finding("notes", FindingStatus.Fail, subject, absent.Count == words.Count ? "the note is on the original but not on the redrawn drawing" : $"the note is incomplete on the redrawn drawing (missing: {string.Join(" ", absent)})",
                "(not on the redrawn drawing)", n.Text, src, $"original {Place(n)}"));
        }
        foreach (var x in rLeft)
            f.Add(new Finding("notes", FindingStatus.Review, subject, "the note is on the redrawn drawing but not on the original", x.Text, "(not on the original)", new SourceRef(o.File, $"sheet {o.Page}"), Place(x)));
        if (on.Count > 0 && ok == on.Count && rLeft.Count == 0) f.Add(new Finding("notes", FindingStatus.Ok, subject, $"all {on.Count} note(s) of the original are on the redrawn drawing", $"{rn.Count}", $"{on.Count}", new SourceRef(o.File, $"sheet {o.Page}")));
    }

    private static List<string> Words(string s) => System.Text.RegularExpressions.Regex.Matches(s.ToUpperInvariant(), @"[A-Z0-9./%&""'-]+").Select(m => m.Value).ToList();
    private static double Dist(PidItem a, PidItem b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Similarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1 : 1.0 - (double)Edit(a, b) / max;
    }

    internal static int Edit(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // 0/O, 1/I/L, 5/S, 8/B, 2/Z: characters that are easy to mistake when a drawing is retyped
    private static string LookAlike(string original, string redrawn)
    {
        var pairs = new[] { ('0', 'O'), ('1', 'I'), ('1', 'L'), ('5', 'S'), ('8', 'B'), ('2', 'Z'), ('6', 'G') };
        var a = PidReader.Key(original); var b = PidReader.Key(redrawn);
        if (a.Length != b.Length) return "";
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i] && pairs.Any(p => (p.Item1 == a[i] && p.Item2 == b[i]) || (p.Item2 == a[i] && p.Item1 == b[i])))
                return $" (look-alike characters: '{a[i]}' became '{b[i]}')";
        return "";
    }
}

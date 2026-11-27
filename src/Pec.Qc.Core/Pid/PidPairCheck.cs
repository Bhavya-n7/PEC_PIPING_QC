namespace Pec.Qc.Core.Pid;

/// <summary>Are two P&amp;ID books a pair at all? Compares the line numbers and tags of the whole books before anything is read as a finding: a different sheet, unit or tag scheme gives a low overlap.</summary>
public static class PidPairCheck
{
    public sealed record Result(int Common, int OriginalCount, int RedrawnCount, double OverlapPercent, string? Warning);

    public static Result Check(IReadOnlyList<PidSheet> original, IReadOnlyList<PidSheet> redrawn)
    {
        static HashSet<string> Keys(IReadOnlyList<PidSheet> b) => b.SelectMany(x => x.Items).Where(i => i.Kind is PidKind.Line or PidKind.Tag).Select(i => i.Key).ToHashSet();
        var o = Keys(original); var r = Keys(redrawn);
        var common = o.Intersect(r).Count(); var overlap = Math.Round(100.0 * common / Math.Max(1, Math.Min(o.Count, r.Count)));
        string? warning = null;
        if (o.Count < 8 || r.Count < 8) warning = "One side has almost no text (a scan, or text drawn as lines): this text comparison says little. Look at the drawings (pdf_zones, pdf_render).";
        else if (overlap < 25) warning = $"Only {overlap}% of the line numbers and tags are on both drawings ({common} of {o.Count} original / {r.Count} redrawn): they may not be a true pair (a different sheet, unit or tag scheme). Look at both title blocks first and tell the user before you list findings.";
        return new Result(common, o.Count, r.Count, overlap, warning);
    }
}

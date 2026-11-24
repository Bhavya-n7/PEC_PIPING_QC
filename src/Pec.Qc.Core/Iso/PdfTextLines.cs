using UglyToad.PdfPig.Content;

namespace Pec.Qc.Core.Iso;

/// <summary>A line of text on a page. Positions are in points on the reference frame (A3 landscape, 1190.55 x 841.89), measured from the TOP left, whatever the real page size.</summary>
public sealed record TextLine(string T, double X0, double Y0, double X1, double Y1)
{
    public override string ToString() => $"{Y0:0},{X0:0} {T}";
}

/// <summary>
/// Rebuilds the text lines of a page from the letters and their positions. Isometric tools often draw one character at a time and print no space characters, so a
/// line is a run of letters on one baseline with only small gaps; a gap wider than about half the letter size starts a new line (that is how table columns and the
/// label / value pairs of the title block stay apart). Rotated text is left out.
/// </summary>
public static class PdfTextLines
{
    public const double RefWidth = 1190.551, RefHeight = 841.889;

    public static List<TextLine> Read(Page page)
    {
        var sx = RefWidth / page.Width;
        var sy = RefHeight / page.Height;
        var letters = page.Letters.Where(l => !string.IsNullOrEmpty(l.Value) && Math.Abs(l.StartBaseLine.Y - l.EndBaseLine.Y) < 0.5).ToList();
        // rows: letters whose baselines are within 0.8 pt of each other, then each row is walked left to right
        var merged = new List<List<Letter>>();
        var current = new List<Letter>();
        var sum = 0.0;
        foreach (var l in letters.OrderByDescending(l => l.StartBaseLine.Y))
        {
            if (current.Count > 0 && Math.Abs(l.StartBaseLine.Y - sum / current.Count) > 0.8)
            {
                merged.Add([.. current.OrderBy(x => x.StartBaseLine.X)]);
                current = []; sum = 0;
            }
            current.Add(l); sum += l.StartBaseLine.Y;
        }
        if (current.Count > 0) merged.Add([.. current.OrderBy(x => x.StartBaseLine.X)]);
        var lines = new List<TextLine>();
        foreach (var row in merged)
        {
            var run = new List<Letter>();
            foreach (var l in row)
            {
                if (run.Count > 0)
                {
                    var prev = run[^1];
                    var size = Math.Max(Math.Max(prev.PointSize, l.PointSize), 4);
                    var gap = l.StartBaseLine.X - prev.EndBaseLine.X;
                    if (gap > 0.5 * size || gap < -1.5 * size) { Flush(run, lines, page, sx, sy); run = []; }
                }
                run.Add(l);
            }
            Flush(run, lines, page, sx, sy);
        }
        return [.. lines.OrderBy(l => Math.Round(l.Y0)).ThenBy(l => l.X0)];
    }

    private static void Flush(List<Letter> run, List<TextLine> into, Page page, double sx, double sy)
    {
        if (run.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < run.Count; i++)
        {
            var l = run[i];
            if (i > 0)
            {
                var gap = l.StartBaseLine.X - run[i - 1].EndBaseLine.X;
                var size = Math.Max(Math.Max(run[i - 1].PointSize, l.PointSize), 4);
                if (gap > 0.15 * size && !run[i - 1].Value.EndsWith(' ') && !l.Value.StartsWith(' ')) sb.Append(' ');
            }
            sb.Append(l.Value);
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0) return;
        var size0 = run.Max(l => l.PointSize);
        var baseY = page.Height - run[0].StartBaseLine.Y;
        var x0 = run[0].StartBaseLine.X;
        var x1 = run[^1].EndBaseLine.X;
        into.Add(new TextLine(text, x0 * sx, (baseY - size0) * sy, x1 * sx, (baseY + 0.25 * size0) * sy));
    }
}

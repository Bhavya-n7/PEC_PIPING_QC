using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Pec.Qc.Core.Findings;

namespace Pec.Qc.Core.Pilot;

/// <summary>One thing the QC team found by hand on a drawing: which drawing, which line or tag, which kind of check, and what was wrong.</summary>
public sealed record HandFinding(string Document, string Subject, string Check, string Description);

public sealed record SavedReport(string File, string Document, string QcType, string Verdict, IReadOnlyList<Finding> Findings);

public enum HandOutcome { Found, Flagged, Missed }

public sealed record HandMatch(HandFinding Hand, HandOutcome Outcome, Finding? Tool);

public sealed record Agreement(
    IReadOnlyList<HandMatch> Matches, IReadOnlyList<Finding> ExtraFails, IReadOnlyList<Finding> ExtraReviews, int FailTotal)
{
    public int HandCount => Matches.Count;
    public int Found => Matches.Count(m => m.Outcome == HandOutcome.Found);
    public int Flagged => Matches.Count(m => m.Outcome == HandOutcome.Flagged);
    public int Missed => Matches.Count(m => m.Outcome == HandOutcome.Missed);
    /// <summary>Hand findings the assistant reported as a FAIL.</summary>
    public double? StrictRecall => HandCount == 0 ? null : (double)Found / HandCount;
    /// <summary>Hand findings the assistant at least pointed at (FAIL or REVIEW).</summary>
    public double? Recall => HandCount == 0 ? null : (double)(Found + Flagged) / HandCount;
    /// <summary>FAILs that match something on the hand list, over all FAILs. A FAIL that is not on the hand list is not necessarily wrong (the team may have missed it): a person looks at each.</summary>
    public double? FailPrecision => FailTotal == 0 ? null : (double)Found / FailTotal;
}

/// <summary>
/// Measures the assistant against the QC team's own result: the pilot's exit criterion. A saved report is read back, the team's hand list is read, and every hand finding is
/// matched to a FAIL (found) or a REVIEW (flagged) of the same drawing, line or tag, and check; the rest is missed. FAILs that nobody listed are returned for a person to judge.
/// </summary>
public static class HandComparison
{
    private static string Norm(string? s) => Regex.Replace((s ?? "").ToUpperInvariant(), @"[^A-Z0-9]", "");

    public static SavedReport ReadReport(string xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        string Sum(string key) => wb.Worksheet("Summary").Rows().Select(r => (r.Cell(1).GetString(), r.Cell(2).GetString())).FirstOrDefault(x => x.Item1 == key).Item2 ?? "";
        var list = new List<Finding>();
        var ws = wb.Worksheet("Findings");
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var st = FindingStatuses.Parse(row.Cell(1).GetString());
            if (st is null) continue;
            var doc = row.Cell(7).GetString(); var page = row.Cell(8).GetString();
            list.Add(new Finding(row.Cell(3).GetString(), st.Value, row.Cell(2).GetString(), row.Cell(4).GetString(), row.Cell(5).GetString(), row.Cell(6).GetString(),
                doc.Length > 0 ? new SourceRef(doc, page) : null, row.Cell(9).GetString(), row.Cell(10).GetString()));
        }
        return new SavedReport(xlsx, Sum("Document"), Sum("QC type"), Sum("Verdict"), list);
    }

    private static readonly string[][] Headers =
    [
        ["drawing", "document", "file", "drawingno", "dwg"], ["subject", "line", "lineno", "tag", "item", "equipment"], ["check", "category", "type", "kind"], ["description", "what", "comment", "finding", "remark", "remarks", "issue"],
    ];

    /// <summary>Reads the team's hand list from .xlsx or .csv. Columns are found by their headers (drawing, line or tag, check, description); only the description is required.</summary>
    public static IReadOnlyList<HandFinding> ReadHandList(string file)
    {
        List<List<string>> rows;
        if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            rows = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).Select(l => SplitCsv(l)).ToList();
        else
        {
            using var wb = new XLWorkbook(file);
            var ws = wb.Worksheets.First();
            rows = ws.RowsUsed().Select(r => Enumerable.Range(1, ws.LastColumnUsed()?.ColumnNumber() ?? 1).Select(c => r.Cell(c).GetString().Trim()).ToList()).ToList();
        }
        if (rows.Count < 2) return [];
        var head = rows[0].Select(h => Norm(h).ToLowerInvariant()).ToList();
        int Col(int k) => head.FindIndex(h => Headers[k].Contains(h));
        var (cd, cs, cc, cx) = (Col(0), Col(1), Col(2), Col(3));
        if (cx < 0) throw new InvalidDataException("The hand list needs a column with the description of what was found (header: Description).");
        string At(List<string> r, int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
        return rows.Skip(1).Select(r => new HandFinding(At(r, cd), At(r, cs), At(r, cc), At(r, cx))).Where(h => h.Description.Length > 0).ToList();
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>(); var sb = new System.Text.StringBuilder(); var q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if ((ch == ',' || ch == ';') && !q) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    private static readonly HashSet<string> Stop = ["THE", "AND", "WITH", "FOR", "FROM", "THAT", "THIS", "WRONG", "SHOULD", "WAS", "ARE", "NOT", "ONLY", "SHEET"];

    private static bool Matches(HandFinding h, Finding f)
    {
        var text = Norm(f.Subject + f.Message + f.DrawingValue + f.SourceValue + f.Check);
        var subjectOk = h.Subject.Length == 0 || Norm(f.Subject).Contains(Norm(h.Subject)) || Norm(h.Subject).Contains(Norm(f.Subject)) && Norm(f.Subject).Length > 3 || text.Contains(Norm(h.Subject));
        if (!subjectOk) return false;
        if (h.Check.Length > 0 && (Norm(f.Check) == Norm(h.Check) || text.Contains(Norm(h.Check)))) return true;
        // otherwise the words of the description: any word of 4+ letters that the finding also contains (class, pressure, tee, coordinate...)
        var words = Regex.Matches(h.Description.ToUpperInvariant(), "[A-Z]{4,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct();
        return words.Any(w => text.Contains(w)) || (h.Check.Length == 0 && h.Description.Length > 0 && Regex.Matches(h.Description, @"\d+").Select(m => m.Value).Any(n => n.Length >= 2 && text.Contains(n)));
    }

    public static Agreement Compare(IReadOnlyList<HandFinding> hand, SavedReport report)
    {
        var docKey = Norm(Path.GetFileNameWithoutExtension(report.Document));
        var fails = report.Findings.Where(f => f.Status == FindingStatus.Fail).ToList();
        var reviews = report.Findings.Where(f => f.Status == FindingStatus.Review).ToList();
        var used = new HashSet<Finding>();
        var result = new List<HandMatch>();
        foreach (var h in hand.Where(h => h.Document.Length == 0 || Norm(Path.GetFileNameWithoutExtension(h.Document)).Contains(docKey) || docKey.Contains(Norm(Path.GetFileNameWithoutExtension(h.Document)))))
        {
            var f = fails.FirstOrDefault(x => !used.Contains(x) && Matches(h, x));
            if (f is not null) { used.Add(f); result.Add(new HandMatch(h, HandOutcome.Found, f)); continue; }
            var r = reviews.FirstOrDefault(x => !used.Contains(x) && Matches(h, x));
            if (r is not null) { used.Add(r); result.Add(new HandMatch(h, HandOutcome.Flagged, r)); continue; }
            result.Add(new HandMatch(h, HandOutcome.Missed, null));
        }
        return new Agreement(result, fails.Where(x => !used.Contains(x)).ToList(), reviews.Where(x => !used.Contains(x)).ToList(), fails.Count);
    }
}

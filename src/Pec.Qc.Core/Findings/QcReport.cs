using System.Text;
using ClosedXML.Excel;
using Pec.Qc.Core.Claude;

namespace Pec.Qc.Core.Findings;

public sealed record QcReportInfo(string Project, QcType QcType, string Document, string Model, string CheckedBy, DateTime When);

public sealed record FindingCounts(int Fail, int Review, int Info, int Ok);

/// <summary>The saved result of a QC check: an Excel workbook (summary + findings) and a Markdown copy anyone can read, in the project's folder on the shared drive.</summary>
public static class QcReport
{
    public static FindingCounts Counts(IEnumerable<Finding> f)
    {
        var l = f.ToList();
        return new(l.Count(x => x.Status == FindingStatus.Fail), l.Count(x => x.Status == FindingStatus.Review), l.Count(x => x.Status == FindingStatus.Info), l.Count(x => x.Status == FindingStatus.Ok));
    }

    /// <summary>FAIL if anything fails, REVIEW if anything needs a person, else OK. A check with no findings at all is not an OK: nothing was checked.</summary>
    public static string Verdict(IEnumerable<Finding> f)
    {
        var l = f.ToList();
        if (l.Count == 0) return "NOT CHECKED";
        return l.Any(x => x.Status == FindingStatus.Fail) ? "FAIL" : l.Any(x => x.Status == FindingStatus.Review) ? "REVIEW" : "OK";
    }

    private static IEnumerable<Finding> Ordered(IEnumerable<Finding> f) => f.OrderByDescending(x => x.Status).ThenBy(x => x.Subject, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Check);

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    public static string Markdown(QcReportInfo info, IReadOnlyList<Finding> findings)
    {
        var c = Counts(findings);
        var sb = new StringBuilder();
        sb.AppendLine($"# {QcTypes.Label(info.QcType)}: {info.Document}").AppendLine();
        sb.AppendLine($"Project: **{info.Project}**  |  Model: {(info.Model.Length > 0 ? info.Model : "-")}  |  Checked by: {info.CheckedBy}  |  {info.When:yyyy-MM-dd HH:mm}").AppendLine();
        sb.AppendLine($"**Verdict: {Verdict(findings)}**   FAIL {c.Fail}  |  REVIEW {c.Review}  |  INFO {c.Info}  |  OK {c.Ok}").AppendLine();
        sb.AppendLine("FAIL = the drawing and the source disagree. REVIEW = could not be decided, a person must look. INFO = cannot be verified. A person decides which side is right and signs off.").AppendLine();
        var attention = Ordered(findings).Where(f => f.Status is FindingStatus.Fail or FindingStatus.Review).ToList();
        sb.AppendLine("## Findings needing attention").AppendLine();
        if (attention.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Status | Subject | Check | What | Drawing | Source | Where the source is |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var f in attention)
                sb.AppendLine($"| {FindingStatuses.Code(f.Status)} | {Esc(f.Subject)} | {Esc(f.Check)} | {Esc(f.Message)} | {Esc(f.DrawingValue)} | {Esc(f.SourceValue)} | {Esc(f.Source?.ToString() ?? "")} |");
        }
        var info2 = Ordered(findings).Where(f => f.Status == FindingStatus.Info).ToList();
        if (info2.Count > 0) { sb.AppendLine().AppendLine("## Could not be verified").AppendLine(); foreach (var f in info2) sb.AppendLine($"- {f.Subject}{(f.Subject.Length > 0 ? ": " : "")}{f.Message}"); }
        return sb.ToString();
    }

    public static void WriteExcel(string path, QcReportInfo info, IReadOnlyList<Finding> findings)
    {
        var fill = new Dictionary<FindingStatus, XLColor>
            { [FindingStatus.Fail] = XLColor.FromHtml("#F8CBAD"), [FindingStatus.Review] = XLColor.FromHtml("#FFE699"), [FindingStatus.Info] = XLColor.FromHtml("#DDEBF7"), [FindingStatus.Ok] = XLColor.FromHtml("#C6E0B4") };
        using var wb = new XLWorkbook();
        var sum = wb.AddWorksheet("Summary");
        var c = Counts(findings);
        var rows = new (string, string)[]
        {
            ("QC type", QcTypes.Label(info.QcType)), ("Document", info.Document), ("Project", info.Project), ("Model", info.Model), ("Checked by", info.CheckedBy),
            ("Date", info.When.ToString("yyyy-MM-dd HH:mm")), ("Verdict", Verdict(findings)), ("FAIL", c.Fail.ToString()), ("REVIEW", c.Review.ToString()), ("INFO", c.Info.ToString()), ("OK", c.Ok.ToString()),
        };
        for (var i = 0; i < rows.Length; i++) { sum.Cell(i + 1, 1).Value = rows[i].Item1; sum.Cell(i + 1, 1).Style.Font.Bold = true; sum.Cell(i + 1, 2).Value = rows[i].Item2; }
        sum.Cell(rows.Length + 2, 1).Value = "FAIL = drawing and source disagree. REVIEW = could not be decided, a person must look. INFO = cannot be verified. A person decides and signs off.";
        sum.Columns().AdjustToContents(); sum.Column(2).Width = Math.Min(sum.Column(2).Width, 70);

        var ws = wb.AddWorksheet("Findings");
        string[] head = ["Status", "Subject", "Check", "What", "Drawing", "Source", "Source document", "Page / item", "Location", "Rule"];
        for (var i = 0; i < head.Length; i++) { ws.Cell(1, i + 1).Value = head[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        var r = 2;
        foreach (var f in Ordered(findings))
        {
            ws.Cell(r, 1).Value = FindingStatuses.Code(f.Status); ws.Cell(r, 1).Style.Fill.BackgroundColor = fill[f.Status];
            ws.Cell(r, 2).Value = f.Subject; ws.Cell(r, 3).Value = f.Check; ws.Cell(r, 4).Value = f.Message; ws.Cell(r, 5).Value = f.DrawingValue; ws.Cell(r, 6).Value = f.SourceValue;
            ws.Cell(r, 7).Value = f.Source?.Document ?? ""; ws.Cell(r, 8).Value = string.Join(", ", new[] { f.Source?.Page ?? "", f.Source?.Item ?? "" }.Where(x => x.Length > 0));
            ws.Cell(r, 9).Value = f.Location; ws.Cell(r, 10).Value = f.RuleId;
            r++;
        }
        ws.SheetView.FreezeRows(1);
        if (r > 2) ws.Range(1, 1, r - 1, head.Length).SetAutoFilter();
        ws.Columns().AdjustToContents();
        foreach (var col in ws.ColumnsUsed()) if (col.Width > 60) col.Width = 60;
        ws.Range(2, 1, Math.Max(r - 1, 2), head.Length).Style.Alignment.WrapText = true;
        ws.Range(2, 1, Math.Max(r - 1, 2), head.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        wb.SaveAs(path);
    }

    /// <summary>The folder of a project's reports: <c>&lt;shared&gt;\reports\&lt;project&gt;</c> (this PC's data folder when there is no shared folder).</summary>
    public static string FolderFor(Settings s, string project) => Path.Combine(s.EffectiveReportsDirectory, ProjectStore.SafeName(string.IsNullOrWhiteSpace(project) ? "(no project)" : project));

    public static (string Excel, string Markdown) PathsFor(Settings s, QcReportInfo info)
    {
        var stem = $"{info.When:yyyyMMdd-HHmmss}-{QcTypes.Code(info.QcType)}-{ProjectStore.SafeName(Path.GetFileNameWithoutExtension(info.Document))}";
        var dir = FolderFor(s, info.Project);
        return (Path.Combine(dir, stem + ".xlsx"), Path.Combine(dir, stem + ".md"));
    }

    /// <summary>Writes both files (creating the folder). Never overwrites: the name carries the time to the second, and an existing name gets a number.</summary>
    public static (string Excel, string Markdown) Write(Settings s, QcReportInfo info, IReadOnlyList<Finding> findings)
    {
        var (xl, md) = PathsFor(s, info);
        Directory.CreateDirectory(Path.GetDirectoryName(xl)!);
        for (var i = 2; File.Exists(xl) || File.Exists(md); i++)
        {
            xl = Path.Combine(Path.GetDirectoryName(xl)!, Path.GetFileNameWithoutExtension(PathsFor(s, info).Excel) + $"-{i}.xlsx");
            md = Path.ChangeExtension(xl, ".md");
        }
        WriteExcel(xl, info, findings);
        File.WriteAllText(md, Markdown(info, findings), new UTF8Encoding(false));
        return (xl, md);
    }
}

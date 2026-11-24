using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class QcReportTools
{
    [McpServerTool(Name = "qc_report_write", Destructive = true)]
    [Description("Save the result of a QC check as a report in the project's reports folder (an Excel workbook and a Markdown copy). Give the findings as JSON: [{\"check\":\"bom\",\"status\":\"FAIL|REVIEW|OK|INFO\",\"subject\":\"the line, tag or sheet\",\"message\":\"what is wrong\",\"drawing\":\"value on the drawing\",\"source\":\"value in the source\",\"sourceDoc\":\"document\",\"sourcePage\":\"page, sheet or row\",\"location\":\"where on the drawing\"}]. A FAIL needs the subject, BOTH values and the source document (and page or row); if the source does not say, it is a REVIEW. INFO is for what could not be verified. The verdict is the worst finding; no findings is NOT CHECKED, not a pass. apply=false validates and previews; apply=true asks the user to approve and saves.")]
    public static object Write([Description("iso, pid or ga")] string qcType, [Description("The drawing checked, e.g. 2-CD-60A07900-B400A-N-HS-017.pdf")] string document, [Description("The findings as a JSON array")] string findings,
        [Description("Project name; empty = the active project")] string? project = null, [Description("The Navisworks model checked against, if any")] string? model = null, bool apply = false)
    {
        var type = QcTypes.Parse(qcType) ?? throw new McpException("qcType must be iso, pid or ga.");
        if (string.IsNullOrWhiteSpace(document)) throw new McpException("Say which drawing was checked (document).");
        var list = FindingJson.Parse(findings, out var errors);
        if (errors.Count > 0) throw new McpException("The findings cannot be saved yet:\n- " + string.Join("\n- ", errors));
        var card = string.IsNullOrWhiteSpace(project) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new McpException($"There is no project named '{project}'.");
        var s = Resolve.Settings();
        var who = Resolve.Who();
        var info = new QcReportInfo(card?.Name ?? "", type, document.Trim(), (model ?? card?.ModelFile ?? "").Trim(), who.Display, DateTime.Now);
        var (xl, md) = QcReport.PathsFor(s, info);
        var counts = QcReport.Counts(list);
        var verdict = QcReport.Verdict(list);
        var attention = list.Where(f => f.Status is FindingStatus.Fail or FindingStatus.Review).OrderByDescending(f => f.Status).Take(12)
            .Select(f => new { status = FindingStatuses.Code(f.Status), f.Subject, f.Message }).ToList();
        if (!apply)
            return new { saved = false, verdict, counts, wouldWrite = new { excel = xl, markdown = md }, project = info.Project.Length > 0 ? info.Project : "(no active project: the report goes to '(no project)')", attention, next = "Show the user the verdict and the findings, then call again with apply=true to save the report." };
        var canonical = $"{info.Project}|{qcType}|{info.Document}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(findings)))[..16]}";
        if (Resolve.NeedApproval("qc_report_write", canonical, $"Save QC report: {QcTypes.Label(type)} of {info.Document} ({verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW)", QcReport.Markdown(info, list)) is { } wait) return wait;
        var (xlsx, mdFile) = QcReport.Write(s, info, list);
        var audit = Resolve.Audit("qc_report_write", canonical, $"{verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW, {counts.Ok} OK");
        return new { saved = true, verdict, counts, excel = xlsx, markdown = mdFile, audit, next = "Tell the user the two paths." };
    }
}

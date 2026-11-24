using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Pid;

namespace Pec.Qc.Mcp.Tools;

/// <summary>The P&amp;ID check: a redrawn P&amp;ID against the client's original.</summary>
[McpServerToolType]
public static class PidTools
{
    private static string Path_(string file, string? project) => IsoTools.ResolvePdf(file, project);

    [McpServerTool(Name = "pid_read", ReadOnly = true)]
    [Description("Read a P&ID PDF's text layer as data: per sheet the line numbers, tags (equipment, instruments, valves), short labels and notes, each with its zone on the sheet (A1..H6). Horizontal and rotated text is read. Symbols and line routing are not. A sheet with no text (a scan) is reported as unreadable. Use it to answer a question about one drawing or to see what pid_compare was given.")]
    public static object Read([Description("Path of the P&ID PDF, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null,
        [Description("Only this sheet (page number); empty = all")] int? sheet = null, [Description("Also list the notes' and labels' full text (long)")] bool full = false)
    {
        var path = Path_(file, project);
        List<PidSheet> sheets;
        try { sheets = PidReader.Read(path); } catch (Exception e) when (e is not McpException) { throw new McpException($"The PDF cannot be read: {e.Message}"); }
        if (sheet is { } n) sheets = sheets.Where(s => s.Page == n).ToList();
        return new
        {
            file = path,
            sheets = sheets.Select(s => new
            {
                page = s.Page, readable = s.HasText, s.Warnings,
                counts = new { lines = s.Items.Count(i => i.Kind == PidKind.Line), tags = s.Items.Count(i => i.Kind == PidKind.Tag), labels = s.Items.Count(i => i.Kind == PidKind.Label), notes = s.Items.Count(i => i.Kind == PidKind.Note) },
                lines = s.Items.Where(i => i.Kind == PidKind.Line).Select(i => new { text = i.Text, zone = i.Zone }).ToList(),
                tags = s.Items.Where(i => i.Kind == PidKind.Tag).Select(i => new { text = i.Text, zone = i.Zone }).ToList(),
                notes = full ? s.Items.Where(i => i.Kind == PidKind.Note).Select(i => new { text = i.Text, zone = i.Zone }).Cast<object>().ToList() : null,
                labels = full ? s.Items.Where(i => i.Kind == PidKind.Label).Select(i => new { text = i.Text, zone = i.Zone }).Cast<object>().ToList() : null,
            }).ToList(),
            next = "Give the user what they asked for. A sheet that is not readable was not checked.",
        };
    }

    /// <summary>The original P&amp;ID in the project's inputs for a redrawn file: the same file name, or one name containing the other, or a shared drawing number of 10+ characters.</summary>
    private static string FindOriginal(string redrawnPath, string? project)
    {
        var (lib, _) = Resolve.Inputs(project);
        var stem = Path.GetFileNameWithoutExtension(redrawnPath).ToUpperInvariant();
        var docs = lib.Inventory().Where(d => d.Kind == InputKind.Pid && d.Ext == ".pdf").ToList();
        var tokens = Regex.Split(stem, @"[\s_]+").Where(t => t.Length >= 10).ToList();
        var hits = docs.Where(d =>
        {
            var s = Path.GetFileNameWithoutExtension(d.RelPath).ToUpperInvariant();
            return s == stem || s.Contains(stem) || stem.Contains(s) || tokens.Any(t => s.Contains(t));
        }).Where(d => !string.Equals(Path.GetFullPath(Path.Combine(lib.Folder, d.RelPath)), Path.GetFullPath(redrawnPath), StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return Path.Combine(lib.Folder, hits[0].RelPath);
        throw new McpException(hits.Count == 0
            ? $"I could not find the original of {Path.GetFileName(redrawnPath)} among the P&IDs of the project inputs ({docs.Count} P&ID file(s)). Give the original with 'original'."
            : $"{hits.Count} P&IDs could be the original ({string.Join(", ", hits.Select(h => h.RelPath))}). Give the right one with 'original'.");
    }

    [McpServerTool(Name = "pid_compare", Destructive = true)]
    [Description("Check a REDRAWN P&ID against the client's ORIGINAL P&ID, sheet by sheet, on their text: every line number, tag (equipment, instrument, valve), label and note on the original must be on the redrawn drawing. FAIL = missing or changed (both texts shown, look-alike characters such as 0/O named, with the sheet and zone on the original); REVIEW = only on the redrawn drawing, an item in a different place, or a sheet with no text; the graphics (symbols, routing, flow arrows, line types) are NOT compared and are always listed as not verified. The original is found in the project's inputs (pid folder) by file name unless you give it. apply=false only shows the result; apply=true asks the user to approve and saves the Excel + Markdown report.")]
    public static object Compare([Description("The redrawn P&ID PDF: path, or file name inside the project's inputs folder")] string redrawn, [Description("The client's original P&ID PDF; empty = find it in the project's pid inputs")] string? original = null,
        [Description("Project name; empty = the active project")] string? project = null, [Description("An item that moved more than this many points (A3 = 1190 x 842) is a REVIEW; default 30")] double moveTolerance = 30,
        [Description("Also list what matched (long)")] bool includeOk = false, [Description("true = save the report (asks the user to approve)")] bool apply = false)
    {
        var red = Path_(redrawn, project);
        var orig = string.IsNullOrWhiteSpace(original) ? FindOriginal(red, project) : Path_(original, project);
        if (string.Equals(Path.GetFullPath(red), Path.GetFullPath(orig), StringComparison.OrdinalIgnoreCase)) throw new McpException("The redrawn drawing and the original are the same file.");
        List<PidSheet> os, rs;
        try { os = PidReader.Read(orig); rs = PidReader.Read(red); } catch (Exception e) when (e is not McpException) { throw new McpException($"A PDF cannot be read: {e.Message}"); }
        var findings = new PidComparer(new PidOptions { MoveTolerance = moveTolerance }).Compare(os, rs);
        var counts = QcReport.Counts(findings); var verdict = QcReport.Verdict(findings);
        var card = string.IsNullOrWhiteSpace(project) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
        var s = Resolve.Settings();
        var info = new QcReportInfo(card?.Name ?? "", QcType.Pid, Path.GetFileName(red), "original: " + Path.GetFileName(orig), Resolve.Who().Display, DateTime.Now);
        var (xl, md) = QcReport.PathsFor(s, info);
        object? saved = new { saved = false, wouldWrite = new { excel = xl, markdown = md } };
        if (apply)
        {
            var canonical = $"{info.Project}|pid|{info.Document}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FindingJson.ToJson(findings))))[..16]}";
            if (Resolve.NeedApproval("pid_compare", canonical, $"Save QC report: P&ID check of {info.Document} ({verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW)", QcReport.Markdown(info, findings)) is { } wait) return wait;
            var (xlsx, mdFile) = QcReport.Write(s, info, findings);
            saved = new { saved = true, excel = xlsx, markdown = mdFile, audit = Resolve.Audit("pid_compare", canonical, $"{verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW, {counts.Ok} OK") };
        }
        return new
        {
            redrawn = red, original = orig, sheets = rs.Count, verdict, counts,
            problems = findings.Where(f => f.Status is FindingStatus.Fail or FindingStatus.Review).OrderByDescending(f => f.Status).ThenBy(f => f.Subject, StringComparer.Ordinal).ThenBy(f => f.Check)
                .Select(f => new { status = FindingStatuses.Code(f.Status), sheet = f.Subject, check = f.Check, message = f.Message, redrawnValue = f.DrawingValue, originalValue = f.SourceValue, source = f.Source?.ToString() ?? "", where = f.Location }).ToList(),
            notVerified = findings.Where(f => f.Status == FindingStatus.Info).Select(f => f.Message).ToList(),
            ok = includeOk ? findings.Where(f => f.Status == FindingStatus.Ok).Select(f => (object)new { f.Check, f.Subject, f.Message }).ToList() : null,
            report = saved,
            next = apply ? "Tell the user the verdict and the report paths." : "Give the user the verdict, every FAIL (missing and changed, with both texts and the sheet and zone), then the REVIEWs, then the notVerified list. Offer to save the report (apply=true).",
        };
    }
}

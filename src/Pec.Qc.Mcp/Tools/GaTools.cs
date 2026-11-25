using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Ga;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;

namespace Pec.Qc.Mcp.Tools;

/// <summary>General arrangement (GA) drawings: reading them and checking them against the model and the project inputs.</summary>
[McpServerToolType]
public static class GaTools
{
    [McpServerTool(Name = "ga_read", ReadOnly = true)]
    [Description("Read a general arrangement (GA) drawing's text (a PDF's text layer, or the texts of a DXF / DWG): per sheet the equipment tags with their place, the E/N/U coordinate labels (converted to mm), the elevations, the scale, revision, whether a north arrow is there as text, and the number of notes. Dimensions and symbols are not read. A sheet with no text (a scan) is reported as unreadable. Use it to answer a question about the drawing or to see what ga_check was given.")]
    public static object Read([Description("Path of the GA PDF, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null, [Description("Only this sheet (page number)")] int? sheet = null)
    {
        var path = IsoTools.ResolvePdf(file, project);
        List<GaSheet> sheets;
        try { sheets = GaReader.Read(path); } catch (Exception e) when (e is not McpException) { throw new McpException($"The PDF cannot be read: {e.Message}"); }
        if (sheet is { } n) sheets = sheets.Where(s => s.Page == n).ToList();
        return new
        {
            file = path,
            sheets = sheets.Select(s => new
            {
                page = s.Page, readable = s.HasText, s.Warnings, s.Scale, s.Revision, northArrow = s.HasNorthArrow, notes = s.Notes,
                tags = s.Tags.Select(t => t.Text).Distinct().ToList(),
                lineNumbers = s.Lines.Select(l => new { text = l.Text, sizeIn = l.SizeIn, elevationMm = l.ElevationMm }).ToList(),
                references = s.References, matchlines = s.Matchlines, tierElevation = s.TierElevation, planRanges = s.PlanRanges,
                coordinates = s.Coords.Select(c => new { e_mm = Math.Round(c.E, 1), n_mm = Math.Round(c.N, 1), u_mm = Math.Round(c.U, 1), unit = c.Imperial ? "feet-inches" : "mm assumed", raw = c.Raw }).ToList(),
                elevations = s.Elevations.Select(e => new { mm = Math.Round(e.Mm, 1), unit = e.Imperial ? "feet-inches" : "mm assumed", raw = e.Raw }).ToList(),
            }).ToList(),
            next = "Give the user what they asked for. A sheet that is not readable was not checked.",
        };
    }

    [McpServerTool(Name = "ga_check", Destructive = true)]
    [Description("Check a general arrangement (GA) drawing PDF: equipment tags and E/N/U coordinates against the exported Navisworks model (needs model_export done with this version: equipment is in the snapshot), tags against the P&IDs and the vendor data of the project inputs, and the completeness of the drawing (scale, north arrow, revision). A tag one character away from a model tag, or a coordinate outside the equipment's extent in the model, is a FAIL with both values and the model item; REVIEW = a person must look (tag not found, an elevation outside the height range, no scale...). NOT checked and listed as such: dimensions and equipment sizes, nozzles against vendor data, clearances. apply=false only shows the result; apply=true asks the user to approve and saves the Excel + Markdown report.")]
    public static object Check([Description("Path of the GA PDF, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null,
        [Description("An exported model name (model_summary); empty = the newest")] string? model = null, [Description("A coordinate may be this far outside the equipment extent, in mm (default 100)")] double toleranceMm = 100,
        [Description("Also list what matched (long)")] bool includeOk = false, [Description("true = save the report (asks the user to approve)")] bool apply = false)
    {
        var path = IsoTools.ResolvePdf(file, project);
        var (snap, stored, card) = ModelTools.Load(project, model);
        IsoEvidence ev;
        try
        {
            var (lib, _) = Resolve.Inputs(project);
            var inv = lib.Inventory();
            InputsIndex? index = null;
            if (inv.Any(d => d.Kind is InputKind.Pid or InputKind.Vendor)) { index = new InputsIndex(lib); index.Refresh(); }
            ev = new IsoEvidence(LineListSet.Load(lib), index, inv);
        }
        catch (McpException) { ev = new IsoEvidence(); }
        List<GaSheet> sheets;
        try { sheets = GaReader.Read(path); } catch (Exception e) when (e is not McpException) { throw new McpException($"The PDF cannot be read: {e.Message}"); }
        var findings = new GaChecker(snap, stored.Name, new GaOptions { BoxToleranceMm = toleranceMm }, ev.Index is null && ev.Inventory is null ? null : ev).Check(sheets);
        var counts = QcReport.Counts(findings); var verdict = QcReport.Verdict(findings);
        var s = Resolve.Settings();
        var info = new QcReportInfo(card?.Name ?? "", QcType.Ga, Path.GetFileName(path), stored.Name, Resolve.Who().Display, DateTime.Now);
        var (xl, md) = QcReport.PathsFor(s, info);
        object saved = new { saved = false, wouldWrite = new { excel = xl, markdown = md } };
        if (apply)
        {
            var canonical = $"{info.Project}|ga|{info.Document}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FindingJson.ToJson(findings))))[..16]}";
            if (Resolve.NeedApproval("ga_check", canonical, $"Save QC report: GA check of {info.Document} ({verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW)", QcReport.Markdown(info, findings)) is { } wait) return wait;
            var (xlsx, mdFile) = QcReport.Write(s, info, findings);
            saved = new { saved = true, excel = xlsx, markdown = mdFile, audit = Resolve.Audit("ga_check", canonical, $"{verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW, {counts.Ok} OK") };
        }
        return new
        {
            file = path, model = stored.Name, sheets = sheets.Count, verdict, counts,
            problems = findings.Where(x => x.Status is FindingStatus.Fail or FindingStatus.Review).OrderByDescending(x => x.Status).ThenBy(x => x.Check)
                .Select(x => new { status = FindingStatuses.Code(x.Status), check = x.Check, subject = x.Subject, message = x.Message, drawing = x.DrawingValue, source = x.SourceValue, sourceDoc = x.Source?.ToString() ?? "", where = x.Location }).ToList(),
            notVerified = findings.Where(x => x.Status == FindingStatus.Info).Select(x => x.Message).Distinct().ToList(),
            ok = includeOk ? findings.Where(x => x.Status == FindingStatus.Ok).Select(x => (object)new { x.Check, x.Subject, x.Message, drawing = x.DrawingValue, source = x.SourceValue }).ToList() : null,
            report = saved,
            next = apply ? "Tell the user the verdict and the report paths." : "Give the user the verdict, every FAIL with both values and the model item, then the REVIEWs and the notVerified list. Offer to save the report (apply=true).",
        };
    }
}

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Checking piping isometrics against the exported Navisworks model and the project inputs.</summary>
public static partial class IsoTools
{
    /// <summary>The line list, the PMS / P&amp;ID index and the inventory of the project's inputs. Empty when the project has no inputs folder: the checks then say what they could not compare.</summary>
    private static IsoEvidence BuildEvidence(string? project)
    {
        try
        {
            var (lib, _) = Resolve.Inputs(project);
            var inv = lib.Inventory();
            InputsIndex? index = null;
            if (inv.Any(d => d.Kind is InputKind.Pms or InputKind.Pid)) { index = new InputsIndex(lib); index.Refresh(); }
            return new IsoEvidence(LineListSet.Load(lib), index, inv);
        }
        catch (McpException) { return new IsoEvidence(); }
    }

    private static object Describe(IsoLineResult r, bool includeOk) => new
    {
        line = r.LineNo, file = r.File, sheets = r.Sheets, verdict = r.Verdict, counts = QcReport.Counts(r.Findings),
        problems = r.Findings.Where(f => f.Status is FindingStatus.Fail or FindingStatus.Review).OrderByDescending(f => f.Status).ThenBy(f => f.Check)
            .Select(f => new { status = FindingStatuses.Code(f.Status), check = f.Check, message = f.Message, drawing = f.DrawingValue, source = f.SourceValue, sourceDoc = f.Source?.ToString() ?? "", where = f.Location }).ToList(),
        notVerified = r.Findings.Where(f => f.Status == FindingStatus.Info).Select(f => f.Message).ToList(),
        ok = includeOk ? r.Findings.Where(f => f.Status == FindingStatus.Ok).Select(f => (object)new { check = f.Check, message = f.Message, drawing = f.DrawingValue, source = f.SourceValue }).ToList() : null,
    };

    private static (List<IsoLineResult> Results, string Model, ProjectCard? Project, string RulesSource) Run(string path, string? project, string? model, int? sheet, IsoEvidence ev)
    {
        var (snap, stored, card) = ModelTools.Load(project, model);
        var rules = IsoRules.Load(Resolve.Settings().EffectiveRequirementsDirectory, out var rulesSource);
        List<IsoSheet> sheets;
        try { sheets = IsoReader.Read(path); }
        catch (Exception e) when (e is not McpException) { throw new McpException($"The PDF cannot be read: {e.Message}"); }
        if (sheet is { } n) sheets = sheets.Where(s => s.Page == n).ToList();
        return (new IsoChecker(snap, stored.Name, rules, ev).CheckPdf(sheets), stored.Name, card, rulesSource);
    }

    /// <summary>With apply=true, asks for approval and writes the report. Returns the "waiting" result when approval is pending, else null.</summary>
    private static object? SaveReport(string path, ProjectCard? card, string modelName, List<IsoLineResult> results, bool apply, out object preview)
    {
        var all = results.SelectMany(r => r.Findings).ToList();
        var s = Resolve.Settings();
        var info = new QcReportInfo(card?.Name ?? "", QcType.Isometric, Path.GetFileName(path), modelName, Resolve.Who().Display, DateTime.Now);
        var (xl, md) = QcReport.PathsFor(s, info);
        var counts = QcReport.Counts(all); var verdict = QcReport.Verdict(all);
        preview = new { saved = false, wouldWrite = new { excel = xl, markdown = md } };
        if (!apply) return null;
        var canonical = $"{info.Project}|iso|{info.Document}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FindingJson.ToJson(all))))[..16]}";
        if (Resolve.NeedApproval("iso_check", canonical, $"Save QC report: isometric check of {info.Document} ({verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW)", QcReport.Markdown(info, all)) is { } wait) return wait;
        var (xlsx, mdFile) = QcReport.Write(s, info, all);
        var audit = Resolve.Audit("iso_check", canonical, $"{verdict}: {counts.Fail} FAIL, {counts.Review} REVIEW, {counts.Ok} OK");
        preview = new { saved = true, excel = xlsx, markdown = mdFile, audit };
        return null;
    }

    [McpServerTool(Name = "iso_check", Destructive = true)]
    [Description("Check a piping isometric PDF against the exported Navisworks model and the project inputs. For every line on the drawing: identity (line in the model, duplicates, sheets 1..n, revisions, CONT. links, class and size in the line number), title block against the model, BOM against the model components (quantities and sizes by type), coordinates against the model points and connected lines, cut pipe lengths and total pipe length, and the line list, PMS class and P&ID where the project has them. A FAIL has the drawing value, the source value and the source (model, line list, PMS page...); REVIEW = a person must look; notVerified lists what could NOT be checked (never read it as a pass). Needs an exported model (model_summary). apply=false only checks and shows the result; apply=true also asks the user to approve and saves the Excel + Markdown report to the reports folder. Then answer with the verdict, every FAIL and REVIEW with both values, and the notVerified list (skill qc-iso).")]
    public static object Check([Description("Path of the isometric PDF, or its file name inside the project's inputs folder")] string file, [Description("Project name; empty = the active project")] string? project = null,
        [Description("An exported model name (model_summary); empty = the newest")] string? model = null, [Description("Only this sheet (page number); empty = all")] int? sheet = null,
        [Description("Also list what matched (long)")] bool includeOk = false, [Description("true = save the report (asks the user to approve)")] bool apply = false)
    {
        var path = ResolvePdf(file, project);
        var ev = BuildEvidence(project);
        var (results, modelName, card, rulesSource) = Run(path, project, model, sheet, ev);
        var wait = SaveReport(path, card, modelName, results, apply, out var saved);
        if (wait is not null) return wait;
        return new
        {
            file = path, model = modelName, rules = rulesSource, lines = results.Select(r => Describe(r, includeOk)).ToList(), report = saved,
            next = apply ? "Tell the user the verdict and the report paths." : "Give the user the verdict, every FAIL and REVIEW with both values and the source, and the notVerified list. Offer to save the report (apply=true).",
        };
    }

    [McpServerTool(Name = "iso_check_folder", Destructive = true)]
    [Description("Check every isometric PDF in a folder (not its sub-folders unless recursive) with iso_check and give one summary row per line: verdict and the counts of FAIL / REVIEW. Use iso_check on a single drawing to see its findings. apply=true asks the user to approve and saves one report per PDF.")]
    public static object CheckFolder([Description("Folder with the isometric PDFs")] string folder, [Description("Project name; empty = the active project")] string? project = null,
        [Description("An exported model name; empty = the newest")] string? model = null, bool recursive = false, [Description("At most this many PDFs (default 50)")] int max = 50, bool apply = false)
    {
        var dir = (folder ?? "").Trim().Trim('"');
        if (!Directory.Exists(dir)) throw new McpException($"The folder does not exist: {dir}");
        var pdfs = Directory.EnumerateFiles(dir, "*.pdf", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (pdfs.Count == 0) return new { checkedPdfs = 0, message = "There is no PDF in that folder.", next = "Tell the user." };
        var limit = Math.Clamp(max, 1, 500);
        var ev = BuildEvidence(project);
        var rows = new List<object>(); var unreadable = new List<string>(); var reports = new List<object>();
        var total = new Dictionary<string, int>();
        string? modelName = null, rulesSource = null;
        foreach (var path in pdfs.Take(limit))
        {
            List<IsoLineResult> results; string mn; ProjectCard? card; string rs;
            try { (results, mn, card, rs) = Run(path, project, model, null, ev); }
            catch (McpException e) when (!e.Message.Contains("exported model", StringComparison.Ordinal) && !e.Message.StartsWith("There is no project", StringComparison.Ordinal)) { unreadable.Add($"{Path.GetFileName(path)}: {e.Message}"); continue; }
            modelName = mn; rulesSource = rs;
            foreach (var r in results)
            {
                var c = QcReport.Counts(r.Findings);
                total[r.Verdict] = total.GetValueOrDefault(r.Verdict) + 1;
                rows.Add(new { file = r.File, line = r.LineNo, sheets = r.Sheets, verdict = r.Verdict, fail = c.Fail, review = c.Review, ok = c.Ok, notVerified = c.Info });
            }
            var wait = SaveReport(path, card, mn, results, apply, out var saved);
            if (wait is not null) return wait;
            if (apply) reports.Add(saved);
        }
        return new
        {
            folder = dir, model = modelName, rules = rulesSource, pdfs = pdfs.Count, checkedPdfs = Math.Min(pdfs.Count, limit), verdicts = total, lines = rows, couldNotRead = unreadable, reports = apply ? reports : null,
            next = "Give the user the verdict counts, the lines with FAIL first, and anything that could not be read. Offer iso_check on a single drawing for the details.",
        };
    }
}


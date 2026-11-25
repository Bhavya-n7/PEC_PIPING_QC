using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Pilot;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

/// <summary>The pilot: what the engineers say about the findings, how long the checks took, and how the assistant compares with the team's own result.</summary>
[McpServerToolType]
public static class PilotTools
{
    [McpServerTool(Name = "finding_feedback")]
    [Description("PILOT: record what the engineer decided about a finding AFTER reviewing it on the drawing: confirmed (the finding is a real problem), false-alarm (the finding is wrong or not a problem), or missed (the engineer found a real problem that the assistant did not report). Ask the engineer for each FAIL and REVIEW they looked at, and for anything they found that was not reported. Give the qc type, the drawing, the line or tag, the check (title-block, bom, coordinates, lengths, identity, line-list, pms, pid, equipment, tags, drawing...) and a short note with the reason. These records are how the pilot measures accuracy; for a false alarm also use learn_record with the rule behind it.")]
    public static object Feedback([Description("iso, pid or ga")] string qcType, [Description("The drawing file name")] string document, [Description("The line number, tag or item")] string subject,
        [Description("confirmed | false-alarm | missed")] string verdict, [Description("The check the finding belongs to")] string check = "", [Description("Why: one or two sentences, no passwords")] string note = "")
    {
        var type = QcTypes.Parse(qcType) ?? throw new McpException("qcType must be iso, pid or ga.");
        if (string.IsNullOrWhiteSpace(document)) throw new McpException("Say which drawing it was about.");
        try
        {
            var r = PilotStore.RecordFeedback(Resolve.Settings(), Resolve.Who(), QcTypes.Code(type), Path.GetFileName(document.Trim()), subject.Trim(), check.Trim(), verdict, note);
            return new { recorded = true, r.Verdict, r.Subject, next = r.Verdict == "false-alarm" ? "Thank the engineer. Ask for the rule behind it and record it with learn_record so the QC lead can approve it." : "Thank the engineer." };
        }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "pilot_time")]
    [Description("PILOT: record how long a check took with the assistant and what the engineer estimates it takes by hand for the same drawing (minutes). Ask the engineer once per drawing, at the end.")]
    public static object Time([Description("iso, pid or ga")] string qcType, [Description("The drawing file name")] string document, [Description("Minutes with the assistant, including reading the result")] double minutesWithTool, [Description("Estimated minutes by hand")] double minutesManualEstimate)
    {
        var type = QcTypes.Parse(qcType) ?? throw new McpException("qcType must be iso, pid or ga.");
        try { var r = PilotStore.RecordTime(Resolve.Settings(), Resolve.Who(), QcTypes.Code(type), Path.GetFileName(document.Trim()), minutesWithTool, minutesManualEstimate); return new { recorded = true, savedMinutes = r.MinutesManual - r.MinutesWithTool }; }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "pilot_metrics", ReadOnly = true)]
    [Description("QC LEAD: the pilot's numbers for the last days: per QC type how many findings engineers confirmed, called false alarms, and reported as missed (precision = confirmed / (confirmed + false alarms); recall = confirmed / (confirmed + missed)), the time with the assistant against the manual estimate, which checks give the most false alarms and misses, the installs (how many worked, the failed ones) and how many reports were saved. These are what engineers reported, not a measurement: say how many records there are before quoting a percentage.")]
    public static object Metrics([Description("How many days back, 1-3650")] int days = 30)
    {
        if (Resolve.Require(QcRole.Lead, "Reading the pilot's numbers") is { } denied) return denied;
        var s = PilotStore.Summarize(Resolve.Settings(), days);
        return new
        {
            days = s.Days, reportsSaved = s.ReportsSaved,
            byType = s.ByType.Select(t => new { t.QcType, t.Confirmed, t.FalseAlarms, t.Missed, precision = Pct(t.Precision), recall = Pct(t.Recall), timeRecords = t.TimeRecords, avgMinutesWithTool = Round(t.AvgMinutesWithTool), avgMinutesManual = Round(t.AvgMinutesManual), minutesSaved = Round(t.MinutesSaved), percentSaved = Round(t.PercentSaved) }).ToList(),
            falseAlarmsByCheck = s.FalseAlarmsByCheck.Select(x => new { check = x.Check, count = x.FalseAlarms }).ToList(), missedByCheck = s.MissedByCheck.Select(x => new { check = x.Check, count = x.Missed }).ToList(),
            installs = new { total = s.Installs, ok = s.InstallsOk, failed = s.FailedInstalls.Select(i => new { i.Ts, i.Machine, i.Version, i.Summary }).ToList() },
            next = "Give the numbers with how many records they rest on. A false-alarm check that shows up often needs a rule or a fix; a missed check shows where the assistant is blind.",
        };
    }

    private static string? Pct(double? v) => v is null ? null : $"{v * 100:0}%";
    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);

    [McpServerTool(Name = "pilot_compare", ReadOnly = true)]
    [Description("PILOT: compare the assistant's saved report of a drawing with the QC team's own hand list for the same drawing: how many of the hand findings the assistant reported as a FAIL (found), pointed at as a REVIEW (flagged) or did not report (missed), and which FAILs are not on the hand list (the team may have missed them or the assistant is wrong: a person judges each). The hand list is an Excel or CSV file with the columns Drawing, Subject (line or tag), Check and Description (only Description is required). This is the pilot's exit measure.")]
    public static object Compare([Description("The assistant's saved report (.xlsx), or the drawing's file name to take its newest report")] string report, [Description("The team's hand list (.xlsx or .csv)")] string handList, [Description("Project name; empty = the active project")] string? project = null)
    {
        var path = report.Trim().Trim('"');
        if (!File.Exists(path))
        {
            var card = string.IsNullOrWhiteSpace(project) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
            var dir = QcReport.FolderFor(Resolve.Settings(), card?.Name ?? project?.Trim() ?? "");
            var stem = Path.GetFileNameWithoutExtension(path);
            path = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.xlsx").Where(f => Path.GetFileName(f).Contains(stem, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f).FirstOrDefault() ?? "" : "";
            if (path.Length == 0) throw new McpException($"No saved report found for '{report}'. Give the full path of the report (.xlsx), or save one with iso_check / pid_compare / ga_check (apply=true).");
        }
        var hand = handList.Trim().Trim('"');
        if (!File.Exists(hand)) throw new McpException($"The hand list does not exist: {hand}");
        SavedReport rep; IReadOnlyList<HandFinding> list;
        try { rep = HandComparison.ReadReport(path); list = HandComparison.ReadHandList(hand); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or KeyNotFoundException) { throw new McpException("Cannot read the files: " + e.Message); }
        var a = HandComparison.Compare(list, rep);
        string P(double? v) => v is null ? "n/a" : $"{v * 100:0}%";
        return new
        {
            report = rep.File, drawing = rep.Document, verdict = rep.Verdict, handFindings = a.HandCount, found = a.Found, flagged = a.Flagged, missed = a.Missed,
            strictRecall = P(a.StrictRecall), recall = P(a.Recall), failsReported = a.FailTotal, failPrecision = P(a.FailPrecision),
            perHandFinding = a.Matches.Select(m => new { hand = m.Hand.Description, subject = m.Hand.Subject, outcome = m.Outcome.ToString().ToLowerInvariant(), assistant = m.Tool is null ? null : $"{FindingStatuses.Code(m.Tool.Status)}: {m.Tool.Message}" }).ToList(),
            failsNotOnTheHandList = a.ExtraFails.Select(f => new { f.Subject, f.Check, f.Message, drawing = f.DrawingValue, source = f.SourceValue }).ToList(),
            reviewsNotOnTheHandList = a.ExtraReviews.Count,
            next = "Give the numbers with how many hand findings they rest on, list the missed ones, and for each FAIL that is not on the hand list ask the engineer whether it is real (record with finding_feedback). One drawing is an anecdote, not a rate.",
        };
    }
}

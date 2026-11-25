using System.Text.Json;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Core.Pilot;

public sealed record FeedbackRecord(string Ts, string User, string EmployeeCode, string QcType, string Document, string Subject, string Check, string Verdict, string Note);
public sealed record TimeRecord(string Ts, string User, string EmployeeCode, string QcType, string Document, double MinutesWithTool, double MinutesManual);
public sealed record InstallRecord(string Ts, string Machine, string Version, bool Ok, string Summary);

public sealed record TypeMetrics(string QcType, int Confirmed, int FalseAlarms, int Missed, double? Precision, double? Recall, int TimeRecords, double? AvgMinutesWithTool, double? AvgMinutesManual, double? MinutesSaved, double? PercentSaved);
public sealed record PilotSummary(int Days, IReadOnlyList<TypeMetrics> ByType, IReadOnlyList<(string Check, int FalseAlarms)> FalseAlarmsByCheck, IReadOnlyList<(string Check, int Missed)> MissedByCheck, int Installs, int InstallsOk, IReadOnlyList<InstallRecord> FailedInstalls, int ReportsSaved);

/// <summary>
/// What the pilot measures, kept as small JSON lines on the shared folder (<c>pilot\feedback</c>, <c>pilot\time</c>, <c>pilot\installs</c>): did an engineer confirm a finding, call it a false
/// alarm, or report something the assistant missed; how long did the check take against the manual estimate; did the install work. Written like the other logs (append only; kept on
/// this PC while the share is away and sent back later).
/// </summary>
public static class PilotStore
{
    public static readonly string[] Verdicts = ["confirmed", "false-alarm", "missed"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static string Root(Settings s) => s.HasShared ? Path.Combine(s.SharedRoot!, "pilot") : Path.Combine(Settings.DataDirectory, "pilot");

    private static void Append(Settings s, string kind, string file, object row)
    {
        var line = JsonSerializer.Serialize(row, Json) + "\n";
        foreach (var root in new[] { Root(s), Path.Combine(Settings.DataDirectory, "pilot") }.Distinct())
        {
            try
            {
                var dir = Path.Combine(root, kind, DateTime.UtcNow.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, file), line);
                return;                      // written; the local copy is only for an unreachable share
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PathDiagnostics.Note("pilot", "write failed", root, e.Message); }
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("s") + "Z";
    private static string FileFor(UserIdentity who) => $"{who.FileKey}-{Environment.MachineName}.jsonl";

    public static FeedbackRecord RecordFeedback(Settings s, UserIdentity who, string qcType, string document, string subject, string check, string verdict, string note)
    {
        var v = verdict.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (v is "false" or "falsealarm" or "wrong") v = "false-alarm";
        if (v is "ok" or "correct" or "accepted" or "true") v = "confirmed";
        if (!Verdicts.Contains(v)) throw new ArgumentException("verdict must be confirmed, false-alarm or missed.");
        var r = new FeedbackRecord(Now(), who.Name, who.EmployeeCode, qcType, document, subject, check, v, LessonStore_Scrub(note));
        Append(s, "feedback", FileFor(who), r);
        return r;
    }

    public static TimeRecord RecordTime(Settings s, UserIdentity who, string qcType, string document, double withTool, double manual)
    {
        if (withTool < 0 || manual < 0 || withTool > 24 * 60 || manual > 24 * 60) throw new ArgumentException("Minutes must be between 0 and 1440.");
        var r = new TimeRecord(Now(), who.Name, who.EmployeeCode, qcType, document, withTool, manual);
        Append(s, "time", FileFor(who), r);
        return r;
    }

    public static void RecordInstall(Settings s, string version, bool ok, string summary) =>
        Append(s, "installs", $"{Environment.MachineName}.jsonl", new InstallRecord(Now(), Environment.MachineName, version, ok, LessonStore_Scrub(summary)));

    private static string LessonStore_Scrub(string? t) => Knowledge.LessonStore.Scrub(t);

    private static List<T> ReadAll<T>(Settings s, string kind, DateTime sinceUtc, Func<T, string> ts)
    {
        var all = new List<T>();
        foreach (var root in new[] { Root(s), Path.Combine(Settings.DataDirectory, "pilot") }.Distinct())
        {
            var dir = Path.Combine(root, kind);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    foreach (var line in File.ReadLines(f))
                        try { if (JsonSerializer.Deserialize<T>(line, Json) is { } r && DateTime.TryParse(ts(r), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) && t >= sinceUtc) all.Add(r); }
                        catch (JsonException) { }
                }
                catch (IOException) { }
            }
        }
        return all;
    }

    public static PilotSummary Summarize(Settings s, int days)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 3650));
        var fb = ReadAll<FeedbackRecord>(s, "feedback", since, r => r.Ts);
        var tm = ReadAll<TimeRecord>(s, "time", since, r => r.Ts);
        var ins = ReadAll<InstallRecord>(s, "installs", since, r => r.Ts);
        var types = fb.Select(f => f.QcType).Concat(tm.Select(t => t.QcType)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var by = types.Select(t =>
        {
            var f = fb.Where(x => x.QcType.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
            var c = f.Count(x => x.Verdict == "confirmed"); var fa = f.Count(x => x.Verdict == "false-alarm"); var m = f.Count(x => x.Verdict == "missed");
            var tt = tm.Where(x => x.QcType.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList();
            double? saved = tt.Count == 0 ? null : tt.Sum(x => x.MinutesManual - x.MinutesWithTool);
            var manual = tt.Sum(x => x.MinutesManual);
            return new TypeMetrics(t, c, fa, m, c + fa == 0 ? null : (double)c / (c + fa), c + m == 0 ? null : (double)c / (c + m), tt.Count,
                tt.Count == 0 ? null : tt.Average(x => x.MinutesWithTool), tt.Count == 0 ? null : tt.Average(x => x.MinutesManual), saved, manual <= 0 || saved is null ? null : saved / manual * 100);
        }).ToList();
        var reports = 0;
        try { var rd = s.EffectiveReportsDirectory; if (Directory.Exists(rd)) reports = Directory.GetFiles(rd, "*.xlsx", SearchOption.AllDirectories).Count(f => File.GetLastWriteTimeUtc(f) >= since); } catch (IOException) { }
        return new PilotSummary(days, by,
            fb.Where(x => x.Verdict == "false-alarm").GroupBy(x => x.Check.Length > 0 ? x.Check : "(not stated)").OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).ToList(),
            fb.Where(x => x.Verdict == "missed").GroupBy(x => x.Check.Length > 0 ? x.Check : "(not stated)").OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).ToList(),
            ins.Count, ins.Count(i => i.Ok), ins.Where(i => !i.Ok).ToList(), reports);
    }
}

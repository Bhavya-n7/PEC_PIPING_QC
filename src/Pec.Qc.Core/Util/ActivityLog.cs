using System.Text.Json;
using System.Text.RegularExpressions;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Core.Util;

public sealed record ActivityRow(string Ts, string User, string EmployeeCode, string Company, string Machine, string ChatId, string Project, string Type, string Text, string Tool, bool? Ok, string Detail);

/// <summary>
/// The team's activity log: what each user asked the assistant and what it did (their prompts, its answers, every tool it used and how that ended). One line per
/// event in <c>&lt;shared&gt;\activity\yyyy-MM\&lt;employee code&gt;-&lt;machine&gt;.jsonl</c> (this PC's own folder when there is no shared folder). It is for support,
/// accuracy follow-up and learning; passwords and keys are scrubbed and long texts are cut.
/// </summary>
public static class ActivityLog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static string Directory_(Settings s) => s.HasShared ? Path.Combine(s.SharedRoot!, "activity") : Path.Combine(Settings.DataDirectory, "activity");

    private static int Cap(string type) => type switch { "prompt" => 4000, "answer" => 2500, _ => 700 };

    public static void Write(Settings s, UserIdentity who, string type, string text, string? tool = null, bool? ok = null, string? chatId = null, string? project = null, string? detail = null)
    {
        var row = new ActivityRow(DateTime.UtcNow.ToString("s") + "Z", who.Name, who.EmployeeCode, who.Company, Environment.MachineName, chatId ?? "", project ?? "", type,
            Trunc(LessonStore.Scrub(text), Cap(type)), tool ?? "", ok, Trunc(LessonStore.Scrub(detail ?? ""), 700));
        var line = JsonSerializer.Serialize(row, Json) + "\n";
        var file = $"{who.FileKey}-{Environment.MachineName}.jsonl";
        foreach (var root in new[] { Directory_(s), Path.Combine(Settings.DataDirectory, "activity") }.Distinct())
        {
            try
            {
                var dir = Path.Combine(root, DateTime.UtcNow.ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, file), line);
                return;                                           // written; the local fallback is only for an unreachable share
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PathDiagnostics.Note("activity", "write failed", root, e.Message); }
        }
    }

    private static string Trunc(string t, int max) => t.Length <= max ? t : t[..max] + "…";

    /// <summary>Everything logged since a date, newest first, optionally for one person (name or employee code) or containing some words.</summary>
    public static IReadOnlyList<ActivityRow> Read(Settings s, DateTime sinceUtc, string? who = null, string? contains = null, string? type = null, int limit = 200)
    {
        var root = Directory_(s);
        if (!Directory.Exists(root)) return [];
        var rows = new List<ActivityRow>();
        foreach (var f in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    try
                    {
                        var r = JsonSerializer.Deserialize<ActivityRow>(line, Json);
                        if (r is null || !DateTime.TryParse(r.Ts, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) || t < sinceUtc) continue;
                        rows.Add(r);
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
        }
        IEnumerable<ActivityRow> q = rows;
        if (!string.IsNullOrWhiteSpace(who)) q = q.Where(r => r.User.Contains(who, StringComparison.OrdinalIgnoreCase) || r.EmployeeCode.Equals(who, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(contains)) q = q.Where(r => (r.Text + " " + r.Detail + " " + r.Tool).Contains(contains, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        return q.OrderByDescending(r => r.Ts).Take(Math.Clamp(limit, 1, 2000)).ToList();
    }
}

/// <summary>Does a message tell the assistant it was wrong or not accurate? Those messages are marked in the log and always followed by a reflection.</summary>
public static class Corrections
{
    private static readonly Regex Looks = new(
        @"\b(wrong|incorrect|not correct|not right|not accurate|inaccurate|not matching|doesn.t match|does not match|mistake|not what i (asked|wanted|said)|that.s not|should (be|not|have)|shouldn.t|you (missed|forgot|didn.t)|fix (this|it|that)|still (wrong|not)|again\b.*\bnot|redo|try again|why (did|is|are) (it|you)|not working|failed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeCorrection(string? text) => !string.IsNullOrWhiteSpace(text) && Looks.IsMatch(text);
}

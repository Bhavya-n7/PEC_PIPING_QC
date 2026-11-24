namespace Pec.Qc.Core.Util;

/// <summary>One row of the activity report: a person on a day.</summary>
public sealed record ActivityDay(string Day, string User, string EmployeeCode, int Prompts, int Corrections, int ToolCalls, int ToolErrors, int Errors, int Chats, IReadOnlyList<string> TopTools);

/// <summary>The activity log as a table: per day and person, what was asked, how often the assistant was corrected, and where tools failed (where it was inaccurate).</summary>
public static class ActivitySummary
{
    public static IReadOnlyList<ActivityDay> ByDay(IEnumerable<ActivityRow> rows) =>
        rows.GroupBy(r => (Day: r.Ts.Length >= 10 ? r.Ts[..10] : r.Ts, r.User, r.EmployeeCode))
            .OrderByDescending(g => g.Key.Day).ThenBy(g => g.Key.User, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ActivityDay(g.Key.Day, g.Key.User, g.Key.EmployeeCode,
                g.Count(r => r.Type == "prompt"),
                g.Count(r => r.Type == "prompt" && r.Detail.Contains("correction", StringComparison.OrdinalIgnoreCase)),
                g.Count(r => r.Type == "tool"),
                g.Count(r => r.Type == "result" && r.Ok == false),
                g.Count(r => r.Type == "error"),
                g.Where(r => r.ChatId.Length > 0).Select(r => r.ChatId).Distinct().Count(),
                g.Where(r => r.Type == "tool" && r.Tool.Length > 0).GroupBy(r => r.Tool).OrderByDescending(t => t.Count()).Take(3).Select(t => $"{t.Key} x{t.Count()}").ToList()))
            .ToList();

    public static IReadOnlyList<(string Type, int Count)> ByType(IEnumerable<ActivityRow> rows) =>
        rows.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).ToList();
}

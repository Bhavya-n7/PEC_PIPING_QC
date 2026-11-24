using System.ComponentModel;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Users;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class ActivityTools
{
    [McpServerTool(Name = "who_am_i", ReadOnly = true)]
    [Description("The name, employee code and company of the person using the assistant on this PC (asked once at first start). Everything is logged against it.")]
    public static object WhoAmI()
    {
        var s = Resolve.Settings();
        var w = UserIdentity.Current(s);
        var rec = UserRegistry.Touch(s, w);
        return new { w.Name, w.EmployeeCode, w.Company, complete = w.IsComplete, role = rec.Role, windowsUser = Environment.UserName, machine = Environment.MachineName };
    }

    [McpServerTool(Name = "activity_report", ReadOnly = true)]
    [Description("The team's activity log: what each user asked the assistant and what it did (prompts, answers, every tool used and how it ended), with name, employee code and company. For support, accuracy follow-up and learning: ask for one person (name or employee code), a number of days, words that appear in it, or only a type: prompt, answer, tool, result, done, error, approval, learned, stopped. Prompts that look like corrections are marked. Returns a summary per person and the latest lines.")]
    public static object Report(
        [Description("A name or an employee code; empty = everyone")] string? who = null,
        [Description("How many days back, 1-365")] int days = 7,
        [Description("Words that must appear in the text, tool name or detail")] string? contains = null,
        [Description("Only this type of line")] string? type = null,
        [Description("How many lines to return, 1-300")] int limit = 60)
    {
        var s = Resolve.Settings();
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
        var all = ActivityLog.Read(s, since, who, contains, type, 2000);
        var people = all.GroupBy(r => (r.User, r.EmployeeCode)).Select(g => new
        {
            user = g.Key.User, employeeCode = g.Key.EmployeeCode, company = g.First().Company,
            prompts = g.Count(r => r.Type == "prompt"),
            corrections = g.Count(r => r.Type == "prompt" && r.Detail.Contains("correction")),
            toolCalls = g.Count(r => r.Type == "tool"),
            toolErrors = g.Count(r => r.Type == "result" && r.Ok == false),
            lastSeen = g.Max(r => r.Ts),
        }).OrderByDescending(p => p.prompts).ToList();
        return new
        {
            since = since.ToString("yyyy-MM-dd"), lines = all.Count, people,
            latest = all.Take(Math.Clamp(limit, 1, 300)).Select(r => new { r.Ts, r.User, r.EmployeeCode, chat = r.ChatId, r.Type, r.Tool, r.Ok, text = r.Text.Length > 300 ? r.Text[..300] + "…" : r.Text, r.Detail }),
            location = ActivityLog.Directory_(s),
            note = all.Count == 0 ? "Nothing logged for that selection." : "Look at corrections and tool errors: they are where the assistant was inaccurate. Record the lesson with learn_record and, if a tool was wrong, fix and save it again.",
        };
    }
}

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class LearnTools
{
    private static LessonStore Store() => new(Resolve.Settings());

    [McpServerTool(Name = "learn_record")]
    [Description("SELF-LEARNING: store a lesson in the company-wide learning store (central shared folder when configured). Call it whenever the user CORRECTS you, states a preference, or you find a gotcha, a recipe that worked or a failure. kind = correction|success|failure|preference|gotcha|recipe. One or two generic sentences: no client names, never passwords or keys (they are redacted).")]
    public static object Record([Description("correction|success|failure|preference|gotcha|recipe")] string kind, string text, string tool = "", string context = "", string outcome = "")
    {
        try { var (l, file, central) = Store().Record(kind, text, tool, context, outcome); return new { recorded = l.Id, store = file, central }; }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "learn_search", ReadOnly = true)]
    [Description("Search the approved company lessons. Call it BEFORE a risky or repeated task (modeling, conversion, delete, catalog).")]
    public static object Search(string query, int limit = 10) => Store().Search(query, Math.Clamp(limit, 1, 25));

    [McpServerTool(Name = "learn_pull", ReadOnly = true)]
    [Description("The curated company-wide LEARNED.md. Read it at the start of a session and apply it before generic advice.")]
    public static string Pull() { var t = Store().Pull(); return t.Length > 0 ? t : "(no company LEARNED.md yet)"; }

    [McpServerTool(Name = "learn_pending", ReadOnly = true)]
    [Description("QC LEAD: the lessons the team has recorded that nobody has approved or rejected yet, strongest first (strong = several different people recorded the same thing; corrections come before the rest). Show them to the user with their ids, who recorded them and the context, and ask which to approve into the company list and which to reject. Lessons are already usable by everyone; approving makes them permanent in LEARNED.md, rejecting hides them from everyone.")]
    public static object Pending([Description("How many, 1-100")] int limit = 30)
    {
        if (Resolve.Require(QcRole.Lead, "Reviewing the team's lessons") is { } denied) return denied;
        var list = new LessonCurator(Resolve.Settings()).Pending();
        return new
        {
            count = list.Count,
            lessons = list.Take(Math.Clamp(limit, 1, 100)).Select(p => new { id = p.Lesson.Id, kind = p.Lesson.Kind, strong = p.Strong, people = p.People, by = p.Lesson.User, p.Lesson.EmployeeCode, when = p.Lesson.Ts, tool = p.Lesson.Tool, text = p.Lesson.Text, context = p.Lesson.Context, outcome = p.Lesson.Outcome }).ToList(),
            next = list.Count == 0 ? "Nothing to review." : "Show them to the user and ask which ids to approve and which to reject (learn_review). Never approve on your own.",
        };
    }

    [McpServerTool(Name = "learn_review", Destructive = true)]
    [Description("QC LEAD: approve or reject lessons by id (from learn_pending). Approve = permanent in the company list and in LEARNED.md (the previous LEARNED.md is kept); reject = hidden from everyone. apply=false shows what would happen; apply=true asks the user to approve and does it. The decision is logged with who made it.")]
    public static object Review([Description("approve or reject")] string decision, [Description("Lesson ids, comma separated")] string ids, [Description("Why (for a rejection)")] string reason = "", bool apply = false)
    {
        if (Resolve.Require(QcRole.Lead, "Approving or rejecting a lesson") is { } denied) return denied;
        var approve = decision.Trim().Equals("approve", StringComparison.OrdinalIgnoreCase);
        if (!approve && !decision.Trim().Equals("reject", StringComparison.OrdinalIgnoreCase)) throw new McpException("decision must be approve or reject.");
        var list = ids.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).Distinct().ToList();
        if (list.Count == 0) throw new McpException("Give the lesson ids (from learn_pending).");
        var cur = new LessonCurator(Resolve.Settings());
        var pending = cur.Pending();
        var known = list.Where(i => pending.Any(p => p.Lesson.Id.Equals(i, StringComparison.OrdinalIgnoreCase))).ToList();
        var unknown = list.Except(known, StringComparer.OrdinalIgnoreCase).ToList();
        if (!apply)
            return new { applied = false, decision = approve ? "approve" : "reject", would = pending.Where(p => known.Contains(p.Lesson.Id, StringComparer.OrdinalIgnoreCase)).Select(p => new { id = p.Lesson.Id, text = p.Lesson.Text }).ToList(), notPending = unknown, next = "Show the user the lessons and call again with apply=true to do it." };
        var canonical = $"{(approve ? "approve" : "reject")}|{string.Join(",", known.OrderBy(x => x))}";
        if (Resolve.NeedApproval("learn_review", canonical, $"{(approve ? "Approve" : "Reject")} {known.Count} lesson(s) {(approve ? "into the company list" : "")}", string.Join("\n", pending.Where(p => known.Contains(p.Lesson.Id, StringComparer.OrdinalIgnoreCase)).Select(p => "- " + p.Lesson.Text))) is { } wait) return wait;
        var who = Resolve.Who().Display;
        var r = approve ? cur.Approve(known, who) : cur.Reject(known, who, reason);
        var audit = Resolve.Audit("learn_review", canonical, approve ? $"approved {r.Approved}" : $"rejected {r.Rejected}");
        return new { applied = true, approved = r.Approved, rejected = r.Rejected, notFound = r.NotFound.Concat(unknown).Distinct().ToList(), learnedFile = r.LearnedFile, audit, next = "Tell the user what was done." };
    }

    [McpServerTool(Name = "sync_offline", ReadOnly = false)]
    [Description("Send the lessons and activity that were kept on this PC while the shared folder was away to the shared folder. It also runs by itself at start; call it when the user asks whether everything has reached the share.")]
    public static object SyncOffline()
    {
        var r = OfflineQueue.Sync(Resolve.Settings());
        return new { r.SharedReachable, r.Files, r.Lines, alreadyThere = r.Skipped, r.Problems, message = r.ToString() };
    }
}

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Knowledge;

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
}

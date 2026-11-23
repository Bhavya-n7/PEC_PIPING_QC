using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class KnowledgeTools
{
    private static KnowledgeStore Store() => KnowledgeStore.From(Resolve.Settings());

    [McpServerTool(Name = "recall", ReadOnly = true)]
    [Description("Look up what the team has taught the assistant (client rules, corrections, option meanings, how-tos). Check this before answering questions about a client's requirements or a past correction.")]
    public static object Recall(string query, int max = 5) =>
        Store().Recall(query, Math.Clamp(max, 1, 10)).Select(r => new { r.Entry.Id, r.Entry.Title, r.Entry.Category, r.Entry.Client, r.Entry.Source, saved = r.Entry.Saved, r.Entry.Text, score = Math.Round(r.Score, 2) });

    [McpServerTool(Name = "list_knowledge", ReadOnly = true)]
    [Description("All saved knowledge entries (id, title, category, client, source, date), without the text.")]
    public static object ListKnowledge() =>
        Store().All().Select(e => new { e.Id, e.Title, e.Category, e.Client, e.Source, saved = e.Saved });

    [McpServerTool(Name = "remember", Destructive = true)]
    [Description("Save one piece of knowledge (a client rule, a correction, an option meaning, a how-to) so it can be recalled later. A source is required (document and page, or who said it); never save something that is only a guess. apply=false previews it; with apply=true the user must approve in the app. Same title = update.")]
    public static object Remember(
        [Description("Short title, e.g. 'Reliance weld joint numbering'")] string title,
        [Description("The rule or fact, in plain words")] string text,
        [Description("client-rule | correction | option-meaning | how-to | context | decision | lesson | mistake | skill-proposal | note")] string category,
        [Description("Where it comes from: document and page, or who told you")] string source,
        [Description("Client or project it applies to; optional")] string client = "",
        bool apply = false)
    {
        try
        {
            var preview = $"Title: {title}\nCategory: {category}\nClient: {client}\nSource: {source}\n\n{text}";
            if (string.IsNullOrWhiteSpace(source) || !KnowledgeStore.Categories.Contains(category) || string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A text, a source and a valid category (" + string.Join(", ", KnowledgeStore.Categories) + ") are needed.");
            var existing = Store().Get(KnowledgeStore.IdFor(title));
            if (!apply) return new { saved = false, willUpdateExisting = existing is not null, preview, next = "Call again with apply=true to save it." };
            if (Resolve.NeedApproval("remember", $"{title}|{category}|{client}|{source}|{text}", $"Remember: {title}", preview) is { } wait) return wait;
            var e = Store().Save(title, text, category, source, client);
            return new { saved = true, e.Id, updated = existing is not null };
        }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "forget", Destructive = true)]
    [Description("Delete a saved knowledge entry by id. apply=false shows it; with apply=true the user must approve in the app.")]
    public static object Forget(string id, bool apply = false)
    {
        var e = Store().Get(id) ?? throw new McpException($"No knowledge entry '{id}'. Use list_knowledge.");
        if (!apply) return new { deleted = false, entry = new { e.Id, e.Title, e.Category, e.Text }, next = "Call again with apply=true to run it." };
        if (Resolve.NeedApproval("forget", id, $"Forget: {e.Title}", e.Text, destructive: true) is { } wait) return wait;
        return new { deleted = Store().Delete(id) };
    }
}

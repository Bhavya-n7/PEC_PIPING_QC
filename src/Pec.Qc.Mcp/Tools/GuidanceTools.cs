using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class GuidanceTools
{
    [McpServerTool(Name = "guidance", ReadOnly = true)]
    [Description("Read the assistant's rules and how-to guides on demand, so they need not be repeated in every request. No name: list what exists (master = the full rules, plus one guide per kind of task). With a name: the full text. Read the matching guide before doing that kind of task, and whenever you are unsure about a rule.")]
    public static object Guidance([Description("'master' or a guide name from the list; empty to list")] string? name = null)
    {
        var s = Resolve.Settings();
        if (string.IsNullOrWhiteSpace(name))
            return new
            {
                master = "Full rules: role, how to work, when to ask, how to report, what to save as knowledge",
                guides = PromptBuilder.SkillList(s.EffectiveSkillsDirectory).Select(x => new { name = x.Name, useWhen = x.When }),
            };
        return PromptBuilder.ReadGuidance(s, name.Trim()) is { } text
            ? new { name, text }
            : throw new McpException($"No guidance called '{name}'. Call guidance with no name to list them.");
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Claude;

/// <summary>Finds the MCP server and skills that travel with the app and builds the instructions Claude gets.</summary>
public static class AssistantSetup
{
    public static string? FindMcpExe(string appDirectory)
    {
        foreach (var p in new[]
                 {
                     Path.Combine(appDirectory, "mcp", "Pec.Qc.Mcp.exe"),
                     Path.Combine(appDirectory, "Pec.Qc.Mcp.exe"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>The same rules as the system prompt; used when no master-prompt.md exists yet.</summary>
    public const string BasePrompt = PromptBuilder.CorePrompt;

    /// <summary>Base prompt plus the text of every skill found in <paramref name="skillsDirectory"/> (front matter removed).</summary>
    public static string BuildSystemPrompt(string? skillsDirectory)
    {
        var sb = new StringBuilder(BasePrompt);
        if (skillsDirectory is not null && Directory.Exists(skillsDirectory))
            foreach (var f in Directory.GetFiles(skillsDirectory, "SKILL.md", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var text = File.ReadAllText(f);
                var m = Regex.Match(text, @"^---\s*\r?\n(?<fm>.*?)\r?\n---\s*\r?\n(?<body>.*)$", RegexOptions.Singleline);
                var name = Regex.Match(m.Success ? m.Groups["fm"].Value : "", @"^name:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                var when = Regex.Match(m.Success ? m.Groups["fm"].Value : "", @"^description:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                sb.AppendLine().AppendLine().AppendLine($"## Skill: {(name.Length > 0 ? name : Path.GetFileName(Path.GetDirectoryName(f)))}")
                  .AppendLine($"Use when: {when}").AppendLine(m.Success ? m.Groups["body"].Value.Trim() : text.Trim());
            }
        return sb.ToString();
    }
}

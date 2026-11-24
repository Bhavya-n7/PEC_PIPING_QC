using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Pec.Qc.Tests;

/// <summary>The guidance Claude reads must describe the product as it is: valid skill files, and no tool named that does not exist.</summary>
public class SkillsTests
{
    private static HashSet<string> ToolNames()
    {
        var asm = typeof(Pec.Qc.Mcp.Tools.EnvironmentTools).Assembly;
        var names = new HashSet<string>();
        foreach (var t in asm.GetTypes())
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                if (m.GetCustomAttribute<McpServerToolAttribute>() is { } a) names.Add(a.Name ?? m.Name);
        return names;
    }

    private static IEnumerable<string> Files()
    {
        var skills = TestPaths.FindUp("skills/qc-platform/SKILL.md");
        Assert.NotEqual("", skills);
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(skills)!, "..", ".."));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "skills"), "SKILL.md", SearchOption.AllDirectories)) yield return f;
        yield return Path.Combine(root, "prompts", "master-prompt.md");
    }

    [Fact]
    public void Every_skill_has_a_name_that_matches_its_folder_and_a_description()
    {
        foreach (var f in Files().Where(f => f.EndsWith("SKILL.md")))
        {
            var text = File.ReadAllText(f);
            var m = Regex.Match(text, @"^---\s*\r?\nname:\s*(?<n>.+?)\s*\r?\ndescription:\s*(?<d>.+?)\s*\r?\n---", RegexOptions.Singleline);
            Assert.True(m.Success, f + ": needs front matter with name and description");
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(f)), m.Groups["n"].Value.Trim());
            Assert.True(m.Groups["d"].Value.Length > 40, f + ": the description must say when to read the skill");
        }
    }

    [Fact]
    public void The_QC_skills_the_plan_promises_exist()
    {
        var names = Files().Where(f => f.EndsWith("SKILL.md")).Select(f => Path.GetFileName(Path.GetDirectoryName(f))).ToHashSet();
        foreach (var n in new[] { "qc-iso", "qc-pid", "qc-ga", "qc-findings-and-reports", "qc-project-inputs", "qc-troubleshoot", "qc-platform", "qc-learning" }) Assert.Contains(n, names);
    }

    [Fact]
    public void No_skill_or_prompt_names_a_tool_that_does_not_exist()
    {
        var tools = ToolNames();
        Assert.Contains("inputs_search", tools); Assert.Contains("qc_report_write", tools);
        // a backticked snake_case word is a tool name; a few are not tools (fields of a result) and are listed here on purpose
        var notTools = new HashSet<string> { "qc_type" };
        foreach (var f in Files())
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`"))
                if (!notTools.Contains(m.Groups[1].Value)) Assert.True(tools.Contains(m.Groups[1].Value), $"{Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)} mentions the tool `{m.Groups[1].Value}`, which does not exist");
    }

    [Fact]
    public void The_core_prompt_names_only_real_tools()
    {
        var tools = ToolNames();
        foreach (Match m in Regex.Matches(Pec.Qc.Core.Claude.PromptBuilder.CorePrompt, @"\b([a-z]+(?:_[a-z]+)+)\b"))
            Assert.True(tools.Contains(m.Groups[1].Value), $"the core prompt mentions `{m.Groups[1].Value}`, which is not a tool");
    }

    [Fact]
    public void The_tools_the_QC_work_relies_on_are_all_there()
    {
        var tools = ToolNames();
        foreach (var t in new[] { "qc_status", "inputs_list", "inputs_search", "inputs_read", "line_list_summary", "line_list_lookup", "client_requirements", "save_requirements", "requirements_check", "qc_checklist", "qc_report_write", "pdf_read", "guidance", "learn_record" })
            Assert.Contains(t, tools);
    }
}

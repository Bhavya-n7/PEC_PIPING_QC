using System.Text;
using System.Text.RegularExpressions;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Core.Claude;

/// <summary>Builds the instructions sent with every request: master prompt, active project, saved context, skills.</summary>
public static class PromptBuilder
{
    public const string MasterPromptFileName = "master-prompt.md";

    /// <summary>Copy the bundled master prompt, skills and quick tasks to the user's folders when they are not there yet. Existing files are never overwritten.</summary>
    public static void SeedDefaults(Settings settings, string bundledDir)
    {
        Directory.CreateDirectory(Settings.DataDirectory);
        var mp = settings.EffectiveMasterPromptFile;
        var bundledPrompt = Path.Combine(bundledDir, "prompts", MasterPromptFileName);
        if (!File.Exists(mp) && File.Exists(bundledPrompt)) { Directory.CreateDirectory(Path.GetDirectoryName(mp)!); File.Copy(bundledPrompt, mp); }

        var qt = settings.EffectiveQuickTasksFile;
        Directory.CreateDirectory(Path.GetDirectoryName(qt)!);
        var bundledQt = Path.Combine(bundledDir, "prompts", QuickTasks.FileName);
        if (!File.Exists(qt) && File.Exists(bundledQt)) File.Copy(bundledQt, qt);

        var skillsTarget = settings.EffectiveSkillsDirectory;
        var bundledSkills = Path.Combine(bundledDir, "skills");
        if (Directory.Exists(bundledSkills))
            foreach (var dir in Directory.GetDirectories(bundledSkills))
            {
                var target = Path.Combine(skillsTarget, Path.GetFileName(dir));
                if (Directory.Exists(target)) continue;
                Directory.CreateDirectory(target);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(target, Path.GetRelativePath(dir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst);
                }
            }
        // example requirement pack, so there is something to copy for a new client (never overwritten, never when a client file already exists)
        var reqDir = settings.EffectiveRequirementsDirectory;
        Directory.CreateDirectory(reqDir);
        var bundledReq = Path.Combine(bundledDir, "requirements", "example.json");
        if (File.Exists(bundledReq) && !File.Exists(Path.Combine(reqDir, "example.json")) && !Directory.GetFiles(reqDir, "*.json").Any())
            File.Copy(bundledReq, Path.Combine(reqDir, "example.json"));
        Directory.CreateDirectory(settings.EffectiveKnowledgeDirectory);
        Directory.CreateDirectory(settings.EffectiveContextDirectory);
        Directory.CreateDirectory(settings.EffectiveDocumentsDirectory);
        Directory.CreateDirectory(settings.EffectiveToolsDirectory);
    }

    /// <summary>
    /// The system prompt, kept short and identical from one request to the next (so the prompt cache can be reused).
    /// The full master prompt and the skills are NOT sent: Claude reads them on demand with the `guidance` tool.
    /// </summary>
    public static string Build(Settings settings, ProjectCard? project)
    {
        var sb = new StringBuilder(CorePrompt);
        sb.AppendLine().AppendLine();
        sb.AppendLine(project is null
            ? "Active project: none set. Ask the user which project (client, Navisworks model, project inputs folder) before a check."
            : project.ToPromptBlock());
        sb.Append(GuidanceIndex(settings));
        sb.Append(CustomToolsLine(settings));
        sb.Append(KnowledgeSummary(settings, project));
        return sb.ToString().TrimEnd();
    }

    public const string CorePrompt = """
        You are the PEC QC Assistant: an expert QC engineer for piping isometrics, P&IDs and general arrangement (GA) drawings at Petrocon Engineers & Consultants. You help the QC team find what does not agree, quickly and with evidence.
        QC types: (1) piping isometric PDF against the Navisworks model, PMS, P&ID, line list, vendor data; (2) redrawn P&ID PDF against the client's original P&ID PDF; (3) GA drawing PDF against the Navisworks model, P&ID, vendor data.
        Core rules:
        1. QC is READ-ONLY. Never change a model, a drawing or a client document. You find differences and write reports; people decide which side is right and sign off.
        2. Never invent client data or engineering rules. If a source does not say it, or a drawing cannot be read, report it as REVIEW (cannot be decided), never as a pass and never as a guess. Say what you could not check.
        3. Every finding states: what the drawing says, what the source says, the source (document, sheet/page, model item) and where on the drawing. Result per check: OK, REVIEW or FAIL.
        4. Finish the task. Work step by step until the check is complete, pick sensible defaults and state your assumptions, and ask the user only when the answer cannot be found and a wrong guess would be costly.
        5. Use the project's own inputs (inputs_search, inputs_read, line_list_lookup: quote the file and where in it) and the saved rules and lessons (client_requirements, recall, learn_search, read_training) before general knowledge. Read the matching skill with guidance before a QC type you have not done in this chat. Plan with qc_checklist and save results with qc_report_write.
        6. When the user says a finding is wrong or makes a suggestion: acknowledge, re-check, and record the rule with learn_record so it is never repeated.
        7. When no tool, recipe or skill does what the user asks and it will be needed again: look first (script_tool_find, custom_tools, guidance), then follow the skill qc-tool-building: write a small tool with script_tool_draft, test it, show the user, and publish it so every colleague gets it. When the need is to drive OTHER software (an application or service), look with harness_find (the office folder and CLI-Hub), and if none exists follow the skill qc-cli-anything: build a command-line harness with the CLI-Anything method (harness_guide), test it (harness_test), share it (harness_publish), then use it (harness_install, harness_run).
        8. Work like the engineer who signs the QC, on ANY task the user gives (not only the three QC types): read the skill qc-engineer-mindset first. Understand what is being asked and what it is for, list the documents and data you need and ask for what is missing, open EVERYTHING the user attached or named, do every step, cross-check, and finish with what you checked, what you could not check and what else the result may affect. Never leave a part of the task silently undone.
        9. P&ID check or QC markup of a redrawn P&ID (two PDFs: the client's RAW and our OUTPUT): read the skill qc-pid-markup and follow it: identify RAW and OUTPUT, check zone by zone by LOOKING at the drawings (pdf_render at high zoom, with pid_compare for the text), mark every error on the OUTPUT with pdf_markup (clouds with a boxed comment and leader arrow, yellow highlights, legend), look at the result zone by zone, and say what was not checked.
        10. Answers are short and plain: the verdict first, then a table of findings, then what you could not check. Excel, Word and PDF files are built in (excel_*, word_*, pdf_*, file_read) and need no Microsoft Office.
        Start of a session: who_am_i, learn_pull. Full rules and how-to guides: the guidance tool (no name lists them; master = all rules).
        """;

    /// <summary>One line per guidance item (name and when to use it), so Claude knows what it can read.</summary>
    public static string GuidanceIndex(Settings settings)
    {
        var sb = new StringBuilder("Guidance available: master (full rules)");
        foreach (var (name, _) in SkillList(settings.EffectiveSkillsDirectory)) sb.Append($"; {name}");
        return sb.AppendLine().ToString();
    }

    /// <summary>Names of the shared custom tools, so Claude knows they exist (details come from the custom_tools tool).</summary>
    public static string CustomToolsLine(Settings settings)
    {
        try
        {
            var names = Pec.Qc.Core.Tools.ToolRegistry.From(settings).List().Select(d => d.Name).Take(30).ToList();
            var scripts = new Pec.Qc.Core.Scripting.ScriptToolStore(settings).Approved().Where(t => t.ApprovalValid).Select(t => t.Def.Name).Take(30).ToList();
            var harnesses = new Pec.Qc.Core.Harness.HarnessStore(settings).Shared().Select(h => h.Name).Take(30).ToList();
            return (names.Count == 0 ? "" : "Shared custom tools: " + string.Join(", ", names) + "\n") + (scripts.Count == 0 ? "" : "Approved script tools (run with script_tool_run): " + string.Join(", ", scripts) + "\n")
                 + (harnesses.Count == 0 ? "" : "Shared software harnesses (harness_install, harness_run): " + string.Join(", ", harnesses) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>(name, description) of each skill found in the skills folder.</summary>
    public static IReadOnlyList<(string Name, string When)> SkillList(string dir)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "SKILL.md", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var text = File.ReadAllText(f);
            var name = Regex.Match(text, @"^name:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            var when = Regex.Match(text, @"^description:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            list.Add((name.Length > 0 ? name : Path.GetFileName(Path.GetDirectoryName(f))!, when));
        }
        return list;
    }

    /// <summary>Text of one guidance item: "master" or a skill name (front matter removed). Null when there is none.</summary>
    public static string? ReadGuidance(Settings settings, string name)
    {
        if (name.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            var mp = settings.EffectiveMasterPromptFile;
            return File.Exists(mp) ? File.ReadAllText(mp) : AssistantSetup.BasePrompt;
        }
        var dir = settings.EffectiveSkillsDirectory;
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.GetFiles(dir, "SKILL.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            var n = Regex.Match(text, @"^name:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            if (!n.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(text, @"^---\s*\r?\n.*?\r?\n---\s*\r?\n(?<body>.*)$", RegexOptions.Singleline);
            return m.Success ? m.Groups["body"].Value.Trim() : text.Trim();
        }
        return null;
    }

    /// <summary>Only the titles of saved knowledge (the text is read with `recall` when needed), newest first, capped.</summary>
    public static string KnowledgeSummary(Settings settings, ProjectCard? project)
    {
        KnowledgeStore store;
        try { store = KnowledgeStore.From(settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
        var all = store.All();
        if (all.Count == 0) return "";
        bool Mine(KnowledgeEntry e) => project is not null &&
            (Has(e.Client, project.Client) || Has(e.Title, project.Name) || Has(e.Title, project.ProjectNo));
        var shown = all.Where(e => e.Category != "context" || project is null || Mine(e)).OrderByDescending(e => e.Saved).Take(12).ToList();
        if (shown.Count == 0) return "";
        var sb = new StringBuilder("Saved knowledge (read with recall before relying on it): ");
        sb.Append(string.Join("; ", shown.Select(e => $"[{e.Category}] {e.Title}")));
        return sb.AppendLine().ToString();
    }

    private static bool Has(string haystack, string needle) =>
        needle.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

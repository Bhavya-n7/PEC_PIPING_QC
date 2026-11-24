using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Claude;

/// <summary>A prompt the user can send with one click. <c>{{field}}</c> comes from the active project; <c>{{?question}}</c> is asked.</summary>
public sealed record QuickTask(string Id, string Category, string Title, string Prompt, string? Hint = null);

public static class QuickTasks
{
    public const string FileName = "quick-tasks.json";

    /// <summary>Built-in tasks, used when no quick-tasks.json is found. A user file in the data folder replaces them.</summary>
    public static IReadOnlyList<QuickTask> Defaults { get; } =
    [
        new("where", "Project", "Which project are we on?", "Tell me the state: the active project, its client, the Navisworks model and the project inputs folder, and what is ready and what blocks a QC check.", "The active project, its model and inputs, and what is ready for a QC check."),
        new("iso", "Isometric QC", "Check an isometric against the model", "Check the isometric {{?Path of the isometric PDF}} for {{project}} against the Navisworks model, the PMS, the P&ID, the line list and vendor data. Give the verdict first, then a table of FAIL and REVIEW findings with the drawing value, the source value and the source, then what you could not check.", "Verdict first, then FAIL and REVIEW findings with drawing value, source value and source."),
        new("iso-batch", "Isometric QC", "Check a folder of isometrics", "Check every isometric PDF in {{?Folder with the isometric PDFs}} for {{project}}. Give one row per line with its verdict, then the findings that need attention, and write the report.", "One row per line with its verdict, the findings that need attention, and a saved report."),
        new("pid", "P&ID QC", "Check a redrawn P&ID against the original", "Compare the redrawn P&ID {{?Path of the redrawn P&ID PDF}} with the client's original {{?Path of the original P&ID PDF}} for {{project}}. List every line, equipment, instrument, tag and note that is missing, extra or different, with the sheet and position.", "Every line, equipment, instrument, tag and note that is missing, extra or different."),
        new("ga", "GA QC", "Check a GA drawing", "Check the GA drawing {{?Path of the GA drawing PDF}} for {{project}} against the Navisworks model, the P&IDs and the vendor data. Give the verdict first, then the FAIL and REVIEW findings with their sources.", "Checked against the Navisworks model, the P&IDs and the vendor data."),
        new("inputs", "Project inputs", "What project inputs do we have?", "List the project inputs for {{project}} (PMS, line list, P&IDs, vendor data) and say which QC checks each one supports and what is missing.", "PMS, line list, P&IDs and vendor data: what is there and what is missing."),
        new("line", "Project inputs", "Look up a line in the line list", "Look up the line {{?Line number}} in the line list of {{project}} and show every value with its unit and where it is (file, sheet, row). Say which values are blank.", "Every value of the line with its unit, and the file, sheet and row it comes from."),
        new("checklist", "Project inputs", "What does a check cover?", "Show the checklist for the {{?QC type: iso, pid or ga}} check and say which project inputs each item needs and which of them {{project}} has.", "The items of the check, the inputs each needs, and what this project is missing."),
        new("rule", "Knowledge", "What does the PMS / client rule say?", "What does {{?Your question about a spec, class or client rule}} say for {{project}}? Answer from the project inputs and saved rules and give the document and page.", "Answered from the project inputs and saved rules, with document and page."),
        new("recall", "Knowledge", "What do we know about this client?", "What do we know about {{client}}? Use recall and list each rule with its source.", "Rules and notes saved for this client, each with its source."),
        new("learn", "Knowledge", "Save what we learned", "Record what we learned in this chat: corrections, preferences, gotchas and rules that worked. Use learn_record and show me each entry before saving.", "Record corrections and rules from this chat, with your approval."),
    ];

    public static IReadOnlyList<QuickTask> Load(string? file = null)
    {
        file ??= Settings.Load().EffectiveQuickTasksFile;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<QuickTask>>(File.ReadAllText(file)) is { Count: > 0 } list) return list;
        }
        catch (JsonException) { }
        return Defaults;
    }

    /// <summary>The questions in a prompt, in order, without duplicates.</summary>
    public static IReadOnlyList<string> Questions(string prompt) =>
        Regex.Matches(prompt, @"\{\{\?([^}]+)\}\}").Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();

    /// <summary>Fill the prompt from the active project and the answers. Fields that are empty are replaced by a plain phrase so the sentence still reads.</summary>
    public static string Fill(string prompt, ProjectCard? project, IReadOnlyDictionary<string, string>? answers = null)
    {
        string F(string v, string empty) => string.IsNullOrWhiteSpace(v) ? empty : v.Trim();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = F(project?.Name ?? "", "the active project"),
            ["client"] = F(project?.Client ?? "", "the client of the active project"),
            ["projectno"] = F(project?.ProjectNo ?? "", "the project number of the active project"),
            ["model"] = F(project?.ModelFile ?? "", "the Navisworks model of the active project"),
            ["inputs"] = F(project?.InputsFolder ?? "", "the inputs folder of the active project"),
        };
        var s = Regex.Replace(prompt, @"\{\{\?([^}]+)\}\}", m => answers is not null && answers.TryGetValue(m.Groups[1].Value.Trim(), out var a) ? a.Trim() : m.Value);
        return Regex.Replace(s, @"\{\{([a-z]+)\}\}", m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value, RegexOptions.IgnoreCase);
    }
}

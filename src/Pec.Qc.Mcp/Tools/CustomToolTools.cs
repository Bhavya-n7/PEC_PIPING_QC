using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Tools;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Calls the assistant's own tools by name, so a custom tool uses exactly the same code (and the same approvals) as Claude does.</summary>
internal static class ToolRunner
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Dictionary<string, MethodInfo> Methods = typeof(ToolRunner).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Select(m => (Attr: m.GetCustomAttribute<McpServerToolAttribute>(), Method: m))
        .Where(x => x.Attr is not null)
        .ToDictionary(x => x.Attr!.Name ?? x.Method.Name, x => x.Method, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(Methods.Keys, StringComparer.OrdinalIgnoreCase);

    public static async Task<JsonNode?> InvokeAsync(string tool, JsonObject args, CancellationToken ct)
    {
        if (!Methods.TryGetValue(tool, out var m)) throw new McpException($"There is no tool '{tool}'.");
        var values = new List<object?>();
        foreach (var p in m.GetParameters())
        {
            if (p.ParameterType == typeof(CancellationToken)) { values.Add(ct); continue; }
            var key = args.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (key is not null && args[key] is { } node) values.Add(node.Deserialize(p.ParameterType, Json));
            else if (p.HasDefaultValue) values.Add(p.DefaultValue);
            else throw new McpException($"Step for '{tool}': the argument '{p.Name}' is missing.");
        }
        object? result;
        try { result = m.Invoke(null, values.ToArray()); }
        catch (TargetInvocationException e) when (e.InnerException is not null) { throw e.InnerException; }
        if (result is Task task)
        {
            await task;
            result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
        }
        return result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), Json);
    }
}

[McpServerToolType]
public static class CustomToolTools
{
    private static ToolRegistry Registry() => ToolRegistry.From(Resolve.Settings());

    [McpServerTool(Name = "custom_tools", ReadOnly = true)]
    [Description("List the shared custom tools (named recipes made of the assistant's own tools), or show one in full. Check this before doing a repeated multi-step task: a tool for it may already exist. Tools made on any PC are shared through the office folder.")]
    public static object CustomTools([Description("Tool name to show in full; empty to list")] string? name = null)
    {
        var reg = Registry();
        reg.Sync();
        if (string.IsNullOrWhiteSpace(name))
            return reg.List().Select(d => new { d.Name, d.Description, parameters = d.Parameters.Select(p => p.Name + (p.Required ? "" : "?")), steps = d.Steps.Count, d.Author });
        return reg.Get(name.Trim()) ?? throw new McpException($"No custom tool '{name}'.");
    }

    [McpServerTool(Name = "run_custom_tool", Destructive = true)]
    [Description("Run a custom tool: its steps run one after another with the given arguments. Every step is a normal tool call, so changes still need the user's approval; a step that waits for approval stops the run and returns resumeFrom: after the user approves, call again with the same arguments and fromStep=resumeFrom. Results are shortened; use the normal tools to see more.")]
    public static async Task<object> RunCustomTool(
        [Description("Custom tool name")] string name,
        [Description("Values for the tool's parameters, e.g. {\"folder\":\"D:/pcf\"}")] JsonObject? arguments = null,
        [Description("Start at this step (1 = first); used to continue after an approval")] int fromStep = 1,
        [Description("Longest result text to return per step")] int maxChars = 1200,
        CancellationToken ct = default)
    {
        var def = Registry().Get(name) ?? throw new McpException($"No custom tool '{name}'. Use custom_tools to list them.");
        var values = (arguments ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        List<(string Tool, JsonObject Args, string? Note)> steps;
        try { steps = ToolRegistry.Resolve(def, values); }
        catch (ArgumentException e) { throw new McpException(e.Message); }

        var done = new List<object>();
        for (var i = Math.Max(1, fromStep) - 1; i < steps.Count; i++)
        {
            var (tool, args, note) = steps[i];
            JsonNode? result;
            try { result = await ToolRunner.InvokeAsync(tool, args, ct); }
            catch (Exception e) when (e is McpException or ArgumentException or InvalidOperationException or IOException or JsonException)
            {
                done.Add(new { step = i + 1, tool, ok = false, error = e.Message });
                return new { name, ran = done.Count, stoppedAt = i + 1, reason = "A step failed; the later steps were not run.", steps = done };
            }
            var text = result?.ToJsonString() ?? "null";
            var waiting = result is JsonObject o && o["waitingForApproval"]?.GetValue<bool>() == true;
            done.Add(new { step = i + 1, tool, ok = true, waitingForApproval = waiting ? true : (bool?)null, result = text.Length <= maxChars ? text : text[..maxChars] + "…(shortened)" });
            if (waiting)
                return new { name, ran = done.Count, stoppedAt = i + 1, resumeFrom = i + 1, reason = "Waiting for the user's approval for this step. After they approve, call run_custom_tool again with the same arguments and fromStep=" + (i + 1) + ".", steps = done };
        }
        return new { name, ran = done.Count, completed = true, steps = done };
    }

    [McpServerTool(Name = "create_custom_tool", Destructive = true)]
    [Description("Create (or update) a shared custom tool: a named recipe of 1-12 steps, each a call of an existing tool with arguments; {{parameter}} in an argument is filled in when the tool runs. Use it when the user repeats the same multi-step task. It contains no code. apply=false checks it and shows it; with apply=true the user must approve in the app, then it is saved on this PC and in the shared folder so every user gets it.")]
    public static object CreateCustomTool(
        [Description("Name: lowercase letters, digits, _ (3-41 characters)")] string name,
        [Description("When to use it, in one sentence")] string description,
        [Description("The values the user gives when running it")] CustomToolParam[] parameters,
        [Description("The steps, in order: {tool, args, note}. Not allowed: run_custom_tool, create_custom_tool, save_skill, remember, forget")] CustomToolStep[] steps,
        bool apply = false)
    {
        var def = new CustomToolDef(name.Trim(), description.Trim(), parameters, steps, Environment.UserName, Environment.MachineName, DateTime.Now);
        var errors = ToolRegistry.Validate(def, ToolRunner.Names);
        var existing = Registry().Get(def.Name);
        if (existing is not null) def = def with { Revision = existing.Revision + 1 };
        var preview = $"Tool: {def.Name} (revision {def.Revision})\n{def.Description}\nParameters: {string.Join(", ", def.Parameters.Select(p => p.Name + (p.Required ? "" : "?")))}\n" +
                      string.Join("\n", def.Steps.Select((s, i) => $"{i + 1}. {s.Tool} {s.Args.ToJsonString()}{(s.Note is null ? "" : "  // " + s.Note)}"));
        if (errors.Count > 0) return new { saved = false, errors, preview };
        if (!apply) return new { saved = false, valid = true, willReplace = existing is not null, preview, next = "Call again with apply=true to save it." };
        if (Resolve.NeedApproval("create_custom_tool", $"{def.Name}|{string.Join("|", def.Steps.Select(s => s.Tool + s.Args.ToJsonString()))}|{string.Join(",", def.Parameters.Select(p => p.Name))}",
                $"Create the shared tool '{def.Name}'", preview) is { } wait) return wait;
        var (local, shared) = Registry().Save(def);
        return new { saved = true, def.Name, def.Revision, savedOnThisPc = local, savedInSharedFolder = shared, note = shared ? "Every user of the shared folder can use it now." : "The shared folder could not be reached; it will be copied there when it is available." };
    }

    [McpServerTool(Name = "save_skill", Destructive = true)]
    [Description("Create or update a shared skill (a how-to guide in <skills folder>\\<name>\\SKILL.md) so every user benefits. Write what to do, step by step, and when to use it; no secrets. apply=false shows the diff; with apply=true the user must approve in the app. A backup of the old version is kept.")]
    public static object SaveSkill(
        [Description("Skill name: lowercase letters, digits and -")] string name,
        [Description("One sentence: when to use this skill")] string description,
        [Description("The guide in Markdown")] string body,
        bool apply = false)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z][a-z0-9\-]{2,40}$")) throw new McpException("Skill name: lowercase letters, digits and -, 3 to 41 characters.");
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(body)) throw new McpException("A description and a body are needed.");
        var file = Path.Combine(Resolve.Settings().EffectiveSkillsDirectory, name, "SKILL.md");
        var text = $"---\nname: {name}\ndescription: {description.Trim().Replace("\n", " ")}\n---\n\n{body.Trim()}\n";
        var preview = SafeWrite.WriteText(file, text, false);
        if (!apply) return new { saved = false, file, preview.Diff, next = "Call again with apply=true to save it." };
        if (preview.LinesChanged == 0) return new { saved = false, file, note = "The skill is already like this." };
        if (Resolve.NeedApproval("save_skill", $"{name}|{text}", $"Save the shared skill '{name}'", preview.Diff) is { } wait) return wait;
        var c = SafeWrite.WriteText(file, text, true);
        return new { saved = true, file, c.BackupPath, note = "Every user of the shared folder gets it." };
    }
}

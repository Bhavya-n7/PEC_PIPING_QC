using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Scripting;
using Pec.Qc.Core.Users;
using Pec.Qc.Mcp.Scripting;

namespace Pec.Qc.Mcp.Tools;

/// <summary>
/// Tools that Claude writes when nothing it has does the job: a small C# script with tests. The author drafts and tests it on their own PC; publishing puts it on the shared folder as
/// PENDING; the QC lead reads it and its tests, the tests are run again, and approving makes it available to everyone (its SHA-256 pinned). Code is only ever run on another PC after that.
/// </summary>
[McpServerToolType]
public static class ScriptToolTools
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static ScriptToolStore Store() => new(Resolve.Settings());

    private static IReadOnlySet<string> Taken() =>
        new HashSet<string>(ToolRunner.Names.Concat(Pec.Qc.Core.Tools.ToolRegistry.From(Resolve.Settings()).List().Select(t => t.Name)), StringComparer.OrdinalIgnoreCase);

    private static List<string> ReadableFolders()
    {
        var s = Resolve.Settings(); var l = new List<string> { s.EffectiveModelsDirectory, s.EffectiveReportsDirectory };
        try { l.Add(Resolve.Inputs(null).Lib.Folder); } catch (McpException) { }
        return l.Where(Directory.Exists).ToList();
    }

    private static string OutDir(string name) => Path.Combine(Settings.DataDirectory, "script-out", name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    private static object Describe(StoredScriptTool t, string where) => new
    {
        name = t.Def.Name, t.Def.Description, parameters = t.Def.Parameters.Select(p => new { p.Name, p.Type, p.Description, p.Required }), where,
        author = t.Author.Length > 0 ? t.Author : t.Def.Author, revision = t.Def.Revision, approvedBy = t.Approval?.ApprovedBy, tests = t.Def.Tests.Count,
    };

    [McpServerTool(Name = "script_tool_find", ReadOnly = true)]
    [Description("Look for a tool that Claude or a colleague already wrote (and the QC lead approved) for what the user asks, BEFORE building a new one: searches the shared approved script tools and this user's own drafts by name and description. Also check custom_tools (recipes) and guidance (skills). An approved tool is run with script_tool_run.")]
    public static object Find([Description("Words about what the tool should do")] string query)
    {
        var hits = Store().Find(query);
        return new
        {
            found = hits.Count, tools = hits.Take(12).Select(h => Describe(h.Tool, h.Where)).ToList(),
            next = hits.Count == 0 ? "Nothing matches. If no skill, recipe or built-in tool does it either, follow the skill qc-tool-building: tell the user you will build the tool, then script_tool_draft." : "Use the one that fits with script_tool_run (read its parameters first); if none fits, build a new one.",
        };
    }

    [McpServerTool(Name = "script_tool_draft", Destructive = true)]
    [Description("BUILD a new tool when no skill, recipe or built-in tool does what the user asks (read the skill qc-tool-building first). You give the name, a description (when to use it), the parameters, the C# code and tests. The code is a C# script: arguments are in the Args dictionary (always text), files are read and written ONLY through the Qc object (Qc.ReadText, Qc.ReadLines, Qc.PdfText, Qc.ExcelRows, Qc.WriteText, Qc.WriteExcel, Qc.Log), the result is what the script returns (a string or an object that becomes JSON). It cannot use the network, start programs, use reflection, the environment or System.IO.File. Tests give arguments as plain TEXT (not file paths: they must run on any PC) and what the result must contain. apply=false only compiles and checks the rules and shows any problem with its line; apply=true asks the user to approve (they see the code), runs every test and saves the draft on this PC. Then show the user the test results.")]
    public static object Draft([Description("lowercase letters, digits and _, e.g. extract_valve_tags")] string name, [Description("One or two sentences: when to use this tool")] string description,
        [Description("JSON array: [{\"name\":\"text\",\"type\":\"string\",\"description\":\"...\",\"required\":true}]")] string parameters, [Description("The C# script")] string code,
        [Description("JSON array: [{\"name\":\"two numbers\",\"args\":{\"text\":\"1 2\"},\"expectContains\":\"3\"},{\"name\":\"bad input\",\"args\":{\"text\":\"x\"},\"expectError\":true}]")] string tests, bool apply = false)
    {
        ScriptToolDef def;
        try
        {
            def = new ScriptToolDef(name.Trim(), description.Trim(), JsonSerializer.Deserialize<List<ScriptParam>>(parameters, Json) ?? [], (JsonSerializer.Deserialize<List<TestIn>>(tests, Json) ?? [])
                .Select(t => new ScriptTest(t.Name ?? "test", (t.Args ?? new()).ToDictionary(k => k.Key, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString() ?? "" : k.Value.ToString()), t.ExpectContains, t.ExpectError)).ToList());
        }
        catch (JsonException e) { throw new McpException("The parameters or tests are not valid JSON: " + e.Message); }
        var problems = ScriptToolStore.Validate(def, Taken());
        if (problems.Count > 0) return new { saved = false, problems, next = "Fix these and call again." };
        var check = ScriptEngine.Analyze(code);
        if (!check.Ok)
            return new { saved = false, compiles = check.Errors.Count == 0, errors = check.Errors, notAllowed = check.Violations.Select(v => new { v.Line, v.What }), next = "Fix the code (use only the Qc object for files; no network, programs, reflection or environment) and call again." };
        if (!apply) return new { saved = false, compiles = true, rulesOk = true, tests = def.Tests.Count, next = "The code compiles and follows the rules. Show the user what the tool will do, then call again with apply=true: the user approves, the tests run and the draft is saved on this PC." };
        var sha = ScriptToolStore.Sha256(code);
        var detail = $"Tool: {def.Name}\n{def.Description}\n\nCODE:\n{code}\n\nTESTS: {JsonSerializer.Serialize(def.Tests, Json)}";
        if (Resolve.NeedApproval("script_tool_draft", def.Name + "|" + sha, $"Run the tests of the new tool '{def.Name}' on this PC and save the draft", detail) is { } wait) return wait;
        var results = ScriptEngine.RunTests(def, code, ReadableFolders(), OutDir(def.Name));
        var draft = Store().SaveDraft(def, code, results);
        var all = results.All(r => r.Passed);
        return new
        {
            saved = true, name = def.Name, revision = draft.Def.Revision, testsPassed = all, results = results.Select(r => new { r.Name, r.Passed, r.Message, r.Output }),
            next = all ? "All tests pass. Show the user the results and what the tool does. Offer to use it now (script_tool_run with useDraft=true) and to publish it for the QC lead's review (script_tool_publish) so every colleague gets it."
                       : "Some tests fail. Fix the code or the tests and draft again. Do not publish a tool whose tests fail.",
        };
    }

    private sealed class TestIn { public string? Name { get; set; } public Dictionary<string, JsonElement>? Args { get; set; } public string? ExpectContains { get; set; } public bool ExpectError { get; set; } }

    private static Dictionary<string, string> ReadArgs(StoredScriptTool t, string argsJson)
    {
        Dictionary<string, JsonElement> raw;
        try { raw = string.IsNullOrWhiteSpace(argsJson) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson, Json) ?? new(); }
        catch (JsonException e) { throw new McpException("args must be a JSON object: " + e.Message); }
        var args = raw.ToDictionary(k => k.Key, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString() ?? "" : k.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var missing = t.Def.Parameters.Where(p => p.Required && !args.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        if (missing.Count > 0) throw new McpException("Missing arguments: " + string.Join(", ", missing));
        return args;
    }

    [McpServerTool(Name = "script_tool_run", Destructive = true)]
    [Description("Run a script tool. An APPROVED tool (see script_tool_find) runs for everyone: it can only read the files you pass as arguments and the project's inputs, models and reports, and write one output folder. useDraft=true runs the user's own unreviewed draft instead: the user must approve that first. Pass args as a JSON object of text values, e.g. {\"file\":\"D:\\\\x.pdf\"}. The result and the files it wrote are returned: tell the user where the files are.")]
    public static object Run([Description("The tool's name")] string name, [Description("JSON object of arguments")] string args = "{}", [Description("Run the user's own draft")] bool useDraft = false, [Description("Seconds, 1-120")] int timeoutSeconds = 30)
    {
        var store = Store();
        var tool = useDraft ? store.Draft(name) : store.ApprovedTool(name);
        if (tool is null) throw new McpException(useDraft ? $"There is no draft named '{name}' on this PC." : $"There is no approved tool named '{name}'. script_tool_find shows what exists; a draft is run with useDraft=true.");
        if (!useDraft && !tool.ApprovalValid) throw new McpException($"The code of '{name}' is not the code that was approved (it was changed): it is not run. Tell the QC lead.");
        var a = ReadArgs(tool, args);
        if (useDraft && Resolve.NeedApproval("script_tool_run", name + "|" + ScriptToolStore.Sha256(tool.Code) + "|" + args, $"Run your draft tool '{name}' (not reviewed yet) on this PC", tool.Code) is { } wait) return wait;
        var r = ScriptEngine.Execute(tool.Code, a, ReadableFolders(), OutDir(name), TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        if (!useDraft) Resolve.Audit("script_tool_run", name + "|" + tool.Approval!.CodeSha256, r.Ok ? "ok" : "failed");
        return new { ran = true, name, ok = r.Ok, result = r.Output, error = r.Error, logs = r.Logs, filesWritten = r.Files, seconds = Math.Round(r.Milliseconds / 1000.0, 1), approved = !useDraft,
            next = r.Ok ? "Give the user the result and the paths of any files written." : "Tell the user what failed. If it is a bug in the tool, fix it with script_tool_draft (a new draft) and publish the fix." };
    }

    [McpServerTool(Name = "script_tool_publish", Destructive = true)]
    [Description("Send the user's tested draft to the shared folder for the QC lead's review, so that every colleague can use it once approved. The tests must all have passed. apply=false shows what would be sent; apply=true asks the user to approve and sends it. Tell the user the QC lead must approve it (script_tool_review) before others can run it.")]
    public static object Publish([Description("The draft's name")] string name, bool apply = false)
    {
        var s = Resolve.Settings(); var store = new ScriptToolStore(s);
        var d = store.Draft(name) ?? throw new McpException($"There is no draft named '{name}' on this PC.");
        if (d.Results is null || d.Results.Count == 0 || d.Results.Any(r => !r.Passed)) throw new McpException("Only a tool whose tests all pass can be published. Draft it again with passing tests.");
        if (!s.HasShared) throw new McpException("No shared folder is set: there is nowhere to share it. Set it in Settings.");
        if (!apply) return new { published = false, name, description = d.Def.Description, tests = d.Def.Tests.Count, wouldWriteTo = Path.Combine(store.PendingDir, Resolve.Who().FileKey, name), next = "Call again with apply=true to send it to the QC lead for review." };
        if (Resolve.NeedApproval("script_tool_publish", name + "|" + ScriptToolStore.Sha256(d.Code), $"Send the tool '{name}' to the shared folder for review", d.Code) is { } wait) return wait;
        var folder = store.Publish(d, Resolve.Who(), d.Results);
        var audit = Resolve.Audit("script_tool_publish", name + "|" + ScriptToolStore.Sha256(d.Code), "pending review");
        return new { published = true, name, folder, audit, next = "Tell the user: the QC lead will review the code and the tests (script_tool_pending / script_tool_review). When approved, every user can run it." };
    }

    [McpServerTool(Name = "script_tool_pending", ReadOnly = true)]
    [Description("QC LEAD: the tools colleagues have sent for review, with their description, parameters, the author's test results and the full CODE. Read the code with the user: what it reads, what it writes, whether it does what the description says. Then script_tool_review.")]
    public static object Pending()
    {
        if (Resolve.Require(QcRole.Lead, "Reviewing tools written by Claude") is { } denied) return denied;
        var list = Store().Pending();
        return new
        {
            count = list.Count,
            tools = list.Select(t => new { author = t.Author, name = t.Def.Name, t.Def.Description, parameters = t.Def.Parameters, revision = t.Def.Revision, sha256 = ScriptToolStore.Sha256(t.Code),
                authorTests = t.Results?.Select(r => new { r.Name, r.Passed, r.Message }), code = t.Code, tests = t.Def.Tests }).ToList(),
            next = list.Count == 0 ? "Nothing waits for review." : "Go through the code with the user. Never approve on your own.",
        };
    }

    [McpServerTool(Name = "script_tool_review", Destructive = true)]
    [Description("QC LEAD: approve or reject a pending tool. Approving checks the code against the rules again, RUNS ITS TESTS on this PC (they must all pass), and then makes the tool available to every user with the code's SHA-256 pinned: changed code is never run. Rejecting hides it and keeps the reason. apply=false shows what would happen; apply=true asks the user to approve and does it.")]
    public static object Review([Description("The author's folder name as shown by script_tool_pending (the author's employee code)")] string author, [Description("The tool's name")] string name, [Description("approve or reject")] string decision, [Description("Why (for a rejection)")] string reason = "", bool apply = false)
    {
        if (Resolve.Require(QcRole.Lead, "Approving or rejecting a tool written by Claude") is { } denied) return denied;
        var store = Store();
        var p = store.PendingTool(author.Trim(), name.Trim()) ?? throw new McpException("That tool is not waiting for review (script_tool_pending lists them).");
        var approve = decision.Trim().Equals("approve", StringComparison.OrdinalIgnoreCase);
        if (!approve && !decision.Trim().Equals("reject", StringComparison.OrdinalIgnoreCase)) throw new McpException("decision must be approve or reject.");
        var sha = ScriptToolStore.Sha256(p.Code);
        if (!approve)
        {
            if (!apply) return new { applied = false, decision = "reject", name, next = "Call again with apply=true to reject it." };
            if (string.IsNullOrWhiteSpace(reason)) throw new McpException("Give the reason for the rejection: the author sees it.");
            store.Reject(p, Resolve.Who().Display, reason.Trim());
            return new { applied = true, rejected = true, audit = Resolve.Audit("script_tool_review", $"{name}|{sha}", "rejected: " + reason.Trim()) };
        }
        var problems = ScriptToolStore.Validate(p.Def, Taken().Except([p.Def.Name], StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var check = ScriptEngine.Analyze(p.Code);
        if (problems.Count > 0 || !check.Ok) return new { applied = false, problems, errors = check.Errors, notAllowed = check.Violations.Select(v => new { v.Line, v.What }), next = "It cannot be approved as it is. Reject it with the reasons so that the author can fix it." };
        if (!apply) return new { applied = false, decision = "approve", name, author = p.Author, sha256 = sha, next = "Call again with apply=true: the user approves, the tests are run on this PC and, if all pass, the tool is available to everyone." };
        if (Resolve.NeedApproval("script_tool_review", $"{author}|{name}|{sha}", $"Approve the tool '{name}' (by {p.Author}) for every user", p.Code) is { } wait) return wait;
        var results = ScriptEngine.RunTests(p.Def, p.Code, ReadableFolders(), OutDir(name));
        if (results.Any(r => !r.Passed)) return new { applied = false, testsPassed = false, results = results.Select(r => new { r.Name, r.Passed, r.Message }), next = "The tests do not pass on this PC, so the tool was not approved. Reject it with the reasons, or ask the author to fix the tests." };
        var approved = store.Approve(p, Resolve.Who().Display, results);
        var audit = Resolve.Audit("script_tool_review", $"{name}|{sha}", "approved");
        return new { applied = true, approved = true, name, sha256 = sha, testsPassed = true, audit, next = "Tell the user the tool is now available to everyone (script_tool_find, script_tool_run)." };
    }
}

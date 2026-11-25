using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Scripting;

public sealed record ScriptParam(string Name, string Type = "string", string Description = "", bool Required = true);

/// <summary>A test the tool must pass: the arguments (plain text, so it can be run on any PC), and what the result must contain (or that it must fail).</summary>
public sealed record ScriptTest(string Name, Dictionary<string, string> Args, string? ExpectContains = null, bool ExpectError = false);

public sealed record ScriptToolDef(string Name, string Description, List<ScriptParam> Parameters, List<ScriptTest> Tests, string Author = "", string EmployeeCode = "", string Created = "", int Revision = 1, string CodeSha256 = "");

public sealed record ScriptTestResult(string Name, bool Passed, string Message, string Output);

public sealed record ScriptApproval(string ApprovedBy, string Ts, string CodeSha256, int Revision);

public sealed record StoredScriptTool(ScriptToolDef Def, string Code, string Folder, ScriptApproval? Approval = null, string Author = "", IReadOnlyList<ScriptTestResult>? Results = null, string? RejectedReason = null)
{
    /// <summary>True when the code on disk is exactly what was approved (nothing else is ever run for other people).</summary>
    public bool ApprovalValid => Approval is not null && Approval.CodeSha256 == ScriptToolStore.Sha256(Code);
}

/// <summary>
/// Tools that Claude wrote: C# scripts with tests. The life cycle keeps one person's code from running on everyone's PC unreviewed: a DRAFT lives on the author's PC; PUBLISHING copies it
/// to the shared folder as PENDING; the QC lead (or an administrator) reads the code and its tests, the tests are run again on the lead's PC, and APPROVING copies it to
/// <c>approved\&lt;name&gt;</c> with its SHA-256. Only approved code whose hash still matches runs on other PCs.
/// </summary>
public sealed class ScriptToolStore(Settings settings)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static readonly Regex NameRule = new("^[a-z][a-z0-9_]{2,40}$", RegexOptions.Compiled);

    public string DraftsDir => Path.Combine(Settings.DataDirectory, "script-tools", "drafts");
    public string SharedDir => Path.Combine(settings.EffectiveToolsDirectory, "scripts");
    public string PendingDir => Path.Combine(SharedDir, "pending");
    public string ApprovedDir => Path.Combine(SharedDir, "approved");

    public static string Sha256(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("\r\n", "\n")))).ToLowerInvariant();

    public static List<string> Validate(ScriptToolDef d, IReadOnlySet<string> builtInNames)
    {
        var e = new List<string>();
        if (!NameRule.IsMatch(d.Name)) e.Add("Name: lowercase letters, digits and _, 3 to 41 characters, starting with a letter.");
        if (builtInNames.Contains(d.Name)) e.Add($"'{d.Name}' is the name of a built-in tool: choose another.");
        if (string.IsNullOrWhiteSpace(d.Description)) e.Add("A description is needed: when to use the tool.");
        var names = d.Parameters.Select(p => p.Name).ToList();
        if (names.Any(n => !Regex.IsMatch(n, "^[A-Za-z][A-Za-z0-9_]{0,40}$"))) e.Add("Parameter names: letters, digits and _, starting with a letter.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) e.Add("Parameter names must be different.");
        if (d.Tests.Count == 0) e.Add("At least one test is needed. A test gives the arguments as plain text (not file paths: it must run on any PC) and what the result must contain.");
        foreach (var t in d.Tests)
        {
            if (!t.ExpectError && string.IsNullOrEmpty(t.ExpectContains)) e.Add($"Test '{t.Name}': say what the result must contain (expectContains), or that it must fail (expectError).");
            foreach (var p in d.Parameters.Where(p => p.Required)) if (!t.Args.Keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && !t.ExpectError) e.Add($"Test '{t.Name}' does not give the required argument '{p.Name}'.");
        }
        return e;
    }

    private static void WriteTool(string folder, ScriptToolDef def, string code, IEnumerable<ScriptTestResult>? results)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tool.json"), JsonSerializer.Serialize(def, Json));
        File.WriteAllText(Path.Combine(folder, "code.cs"), code);
        if (results is not null) File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(results, Json));
    }

    private static StoredScriptTool? ReadTool(string folder, string author = "")
    {
        try
        {
            var tj = Path.Combine(folder, "tool.json"); var cs = Path.Combine(folder, "code.cs");
            if (!File.Exists(tj) || !File.Exists(cs)) return null;
            var def = JsonSerializer.Deserialize<ScriptToolDef>(File.ReadAllText(tj), Json);
            if (def is null) return null;
            ScriptApproval? ap = null; var af = Path.Combine(folder, "approval.json");
            if (File.Exists(af)) ap = JsonSerializer.Deserialize<ScriptApproval>(File.ReadAllText(af), Json);
            IReadOnlyList<ScriptTestResult>? res = null; var rf = Path.Combine(folder, "results.json");
            if (File.Exists(rf)) res = JsonSerializer.Deserialize<List<ScriptTestResult>>(File.ReadAllText(rf), Json);
            string? rej = null; var jf = Path.Combine(folder, "rejected.txt");
            if (File.Exists(jf)) rej = File.ReadAllText(jf);
            return new StoredScriptTool(def, File.ReadAllText(cs), folder, ap, author, res, rej);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // ----- drafts (this PC)
    public StoredScriptTool SaveDraft(ScriptToolDef def, string code, IEnumerable<ScriptTestResult>? results)
    {
        var d = def with { Created = def.Created.Length > 0 ? def.Created : DateTime.UtcNow.ToString("s") + "Z", CodeSha256 = Sha256(code) };
        var prev = Draft(d.Name);
        if (prev is not null) d = d with { Revision = prev.Def.Revision + (prev.Def.CodeSha256 == d.CodeSha256 ? 0 : 1) };
        var folder = Path.Combine(DraftsDir, d.Name);
        WriteTool(folder, d, code, results);
        return ReadTool(folder)!;
    }

    public StoredScriptTool? Draft(string name) => NameRule.IsMatch(name) ? ReadTool(Path.Combine(DraftsDir, name)) : null;
    public IReadOnlyList<StoredScriptTool> Drafts() => !Directory.Exists(DraftsDir) ? [] : Directory.GetDirectories(DraftsDir).Select(d => ReadTool(d)).OfType<StoredScriptTool>().ToList();

    // ----- shared: pending and approved
    public string Publish(StoredScriptTool draft, UserIdentity who, IEnumerable<ScriptTestResult> results)
    {
        var folder = Path.Combine(PendingDir, who.FileKey, draft.Def.Name);
        // a new revision is a new folder name suffix so an earlier pending copy is never overwritten with different code
        if (File.Exists(Path.Combine(folder, "code.cs")) && Sha256(File.ReadAllText(Path.Combine(folder, "code.cs"))) != Sha256(draft.Code)) folder += "-r" + draft.Def.Revision;
        WriteTool(folder, draft.Def with { Author = who.Name, EmployeeCode = who.EmployeeCode }, draft.Code, results);
        return folder;
    }

    public IReadOnlyList<StoredScriptTool> Pending()
    {
        if (!Directory.Exists(PendingDir)) return [];
        var approved = Approved().ToDictionary(a => a.Def.Name + "|" + a.Approval!.CodeSha256, a => a);
        var list = new List<StoredScriptTool>();
        foreach (var authorDir in Directory.GetDirectories(PendingDir))
            foreach (var d in Directory.GetDirectories(authorDir))
                if (ReadTool(d, Path.GetFileName(authorDir)) is { } t && t.RejectedReason is null && !approved.ContainsKey(t.Def.Name + "|" + ScriptToolStore.Sha256(t.Code))) list.Add(t);
        return list.OrderBy(t => t.Def.Created).ToList();
    }

    public StoredScriptTool? PendingTool(string author, string name) =>
        Pending().FirstOrDefault(t => t.Def.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (author.Length == 0 || t.Author.Equals(author, StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<StoredScriptTool> Approved() =>
        !Directory.Exists(ApprovedDir) ? [] : Directory.GetDirectories(ApprovedDir).Where(d => !Path.GetFileName(d).Contains(".bak_")).Select(d => ReadTool(d)).OfType<StoredScriptTool>().Where(t => t.Approval is not null).ToList();

    public StoredScriptTool? ApprovedTool(string name) => Approved().FirstOrDefault(t => t.Def.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Approves a pending tool: copies it to approved\name with its hash and the approver. An earlier approved version is kept next to it as name.bak_time.</summary>
    public StoredScriptTool Approve(StoredScriptTool pending, string approver, IEnumerable<ScriptTestResult> results)
    {
        var target = Path.Combine(ApprovedDir, pending.Def.Name);
        if (Directory.Exists(target))
        {
            var bak = target + ".bak_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(bak);
            foreach (var f in Directory.GetFiles(target)) File.Copy(f, Path.Combine(bak, Path.GetFileName(f)), true);
        }
        var sha = Sha256(pending.Code);
        WriteTool(target, pending.Def with { CodeSha256 = sha }, pending.Code, results);
        File.WriteAllText(Path.Combine(target, "approval.json"), JsonSerializer.Serialize(new ScriptApproval(approver, DateTime.UtcNow.ToString("s") + "Z", sha, pending.Def.Revision), Json));
        try { File.Delete(Path.Combine(target, "rejected.txt")); } catch (IOException) { }
        return ReadTool(target)!;
    }

    public void Reject(StoredScriptTool pending, string by, string reason) =>
        File.WriteAllText(Path.Combine(pending.Folder, "rejected.txt"), $"{by}, {DateTime.Now:yyyy-MM-dd HH:mm}: {reason}");

    /// <summary>Approved tools (and, optionally, this user's drafts) whose name or description matches the words, best first.</summary>
    public IReadOnlyList<(StoredScriptTool Tool, string Where)> Find(string query, bool includeDrafts = true)
    {
        var words = Regex.Matches((query ?? "").ToLowerInvariant(), @"[a-z0-9]{3,}").Select(m => m.Value).Distinct().ToList();
        var all = Approved().Select(t => (Tool: t, Where: t.ApprovalValid ? "approved" : "approved (code changed since: not usable)"))
            .Concat(includeDrafts ? Drafts().Select(t => (Tool: t, Where: "draft on this PC")) : []);
        return all.Select(x => (x.Tool, x.Where, Score: words.Sum(w => ((x.Tool.Def.Name + " " + x.Tool.Def.Description).ToLowerInvariant().Split(w).Length - 1)))).Where(x => words.Count == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score).Select(x => (x.Tool, x.Where)).ToList();
    }
}

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Harness;
using Pec.Qc.Core.McpInstall;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

/// <summary>
/// CLI harnesses: when the user needs software driven that no tool does, Claude builds a small command-line program for it with the CLI-Anything method (the guide is shipped with the assistant),
/// tests it, and shares it on the office folder so every colleague can install and use it. Ready-made ones from the CLI-Hub list can be installed too.
/// </summary>
[McpServerToolType]
public static class HarnessTools
{
    /// <summary>For tests: a fake network and a fake process runner.</summary>
    public static IMcpHttp? Http { get; set; }
    public static IHarnessRunner? Runner { get; set; }
    private static HarnessEnv Env() => new(Http ?? new McpHttp(), Runner ?? new HarnessProcessRunner());
    private static IMcpHttp Net() => Http ?? new McpHttp();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The shipped methodology: app\cli-anything next to the assistant, or third-party\cli-anything in the source tree.</summary>
    public static string? GuideFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var rel in new[] { "cli-anything", Path.Combine("third-party", "cli-anything") })
            {
                var f = Path.Combine(d.FullName, rel);
                if (File.Exists(Path.Combine(f, "HARNESS.md"))) return f;
            }
        return null;
    }

    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["harness"] = "HARNESS.md", ["build"] = "commands/cli-anything.md", ["refine"] = "commands/refine.md", ["test"] = "commands/test.md", ["validate"] = "commands/validate.md",
        ["list"] = "commands/list.md", ["template-skill"] = "templates/SKILL.md.template", ["repl-skin"] = "repl_skin.py", ["publishing"] = "PUBLISHING.md", ["quickstart"] = "QUICKSTART.md",
        ["guide-mcp-backend"] = "guides/mcp-backend.md", ["guide-skill-generation"] = "guides/skill-generation.md", ["guide-auto-save-dry-run"] = "guides/auto-save-dry-run.md",
        ["guide-session-locking"] = "guides/session-locking.md", ["guide-filter-translation"] = "guides/filter-translation.md", ["guide-preview"] = "guides/preview-methodology.md",
    };

    [McpServerTool(Name = "harness_guide", ReadOnly = true)]
    [Description("The CLI-Anything method for building a command-line harness that lets Claude drive other software (read it BEFORE building one; the skill qc-cli-anything tells when). topic: 'harness' (the full method, HARNESS.md: read it all, in parts with offset), 'build' (the phases of the build command), 'refine', 'test', 'validate', 'template-skill' (SKILL.md template), 'repl-skin' (the shared terminal helper to copy into a harness), 'publishing', 'quickstart', or a guide: guide-mcp-backend, guide-skill-generation, guide-auto-save-dry-run, guide-session-locking, guide-filter-translation, guide-preview. No topic lists them. Long files come in parts: pass the offset the result gives.")]
    public static object Guide([Description("Topic name; empty lists the topics")] string topic = "", [Description("Character offset to continue from")] int offset = 0, [Description("Characters to return, 2000-30000")] int maxChars = 20000)
    {
        var folder = GuideFolder() ?? throw new McpException("The CLI-Anything guide is not installed with this copy of the assistant. Run Setup again to repair the installation.");
        if (string.IsNullOrWhiteSpace(topic)) return new { topics = Topics.Select(t => new { topic = t.Key, file = t.Value }), source = "CLI-Anything by HKUDS (Apache-2.0), shipped with the assistant", next = "Read 'harness' first, then 'build'." };
        if (!Topics.TryGetValue(topic.Trim(), out var rel)) throw new McpException("Unknown topic. Call harness_guide with no topic to list them.");
        var text = File.ReadAllText(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));
        var n = Math.Clamp(maxChars, 2000, 30000); var o = Math.Clamp(offset, 0, text.Length);
        var part = text.Substring(o, Math.Min(n, text.Length - o));
        var end = o + part.Length;
        return new { topic, file = rel, length = text.Length, offset = o, text = part, next = end < text.Length ? $"Continue with offset={end}." : "End of the file." };
    }

    private static string RegistryFallback() => Path.Combine(GuideFolder() ?? "", "registry.json");

    [McpServerTool(Name = "harness_find", ReadOnly = true)]
    [Description("Look for a ready command-line harness for a piece of software BEFORE building one: the ones colleagues shared on the office folder (and whether this PC has them installed), and the CLI-Hub list of ready-made CLIs from the CLI-Anything project (Blender, GIMP, LibreOffice, draw.io, FreeCAD, CloudCompare, QGIS ... and many more). Search by the software's name or what it does. Also check script_tool_find and the built-in tools: a harness is for driving OTHER software.")]
    public static async Task<object> Find([Description("Software name or what it should do")] string query)
    {
        var store = new HarnessStore(Resolve.Settings());
        var installed = store.InstalledHere().ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var words = (query ?? "").ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var shared = store.Shared().Where(h => words.Length == 0 || words.Any(w => (h.Name + " " + h.Description + " " + h.Software).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(h => new { h.Name, h.Description, h.Software, h.Version, h.Author, h.TestsPassed, installedHere = installed.ContainsKey(h.Name), where = "office folder" }).ToList();
        var (hub, from) = await CliHub.LoadAsync(Net(), RegistryFallback(), CancellationToken.None);
        var hits = CliHub.Search(hub, query ?? "").Take(12).Select(h => new { h.Name, h.DisplayName, h.Description, h.Requires, h.Category, h.Version, installedHere = installed.ContainsKey(h.Name), where = "CLI-Hub" }).ToList();
        return new
        {
            shared, hub = hits, hubSource = from,
            next = shared.Count + hits.Count == 0 ? "Nothing matches. If the user needs this and it will be needed again, follow the skill qc-cli-anything: build a harness (harness_guide first), test it (harness_test), share it (harness_publish)."
                                               : "Use harness_install (apply=false first) for the one that fits, then harness_run. A CLI-Hub entry needs the software it drives (see 'requires').",
        };
    }

    private static string NameOf(HubEntry h) => h.Name;

    [McpServerTool(Name = "harness_install", Destructive = true)]
    [Description("Install a harness on this PC into the assistant's private Python environment (nothing system-wide changes): a shared one from the office folder (its checksum is checked against the one recorded when it was shared) or a ready-made one from CLI-Hub (downloaded from the CLI-Anything project on GitHub, with its Python dependencies from PyPI). apply=false shows the plan; apply=true asks the user to approve, then installs. The first install also creates the environment (downloads uv and Python, about a minute).")]
    public static async Task<object> Install([Description("The harness name")] string name, [Description("auto, shared or hub")] string source = "auto", bool apply = false)
    {
        var store = new HarnessStore(Resolve.Settings());
        var sharedHit = source.Equals("hub", StringComparison.OrdinalIgnoreCase) ? null : store.SharedHarness(name);
        HubEntry? hubHit = null;
        if (sharedHit is null && !source.Equals("shared", StringComparison.OrdinalIgnoreCase))
        {
            var (hub, _) = await CliHub.LoadAsync(Net(), RegistryFallback(), CancellationToken.None);
            hubHit = hub.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        if (sharedHit is null && hubHit is null) throw new McpException($"There is no harness named '{name}' on the office folder or in CLI-Hub. harness_find lists what exists.");
        string? hubSpec = null;
        if (hubHit is not null) hubSpec = CliHub.ToPipSpec(hubHit.InstallCmd) ?? throw new McpException($"The install command of '{name}' in CLI-Hub is not one this assistant installs automatically ({hubHit.InstallCmd}).");
        var from = sharedHit is not null ? $"the office folder (shared by {sharedHit.Author})" : "CLI-Hub (the CLI-Anything project on GitHub)";
        if (!apply) return new { applied = false, name, from, entryPoint = sharedHit?.EntryPoint ?? hubHit!.EntryPoint, requires = hubHit?.Requires, next = "Show the user where it comes from and what it needs, then call again with apply=true." };
        var detail = sharedHit is not null ? $"Install the harness '{name}' shared by {sharedHit.Author} ({sharedHit.Description}). Checksum {sharedHit.Sha256}." : $"Install '{name}' from CLI-Hub: {hubSpec}\nIt downloads code from the CLI-Anything project and its Python dependencies from PyPI.";
        if (Resolve.NeedApproval("harness_install", name + "|" + (sharedHit?.Sha256 ?? hubSpec), $"Install the command-line harness '{name}' on this PC", detail) is { } wait) return wait;
        var log = new List<string>();
        var env = Env();
        try
        {
            string spec; string entry; string sha;
            if (sharedHit is not null) { var dir = store.Extract(sharedHit); spec = dir; entry = sharedHit.EntryPoint; sha = sharedHit.Sha256; }
            else { spec = hubSpec!; entry = hubHit!.EntryPoint; sha = "hub:" + hubSpec; }
            await env.InstallAsync(spec, editable: false, log.Add, CancellationToken.None);
            var exe = env.EntryExe(entry);
            if (exe is null && Runner is null) throw new McpException($"It installed but the command '{entry}' was not created. Details: {string.Join(" | ", log.TakeLast(3))}");
            store.MarkInstalled(new HarnessStore.Installed(name, entry, sha, sharedHit is not null ? "shared" : "hub", DateTime.Now.ToString("s")));
            return new { applied = true, name, entryPoint = entry, audit = Resolve.Audit("harness_install", name + "|" + sha, "installed"), next = "Read its SKILL.md for how to use it (shared ones: in the shared folder; hub ones: pip package data), then harness_run." };
        }
        catch (McpInstallException e) { throw new McpException(e.Message); }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "harness_skill", ReadOnly = true)]
    [Description("The SKILL.md of a shared harness: what its commands are and how to use them. Read it before harness_run.")]
    public static object Skill([Description("The harness name")] string name)
    {
        var h = new HarnessStore(Resolve.Settings()).SharedHarness(name) ?? throw new McpException($"There is no shared harness named '{name}'. (CLI-Hub ones are used with their --help and --json options.)");
        var f = Path.Combine(h.Folder, "SKILL.md");
        return new { name, description = h.Description, software = h.Software, requires = h.Requires, entryPoint = h.EntryPoint, skill = File.Exists(f) ? File.ReadAllText(f) : "(no SKILL.md)" };
    }

    [McpServerTool(Name = "harness_run", Destructive = true)]
    [Description("Run an installed harness command: the harness's own command-line program (named cli-anything-<software>) with the arguments you give as a JSON array of text, e.g. [\"--json\",\"project\",\"info\"]. Always use --json when the harness offers it. The user approves each run (they see the full command): a harness drives other software and may change that software's files, so say what it will do first and never use one to change a model, drawing or client document in a QC task. Returns the exit code and the output.")]
    public static async Task<object> Run([Description("The installed harness name")] string name, [Description("JSON array of arguments")] string args = "[]", [Description("Seconds, 1-600")] int timeoutSeconds = 120, [Description("Working folder (default: the assistant's workspace)")] string? workDir = null)
    {
        var store = new HarnessStore(Resolve.Settings());
        var inst = store.InstalledHere().FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new McpException($"'{name}' is not installed on this PC. Use harness_install.");
        string[] a;
        try { a = JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(args) ? "[]" : args, Json) ?? []; }
        catch (JsonException e) { throw new McpException("args must be a JSON array of text: " + e.Message); }
        var env = Env();
        var exe = env.EntryExe(inst.EntryPoint) ?? (Runner is not null ? Path.Combine(env.Scripts, inst.EntryPoint + ".exe") : throw new McpException($"The command '{inst.EntryPoint}' was not found in the environment: install it again."));
        var cmd = inst.EntryPoint + " " + string.Join(" ", a.Select(x => x.Contains(' ') ? "\"" + x + "\"" : x));
        if (Resolve.NeedApproval("harness_run", name + "|" + string.Join("\u001f", a), $"Run the harness command: {cmd}", "This runs " + inst.EntryPoint + " on this PC. It may start or change other software and files.") is { } wait) return wait;
        var cwd = workDir is { Length: > 0 } && Directory.Exists(workDir) ? workDir : null;
        var r = await env.RunAsync(exe, a, cwd, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)), CancellationToken.None);
        var text = r.Output.Length > 30000 ? r.Output[..30000] + "\n...(cut)" : r.Output;
        return new { ran = true, name, command = cmd, exitCode = r.ExitCode, timedOut = r.TimedOut, output = text, audit = Resolve.Audit("harness_run", name + "|" + string.Join(" ", a), r.TimedOut ? "timed out" : "exit " + r.ExitCode),
            next = r.ExitCode == 0 ? "Give the user the result (and say what the command changed, if anything)." : "Tell the user what failed; read the harness's SKILL.md again before retrying." };
    }

    [McpServerTool(Name = "harness_test", Destructive = true)]
    [Description("Check and TEST a harness you built in a folder (for example harness-work\\<software>\\agent-harness): it must be an installable Python package with a command cli-anything-<software> and a SKILL.md. apply=false checks the structure only; apply=true asks the user to approve, installs it (editable) into the private environment with pytest and runs its tests. Publishing needs a passing test of exactly this content.")]
    public static async Task<object> Test([Description("The harness folder (it holds setup.py or pyproject.toml)")] string path, bool apply = false)
    {
        var folder = Path.GetFullPath(path.Trim().Trim('"'));
        var check = HarnessStore.Check(folder);
        if (!check.Ok) return new { tested = false, ok = false, problems = check.Problems, next = "Fix these, then call again." };
        if (!apply) return new { tested = false, ok = true, name = check.Name, entryPoint = check.EntryPoint, skill = check.SkillMd, next = "The structure is right. Call again with apply=true to install it and run its tests (the user approves)." };
        var sha = HarnessStore.HashFolder(folder);
        if (Resolve.NeedApproval("harness_test", folder + "|" + sha, $"Install and test the harness '{check.Name}' you built", "It is installed into the assistant's private Python environment (with pytest) and its tests run on this PC.") is { } wait) return wait;
        var env = Env(); var log = new List<string>();
        try { await env.InstallAsync(folder, editable: true, log.Add, CancellationToken.None, "pytest"); }
        catch (McpInstallException e) { throw new McpException(e.Message); }
        var run = await env.RunAsync(env.Python, ["-m", "pytest", "-q", "-x", "--no-header", "-p", "no:cacheprovider"], folder, TimeSpan.FromMinutes(10), CancellationToken.None);
        var passed = run.ExitCode == 0 && !run.TimedOut;
        HarnessStore.RecordTest(folder, sha, passed);
        var tail = HarnessEnv.Tail(run.Output);
        return new { tested = true, passed, exitCode = run.ExitCode, timedOut = run.TimedOut, output = tail, sha256 = sha, next = passed ? "The tests pass. Show the user the result, then offer harness_publish." : "The tests fail: fix the harness and test again. Do not publish it." };
    }

    [McpServerTool(Name = "harness_publish", Destructive = true)]
    [Description("Share a tested harness with every colleague through the office folder (tools\\harnesses\\<software>): the package, its SKILL.md and its checksum. It must have passed harness_test with exactly this content. apply=false shows what would be shared; apply=true asks the user to approve and shares it (when the office requires the QC lead's review of tools, only a QC lead can). Colleagues then find it with harness_find and install it with harness_install.")]
    public static object Publish([Description("The harness folder")] string path, [Description("One or two sentences: what software it drives and what it can do")] string description, [Description("The software it drives, e.g. 'Draw.io'")] string software, [Description("What must be installed for it to work, e.g. 'Draw.io desktop 24+'; empty if nothing")] string requires = "", bool apply = false)
    {
        var s = Resolve.Settings(); var store = new HarnessStore(s);
        var folder = Path.GetFullPath(path.Trim().Trim('"'));
        var check = HarnessStore.Check(folder);
        if (!check.Ok) throw new McpException("The harness is not ready: " + string.Join(" ", check.Problems));
        var sha = HarnessStore.HashFolder(folder);
        if (!HarnessStore.PassedTests(folder, sha)) throw new McpException("This content has not passed harness_test (or was changed since). Test it first.");
        if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(software)) throw new McpException("Give a description and the software name.");
        if (!s.HasShared) throw new McpException("No shared folder is set: there is nowhere to share it. Set it in Settings.");
        if (s.ScriptReviewByLead && Resolve.Require(QcRole.Lead, "Sharing a harness (the office requires the QC lead's review of tools)") is { } denied) return denied;
        var who = Resolve.Who();
        var existing = store.SharedHarness(check.Name);
        if (!apply) return new { published = false, name = check.Name, entryPoint = check.EntryPoint, replaces = existing is null ? null : $"version shared by {existing.Author}", wouldWriteTo = Path.Combine(store.SharedDir, check.Name), next = "Call again with apply=true: the user approves and it is shared with every colleague." };
        if (Resolve.NeedApproval("harness_publish", check.Name + "|" + sha, $"Share the harness '{check.Name}' with every colleague", $"{description.Trim()}\nSoftware: {software}\nRequires: {requires}\nCommand: {check.EntryPoint}\nTested here: yes (content hash {sha[..12]})") is { } wait) return wait;
        var info = new HarnessInfo(check.Name, description.Trim(), software.Trim(), check.EntryPoint, DateTime.Now.ToString("yyyy.MM.dd.HHmm"), who.Name, who.EmployeeCode, DateTime.Now.ToString("s"), "", "built", requires.Trim(), true);
        string dir;
        try { dir = store.Publish(folder, info, check); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { throw new McpException(e.Message); }
        return new { published = true, name = check.Name, folder = dir, audit = Resolve.Audit("harness_publish", check.Name + "|" + sha, "shared"), next = "Tell the user it is shared: colleagues use harness_find and harness_install. It is new code that was tested only by its own tests." };
    }
}

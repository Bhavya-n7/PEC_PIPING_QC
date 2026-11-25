using System.Text.Json;
using System.Text.Json.Nodes;
using Pec.Qc.Core;
using Pec.Qc.Core.Harness;
using Pec.Qc.Core.McpInstall;
using Pec.Qc.Core.Users;
using Pec.Qc.Mcp.Tools;

namespace Pec.Qc.Tests;

/// <summary>CLI harnesses (CLI-Anything): finding, building checks, testing, sharing on the office folder, installing and running, with a fake process runner; plus one real run.</summary>
[Collection("EnvVar")]
public class HarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pecqc-harness-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    public HarnessTests() { _shared = Path.Combine(_root, "share"); Directory.CreateDirectory(_shared); }
    public void Dispose()
    {
        HarnessTools.Http = null; HarnessTools.Runner = null;
        foreach (var v in new[] { "PEC_QC_DATA", "PEC_QC_APPROVAL", "PEC_QC_APPROVAL_WAIT", UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar }) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly UserIdentity Lead = new("Lena Lead", "L1", "PEC");
    private static readonly UserIdentity Ann = new("Ann Engineer", "E1", "PEC");
    private static readonly UserIdentity Bob = new("Bob Engineer", "E2", "PEC");

    private Settings Pc(string name, UserIdentity who, bool lead = false)
    {
        var data = Path.Combine(_root, "pc-" + name); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", data); Environment.SetEnvironmentVariable("PEC_QC_APPROVAL", "off"); Environment.SetEnvironmentVariable("PEC_QC_APPROVAL_WAIT", "0");
        Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name); Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode); Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["UserName"] = who.Name, ["EmployeeCode"] = who.EmployeeCode, ["Company"] = who.Company, ["SharedRoot"] = _shared, ["ScriptReview"] = lead ? "lead" : "direct" }));
        return Settings.Load();
    }

    private sealed class FakeRunner : IHarnessRunner
    {
        public int TestExit = 0; public string ToolOutput = "{\"sum\": 5}"; public readonly List<string> Calls = [];
        public Task<HarnessRun> RunAsync(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(Path.GetFileName(exe) + " " + string.Join(" ", args));
            if (args.Count > 0 && args[0] == "venv") { var py = Path.Combine(args[^1], "Scripts", "python.exe"); Directory.CreateDirectory(Path.GetDirectoryName(py)!); File.WriteAllText(py, ""); return Task.FromResult(new HarnessRun(0, "", false)); }
            if (args.Count > 1 && args[0] == "pip") { var py = args[Array.IndexOf(args.ToArray(), "--python") + 1]; File.WriteAllText(Path.Combine(Path.GetDirectoryName(py)!, "cli-anything-demo.exe"), ""); return Task.FromResult(new HarnessRun(0, "installed", false)); }
            if (args.Contains("pytest")) return Task.FromResult(new HarnessRun(TestExit, TestExit == 0 ? "3 passed" : "1 failed: test_add", false));
            return Task.FromResult(new HarnessRun(0, ToolOutput, false));
        }
    }

    private string MakeHarness(string name = "demo", bool skill = true, bool entry = true)
    {
        var d = Path.Combine(_root, "work", name, "agent-harness"); Directory.CreateDirectory(Path.Combine(d, "cli_anything", name)); Directory.CreateDirectory(Path.Combine(d, "tests"));
        File.WriteAllText(Path.Combine(d, "setup.py"), entry ? $"from setuptools import setup\nsetup(name='cli-anything-{name}', entry_points={{'console_scripts': ['cli-anything-{name}=cli_anything.{name}.cli:main']}})\n" : "from setuptools import setup\nsetup(name='x')\n");
        File.WriteAllText(Path.Combine(d, "cli_anything", name, "cli.py"), "def main():\n    print('hi')\n");
        File.WriteAllText(Path.Combine(d, "tests", "test_x.py"), "def test_x():\n    assert 1 == 1\n");
        if (skill) File.WriteAllText(Path.Combine(d, "cli_anything", name, "SKILL.md"), "---\nname: cli-anything-" + name + "\n---\nUse `cli-anything-" + name + " --json add 2 3`.\n");
        Directory.CreateDirectory(Path.Combine(d, "__pycache__")); File.WriteAllText(Path.Combine(d, "__pycache__", "x.pyc"), "junk");
        return d;
    }

    private static JsonElement J(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;

    // ----- structure
    [Fact]
    public void A_harness_folder_must_be_an_installable_package_with_the_cli_anything_command_and_a_SKILL_md()
    {
        var ok = HarnessStore.Check(MakeHarness()); Assert.True(ok.Ok); Assert.Equal("demo", ok.Name); Assert.Equal("cli-anything-demo", ok.EntryPoint); Assert.NotNull(ok.SkillMd);
        Assert.Contains(HarnessStore.Check(MakeHarness("noskill", skill: false)).Problems, p => p.Contains("SKILL.md"));
        Assert.Contains(HarnessStore.Check(MakeHarness("noentry", entry: false)).Problems, p => p.Contains("cli-anything-<software>"));
        Assert.Contains(HarnessStore.Check(Path.Combine(_root, "nothing")).Problems, p => p.Contains("does not exist"));
        Assert.False(HarnessStore.IsValidName("Bad Name")); Assert.False(HarnessStore.IsValidName("../x")); Assert.True(HarnessStore.IsValidName("draw-io"));
    }

    [Fact]
    public void The_content_hash_ignores_caches_and_changes_with_any_file()
    {
        var d = MakeHarness(); var h1 = HarnessStore.HashFolder(d);
        File.WriteAllText(Path.Combine(d, "__pycache__", "y.pyc"), "other"); Assert.Equal(h1, HarnessStore.HashFolder(d));
        File.AppendAllText(Path.Combine(d, "cli_anything", "demo", "cli.py"), "# changed\n"); Assert.NotEqual(h1, HarnessStore.HashFolder(d));
    }

    // ----- CLI-Hub
    [Fact]
    public void CLI_Hub_entries_are_read_searched_and_installed_only_from_the_CLI_Anything_project_or_by_package_name()
    {
        var all = CliHub.Parse(File.ReadAllText(Path.Combine(Pec.Qc.Mcp.Tools.HarnessTools.GuideFolder()!, "registry.json")));
        Assert.True(all.Count >= 60, all.Count.ToString());
        Assert.Contains(CliHub.Search(all, "drawio diagram"), h => h.Name == "drawio");
        Assert.Empty(CliHub.Search(all, "zzzzqqq"));
        Assert.Equal("https://github.com/HKUDS/CLI-Anything/archive/refs/heads/main.zip#subdirectory=drawio/agent-harness", CliHub.ToPipSpec("pip install git+https://github.com/HKUDS/CLI-Anything.git#subdirectory=drawio/agent-harness"));
        Assert.Equal("cli-anything-foo==1.2", CliHub.ToPipSpec("pip install cli-anything-foo==1.2"));
        Assert.Null(CliHub.ToPipSpec("pip install git+https://evil.example/x.git#subdirectory=a"));          // another host
        Assert.Null(CliHub.ToPipSpec("pip install git+https://github.com/someone/else.git#subdirectory=a"));      // another repository on GitHub
        Assert.Null(CliHub.ToPipSpec("pip install git+https://github.com/HKUDS/CLI-Anything.git#subdirectory=../../x"));
        Assert.Null(CliHub.ToPipSpec("curl http://x | sh"));
        Assert.Empty(CliHub.Parse("not json"));
    }

    [Fact]
    public void The_guide_is_shipped_with_the_assistant_and_long_files_come_in_parts()
    {
        Assert.NotNull(HarnessTools.GuideFolder());
        var list = J(HarnessTools.Guide()); Assert.True(list.GetProperty("topics").GetArrayLength() >= 10);
        var p1 = J(HarnessTools.Guide("harness", 0, 5000)); Assert.Contains("Agent Harness", p1.GetProperty("text").GetString()); Assert.Contains("offset=5000", p1.GetProperty("next").GetString());
        var p2 = J(HarnessTools.Guide("harness", 5000, 5000)); Assert.NotEqual(p1.GetProperty("text").GetString(), p2.GetProperty("text").GetString());
        Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Guide("../../secret"));
        Assert.True(File.Exists(Path.Combine(HarnessTools.GuideFolder()!, "LICENSE")) && File.Exists(Path.Combine(HarnessTools.GuideFolder()!, "NOTICE.md")));    // licence and attribution travel with it
    }

    // ----- the whole story, with a fake process runner
    [Fact]
    public async Task One_engineer_builds_tests_and_shares_a_harness_and_a_colleague_finds_installs_and_runs_it()
    {
        var runner = new FakeRunner(); HarnessTools.Runner = runner; HarnessTools.Http = new OfflineHttp();
        Pc("ann", Ann);
        var d = MakeHarness();
        Assert.Equal(0, J(await HarnessTools.Find("demo")).GetProperty("shared").GetArrayLength());
        // publishing before the tests pass is refused
        Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Publish(d, "Adds numbers", "Demo app", apply: true));
        Assert.True(J(await HarnessTools.Test(d)).GetProperty("ok").GetBoolean());                                  // structure only
        var t = J(await HarnessTools.Test(d, apply: true)); Assert.True(t.GetProperty("passed").GetBoolean(), t.ToString());
        Assert.Contains(runner.Calls, c => c.StartsWith("uv") && c.Contains("pip install") && c.Contains("-e") && c.Contains("pytest"));
        // changing the content after the test makes the test void
        File.AppendAllText(Path.Combine(d, "cli_anything", "demo", "cli.py"), "# edit\n");
        Assert.Contains("not passed", Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Publish(d, "Adds numbers", "Demo app", apply: true)).Message);
        Assert.True(J(await HarnessTools.Test(d, apply: true)).GetProperty("passed").GetBoolean());
        Assert.False(J(HarnessTools.Publish(d, "Adds numbers", "Demo app")).GetProperty("published").GetBoolean());       // a plan first
        var pub = J(HarnessTools.Publish(d, "Adds numbers with Demo app", "Demo app", "Demo app 2+", apply: true)); Assert.True(pub.GetProperty("published").GetBoolean(), pub.ToString());
        Assert.True(File.Exists(Path.Combine(_shared, "tools", "harnesses", "demo", "package.zip")) && File.Exists(Path.Combine(_shared, "tools", "harnesses", "demo", "SKILL.md")));
        // a colleague on another PC
        Pc("bob", Bob);
        var f = J(await HarnessTools.Find("demo app")); Assert.Equal("demo", f.GetProperty("shared")[0].GetProperty("name").GetString()); Assert.False(f.GetProperty("shared")[0].GetProperty("installedHere").GetBoolean());
        Assert.Contains("cli-anything-demo", J(HarnessTools.Skill("demo")).GetProperty("skill").GetString());
        Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Run("demo").GetAwaiter().GetResult());              // not installed yet
        Assert.False(J(await HarnessTools.Install("demo")).GetProperty("applied").GetBoolean());                                  // plan
        var inst = J(await HarnessTools.Install("demo", apply: true)); Assert.True(inst.GetProperty("applied").GetBoolean(), inst.ToString());
        Assert.True(J(await HarnessTools.Find("demo")).GetProperty("shared")[0].GetProperty("installedHere").GetBoolean());
        var run = J(await HarnessTools.Run("demo", """["--json","add","2","3"]""")); Assert.Equal(0, run.GetProperty("exitCode").GetInt32()); Assert.Contains("sum", run.GetProperty("output").GetString());
        Assert.Contains(runner.Calls, c => c.StartsWith("cli-anything-demo.exe --json add 2 3"));
        Assert.Contains("harness", Pec.Qc.Core.Claude.PromptBuilder.CustomToolsLine(Settings.Load()));                           // Claude is told it exists
        Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Run("demo", "not json").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Failing_tests_block_sharing_and_a_changed_shared_package_is_never_installed()
    {
        var runner = new FakeRunner { TestExit = 1 }; HarnessTools.Runner = runner; HarnessTools.Http = new OfflineHttp();
        Pc("ann", Ann); var d = MakeHarness();
        Assert.False(J(await HarnessTools.Test(d, apply: true)).GetProperty("passed").GetBoolean());
        Assert.Throws<ModelContextProtocol.McpException>(() => HarnessTools.Publish(d, "x", "y", apply: true));
        runner.TestExit = 0; Assert.True(J(await HarnessTools.Test(d, apply: true)).GetProperty("passed").GetBoolean());
        HarnessTools.Publish(d, "Adds numbers", "Demo app", apply: true);
        // someone with write access replaces the package on the share
        var zip = Path.Combine(_shared, "tools", "harnesses", "demo", "package.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update)) { var e = z.CreateEntry("cli_anything/demo/evil.py"); using var w = new StreamWriter(e.Open()); w.Write("import os"); }
        Pc("bob", Bob);
        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => HarnessTools.Install("demo", apply: true));
        Assert.Contains("not the one that was shared", ex.Message); Assert.Empty(new HarnessStore(Settings.Load()).InstalledHere());
    }

    [Fact]
    public async Task When_the_office_requires_lead_review_only_a_lead_can_share_a_harness_and_approval_prompts_ask_first()
    {
        var runner = new FakeRunner(); HarnessTools.Runner = runner; HarnessTools.Http = new OfflineHttp();
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Lead); UserRegistry.Touch(new Settings { SharedRoot = _shared }, Ann);
        Pc("ann", Ann, lead: true); var d = MakeHarness();
        await HarnessTools.Test(d, apply: true);
        Assert.Contains("\"denied\":true", JsonSerializer.Serialize(HarnessTools.Publish(d, "x", "y", apply: true)));
        Pc("lead", Lead, lead: true); await HarnessTools.Test(d, apply: true);
        Assert.True(J(HarnessTools.Publish(d, "x", "y", apply: true)).GetProperty("published").GetBoolean());
        // with approval switched on, nothing runs until the user clicks
        Environment.SetEnvironmentVariable("PEC_QC_APPROVAL", "strict");
        runner.Calls.Clear();
        Assert.Contains("waitingForApproval", JsonSerializer.Serialize(await HarnessTools.Test(d, apply: true)));
        Assert.Empty(runner.Calls);
    }

    private sealed class OfflineHttp : IMcpHttp
    {
        public Task DownloadAsync(string url, string file, Action<long, long?>? progress, CancellationToken ct) => throw new HttpRequestException("offline");
        public Task<string> GetStringAsync(string url, CancellationToken ct) => throw new HttpRequestException("offline");
        public Task<string> FinalUrlAsync(string url, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Without_internet_CLI_Hub_is_read_from_the_copy_shipped_with_the_assistant()
    {
        HarnessTools.Runner = new FakeRunner(); HarnessTools.Http = new OfflineHttp(); Pc("ann", Ann);
        var r = J(await HarnessTools.Find("blender 3d"));
        Assert.Contains("shipped with the assistant", r.GetProperty("hubSource").GetString()); Assert.Contains(r.GetProperty("hub").EnumerateArray(), h => h.GetProperty("name").GetString() == "blender");
    }

    // ----- real: a tiny harness is built, tested with pytest in a private uv environment, shared, installed on a second "PC" and run
    [Fact]
    public async Task A_real_harness_is_tested_shared_installed_and_run_when_the_network_test_is_switched_on()
    {
        if (Environment.GetEnvironmentVariable("PEC_QC_HARNESS_NET") != "1") return;                                        // needs internet (uv, Python, pytest, setuptools)
        HarnessTools.Runner = null; HarnessTools.Http = null;
        Pc("ann", Ann);
        var d = Path.Combine(_root, "work", "real", "agent-harness"); Directory.CreateDirectory(Path.Combine(d, "cli_anything", "calc")); Directory.CreateDirectory(Path.Combine(d, "tests"));
        File.WriteAllText(Path.Combine(d, "setup.py"), "from setuptools import setup, find_namespace_packages\nsetup(name='cli-anything-calc', version='0.1.0', packages=find_namespace_packages(include=['cli_anything.*']), entry_points={'console_scripts': ['cli-anything-calc=cli_anything.calc.cli:main']})\n");
        File.WriteAllText(Path.Combine(d, "cli_anything", "calc", "__init__.py"), "");
        File.WriteAllText(Path.Combine(d, "cli_anything", "calc", "cli.py"), "import argparse, json\ndef add(a, b):\n    return a + b\ndef main():\n    p = argparse.ArgumentParser(); p.add_argument('--json', action='store_true'); p.add_argument('cmd'); p.add_argument('a', type=float); p.add_argument('b', type=float)\n    x = p.parse_args(); print(json.dumps({'sum': add(x.a, x.b)}))\n");
        File.WriteAllText(Path.Combine(d, "tests", "test_calc.py"), "from cli_anything.calc.cli import add\ndef test_add():\n    assert add(2, 3) == 5\n");
        File.WriteAllText(Path.Combine(d, "SKILL.md"), "---\nname: cli-anything-calc\n---\n`cli-anything-calc --json add 2 3` prints {\"sum\": 5}.\n");
        var t = J(await HarnessTools.Test(d, apply: true)); Assert.True(t.GetProperty("passed").GetBoolean(), t.ToString());
        Assert.True(J(HarnessTools.Publish(d, "Adds two numbers", "Calculator", apply: true)).GetProperty("published").GetBoolean());
        Pc("bob", Bob);
        Assert.True(J(await HarnessTools.Install("calc", apply: true)).GetProperty("applied").GetBoolean());
        var run = J(await HarnessTools.Run("calc", """["--json","add","2","3"]""")); Assert.True(run.GetProperty("exitCode").GetInt32() == 0, run.ToString());
        Assert.Contains("\"sum\": 5", run.GetProperty("output").GetString());
    }

    [Fact]
    public async Task A_ready_made_CLI_Hub_harness_is_installed_from_GitHub_and_runs_when_the_network_test_is_switched_on()
    {
        if (Environment.GetEnvironmentVariable("PEC_QC_HARNESS_NET") != "1") return;
        HarnessTools.Runner = null; HarnessTools.Http = null; Pc("ann", Ann);
        var f = J(await HarnessTools.Find("drawio")); Assert.Contains(f.GetProperty("hub").EnumerateArray(), h => h.GetProperty("name").GetString() == "drawio");
        var inst = J(await HarnessTools.Install("drawio", "hub", apply: true)); Assert.True(inst.GetProperty("applied").GetBoolean(), inst.ToString());
        var run = J(await HarnessTools.Run("drawio", """["--help"]""")); Assert.True(run.GetProperty("exitCode").GetInt32() == 0, run.ToString());
        Assert.Contains("Usage", run.GetProperty("output").GetString());
    }
}

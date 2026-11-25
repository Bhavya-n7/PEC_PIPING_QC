using Pec.Qc.Mcp.Scripting;

namespace Pec.Qc.Tests;

/// <summary>What a tool written by Claude may and may not do, and that it runs in its own process with a time limit.</summary>
[Collection("EnvVar")]
public class ScriptEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-script-" + Guid.NewGuid().ToString("N"));
    public ScriptEngineTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { Environment.SetEnvironmentVariable(ScriptEngine.HostVariable, null); try { Directory.Delete(_dir, true); } catch { } }

    private static string BuiltHost()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var exe = Path.Combine(d.FullName, "src", "Pec.Qc.Mcp", "bin", "Debug", "net10.0", "win-x64", "Pec.Qc.Mcp.exe");
            if (File.Exists(exe)) return exe;
        }
        throw new FileNotFoundException("The built Pec.Qc.Mcp.exe was not found: build the solution first.");
    }

    private ScriptRun Here(string code, Dictionary<string, string>? args = null) => ScriptEngine.RunHere(code, args ?? new(), [], Path.Combine(_dir, "out"));

    // ----- allowed
    [Fact]
    public void A_normal_tool_uses_arguments_linq_regex_and_json_and_returns_a_value()
    {
        var r = Here("""
            var nums = Regex.Matches(Args["text"], @"\d+").Select(m => int.Parse(m.Value)).ToList();
            return new { count = nums.Count, total = nums.Sum(), max = nums.Max() };
            """, new() { ["text"] = "12 apples, 30 pears and 8 plums" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("""{"count":3,"total":50,"max":30}""", r.Output);
    }

    [Fact]
    public void A_tool_reads_the_file_it_was_given_and_writes_a_result_file_into_the_output_folder_only()
    {
        var input = Path.Combine(_dir, "in.csv"); File.WriteAllText(input, "a,b\n1,2\n3,4\n");
        var r = Here("""
            var lines = Qc.ReadLines(Args["file"]);
            var path = Qc.WriteText("result.txt", string.Join("|", lines));
            Qc.Log("done");
            return path;
            """, new() { ["file"] = input });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.Combine(_dir, "out", "result.txt"), r.Output);
        Assert.Equal("a,b|1,2|3,4", File.ReadAllText(r.Output));
        Assert.Contains("done", r.Logs);
    }

    [Fact]
    public void A_tool_cannot_read_a_file_it_was_not_given_or_write_a_file_type_that_could_run()
    {
        var secret = Path.Combine(_dir, "secret.txt"); File.WriteAllText(secret, "private");
        var other = Path.Combine(_dir, "given.txt"); File.WriteAllText(other, "ok");
        var r = Here("return Qc.ReadText(@\"" + secret + "\");", new() { ["file"] = other });
        Assert.False(r.Ok); Assert.Contains("may only read the files it was given", r.Error);
        var w = Here("return Qc.WriteText(\"run.bat\", \"calc\");");
        Assert.False(w.Ok); Assert.Contains(".txt, .csv", w.Error);
        var traversal = Here("return Qc.WriteText(@\"..\\..\\evil.txt\", \"x\");");
        Assert.True(traversal.Ok); Assert.StartsWith(Path.Combine(_dir, "out"), traversal.Output);                // the folder part is dropped: it still lands in the output folder
    }

    // ----- refused
    [Theory]
    [InlineData("""return System.IO.File.ReadAllText("c:\\x.txt");""", "System.IO.File")]
    [InlineData("""return System.IO.Directory.GetFiles("c:\\").Length;""", "System.IO.Directory")]
    [InlineData("""System.Diagnostics.Process.Start("calc.exe"); return 1;""", "System.Diagnostics.Process")]
    [InlineData("""using var h = new System.Net.Http.HttpClient(); return 1;""", "System.Net")]
    [InlineData("""return Environment.GetEnvironmentVariable("USERNAME");""", "System.Environment")]
    [InlineData("""return System.Reflection.Assembly.GetExecutingAssembly().FullName;""", "System.Reflection")]
    [InlineData("""return "x".GetType().GetMethods().Length;""", "GetType")]
    [InlineData("""return Activator.CreateInstance(typeof(string)) != null;""", "System.Activator")]
    [InlineData("""Console.WriteLine("hi"); return 1;""", "System.Console")]
    [InlineData("""dynamic d = 1; return d + 1;""", "dynamic")]
    [InlineData("""unsafe { int x = 1; int* p = &x; return *p; }""", "unsafe")]
    [InlineData("""var t = new System.Threading.Thread(() => { }); t.Start(); return 1;""", "System.Threading.Thread")]
    [InlineData("""return Microsoft.Win32.Registry.CurrentUser.Name;""", "Microsoft.Win32")]
    [InlineData("""using var f = new System.IO.FileStream("a", System.IO.FileMode.Create); return 1;""", "System.IO.FileStream")]
    public void Code_that_reaches_outside_the_sandbox_is_refused_with_the_reason(string code, string reasonContains)
    {
        var c = ScriptEngine.Analyze(code);
        Assert.False(c.Ok, "was allowed: " + code);
        // refused by the policy (with the reason and the line), or earlier: the library it needs is not one a tool is given, so it does not compile
        Assert.True(c.Violations.Any(v => v.What.Contains(reasonContains, StringComparison.Ordinal)) || c.Errors.Count > 0, "refused without a reason");
        Assert.All(c.Violations, v => Assert.True(v.Line >= 1));
        var run = Here(code);
        Assert.False(run.Ok); Assert.True(run.Error.Contains("may not use") || run.Error.Contains("does not compile"), run.Error);        // and it is not run
    }

    [Fact]
    public void Native_code_and_extra_libraries_are_refused()
    {
        Assert.False(ScriptEngine.Analyze("""
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int MessageBox(int h, string t, string c, int f);
            return 1;
            """).Ok);
        Assert.False(ScriptEngine.Analyze("""
            #r "C:\\x\\evil.dll"
            return 1;
            """).Ok);
    }

    [Fact]
    public void A_compile_error_is_reported_with_the_line_and_nothing_is_run()
    {
        var c = ScriptEngine.Analyze("var x = 1;\nreturn y + 1;");
        Assert.False(c.Ok);
        Assert.Contains(c.Errors, e => e.StartsWith("line 2:"));
        var r = Here("var x = ;");
        Assert.False(r.Ok); Assert.Contains("does not compile", r.Error);
    }

    [Fact]
    public void An_exception_in_the_tool_is_a_failed_run_with_the_message_not_a_crash()
    {
        var r = Here("""return int.Parse(Args["n"]);""", new() { ["n"] = "abc" });
        Assert.False(r.Ok); Assert.Contains("FormatException", r.Error);
    }

    // ----- its own process, with a time limit
    [Fact]
    public void A_tool_runs_in_its_own_process_and_returns_its_result()
    {
        Environment.SetEnvironmentVariable(ScriptEngine.HostVariable, BuiltHost());
        var r = ScriptEngine.Execute("""return Args["a"] + "-" + Args["b"];""", new Dictionary<string, string> { ["a"] = "x", ["b"] = "y" }, [], Path.Combine(_dir, "out"), TimeSpan.FromSeconds(60));
        Assert.True(r.Ok, r.Error); Assert.Equal("x-y", r.Output);
    }

    [Fact]
    public void A_tool_that_never_ends_is_stopped_at_the_time_limit_and_the_server_is_fine()
    {
        Environment.SetEnvironmentVariable(ScriptEngine.HostVariable, BuiltHost());
        // warm the host first so the limit measures the loop, not the start-up
        Assert.True(ScriptEngine.Execute("return 1;", new Dictionary<string, string>(), [], Path.Combine(_dir, "out"), TimeSpan.FromSeconds(60)).Ok);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = ScriptEngine.Execute("while (true) { }", new Dictionary<string, string>(), [], Path.Combine(_dir, "out"), TimeSpan.FromSeconds(1));
        Assert.False(r.Ok); Assert.Contains("was stopped", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Empty(System.Diagnostics.Process.GetProcessesByName("Pec.Qc.Mcp").Where(p => { try { return p.StartTime > DateTime.Now.AddSeconds(-20) && p.MainModule?.FileName == BuiltHost() && p.Responding is false; } catch { return false; } }));
    }

    [Fact]
    public void Tests_pass_or_fail_by_what_the_result_contains_and_by_expected_errors()
    {
        Environment.SetEnvironmentVariable(ScriptEngine.HostVariable, BuiltHost());
        var def = new Pec.Qc.Core.Scripting.ScriptToolDef("sum_numbers", "adds the numbers in a text", [new("text")],
        [
            new("adds", new() { ["text"] = "1 2 3" }, "6"),
            new("wrong expectation", new() { ["text"] = "1 2 3" }, "7"),
            new("bad input fails", new() { ["text"] = "abc" }, ExpectError: true),
            new("bad input should not fail", new() { ["text"] = "1" }, ExpectError: true),
        ]);
        var res = ScriptEngine.RunTests(def, """return Regex.Matches(Args["text"], @"\d+").Select(m => int.Parse(m.Value)).Sum() is var s && s > 0 ? s.ToString() : throw new Exception("no numbers");""", [], Path.Combine(_dir, "out"));
        Assert.Equal(new[] { true, false, true, false }, res.Select(r => r.Passed).ToArray());
        Assert.Contains("does not contain '7'", res[1].Message);
    }
}

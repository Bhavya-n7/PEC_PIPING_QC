using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Pec.Qc.Core.Scripting;

namespace Pec.Qc.Mcp.Scripting;

public sealed record ScriptCheck(bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<ScriptViolation> Violations);

public sealed record ScriptRun(bool Ok, string Output, string Error, IReadOnlyList<string> Logs, IReadOnlyList<string> Files, long Milliseconds, IReadOnlyList<ScriptViolation>? Violations = null);

/// <summary>Compiles, checks and runs the C# scripts of the tools Claude writes. A run happens in a separate process of this same program (so a loop can be stopped and a crash cannot take the server down).</summary>
public static class ScriptEngine
{
    public const string HostVariable = "PEC_QC_SCRIPT_HOST";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    private static ScriptOptions Options()
    {
        // the libraries a tool may reference: the core library (needed for everything), collections, LINQ, regex, JSON and this assembly (the Qc object); what it may DO is decided by the policy
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string Find(string name) => tpa.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Framework library not found: " + name);
        var refs = new[] { "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Text.RegularExpressions", "System.Text.Json", "System.Memory", "System.Runtime.Numerics", "System.Collections.Concurrent" }
            .Select(n => MetadataReference.CreateFromFile(Find(n))).Append(MetadataReference.CreateFromFile(typeof(ScriptGlobals).Assembly.Location));
        return ScriptOptions.Default.WithReferences(refs).WithImports("System", "System.Linq", "System.Collections.Generic", "System.Text", "System.Text.RegularExpressions", "System.Text.Json", "System.Globalization", "Pec.Qc.Mcp.Scripting")
            .WithLanguageVersion(LanguageVersion.Latest);
    }

    /// <summary>Compiles the code and applies the policy. Nothing is run.</summary>
    public static ScriptCheck Analyze(string code)
    {
        try
        {
            var script = CSharpScript.Create(code, Options(), typeof(ScriptGlobals));
            var comp = script.GetCompilation();
            var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()} ({d.Id})").Take(20).ToList();
            if (errors.Count > 0) return new ScriptCheck(false, errors, []);
            var violations = ScriptPolicy.Check(comp, comp.SyntaxTrees.First());
            return new ScriptCheck(violations.Count == 0, [], violations);
        }
        catch (Exception e) when (e is CompilationErrorException or InvalidOperationException) { return new ScriptCheck(false, [e.Message], []); }
    }

    private sealed record Job(string Code, Dictionary<string, string> Args, List<string> ReadableFolders, string OutDir);

    // ----- in the child process
    /// <summary>Entry of <c>Pec.Qc.Mcp.exe --run-script job.json</c>: checks and runs the script and writes the result as one JSON line on stdout.</summary>
    public static int ChildMain(string jobFile)
    {
        ScriptRun result;
        try
        {
            var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(jobFile), Json)!;
            result = RunHere(job.Code, job.Args, job.ReadableFolders, job.OutDir);
        }
        catch (Exception e) { result = new ScriptRun(false, "", "The script host failed: " + e.Message, [], [], 0); }
        Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
        return result.Ok ? 0 : 1;
    }

    /// <summary>Runs in THIS process (the child, and tests). The policy is applied again here: the parent's check is not trusted.</summary>
    public static ScriptRun RunHere(string code, IReadOnlyDictionary<string, string> args, IReadOnlyList<string> readableFolders, string outDir)
    {
        var sw = Stopwatch.StartNew();
        var check = Analyze(code);
        if (!check.Ok)
            return new ScriptRun(false, "", check.Errors.Count > 0 ? "The code does not compile: " + string.Join("; ", check.Errors) : "The code uses something a tool may not use: " + string.Join("; ", check.Violations.Select(v => $"line {v.Line}: {v.What}")), [], [], sw.ElapsedMilliseconds, check.Violations);
        // the files named in the arguments are the files the user chose: those may be read
        var files = args.Values.Where(v => v.Length is > 2 and < 520 && !v.Contains('\n')).Select(v => { try { return Path.GetFullPath(v.Trim().Trim('"')); } catch { return ""; } }).Where(f => f.Length > 0 && File.Exists(f)).ToList();
        var api = new QcApi(files, readableFolders.Select(Path.GetFullPath).ToList(), Path.GetFullPath(outDir));
        try
        {
            var state = CSharpScript.RunAsync(code, Options(), new ScriptGlobals { Args = new Dictionary<string, string>(args, StringComparer.OrdinalIgnoreCase), Qc = api }, typeof(ScriptGlobals)).GetAwaiter().GetResult();
            var rv = state.ReturnValue;
            var text = rv switch { null => "", string s => s, _ => JsonSerializer.Serialize(rv, rv.GetType(), Json) };
            if (text.Length > 200_000) text = text[..200_000] + "…(cut)";
            return new ScriptRun(true, text, "", api.Logs, api.Written, sw.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            var inner = e is AggregateException a ? a.InnerException ?? e : e;
            return new ScriptRun(false, "", inner.GetType().Name + ": " + inner.Message, api.Logs, api.Written, sw.ElapsedMilliseconds);
        }
    }

    // ----- in the server: start the child
    private static string HostExe() => Environment.GetEnvironmentVariable(HostVariable) is { Length: > 0 } h && File.Exists(h) ? h : Environment.ProcessPath ?? throw new InvalidOperationException("The server's own program file is not known.");

    /// <summary>Runs the script in a separate process with a time limit (30 s by default, 120 s at most); the process is stopped when the time is up.</summary>
    public static ScriptRun Execute(string code, IReadOnlyDictionary<string, string> args, IReadOnlyList<string> readableFolders, string outDir, TimeSpan? timeout = null)
    {
        var limit = TimeSpan.FromSeconds(Math.Clamp((timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds, 1, 120));
        var jobFile = Path.Combine(Path.GetTempPath(), "pecqc-script-" + Guid.NewGuid().ToString("N") + ".json");
        var sw = Stopwatch.StartNew();
        try
        {
            File.WriteAllText(jobFile, JsonSerializer.Serialize(new Job(code, new Dictionary<string, string>(args), readableFolders.ToList(), outDir), Json));
            var psi = new ProcessStartInfo(HostExe()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--run-script"); psi.ArgumentList.Add(jobFile);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("The script host did not start.");
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)limit.TotalMilliseconds))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return new ScriptRun(false, "", $"The tool ran longer than {limit.TotalSeconds:0} seconds and was stopped.", [], [], sw.ElapsedMilliseconds);
            }
            var line = stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (string.IsNullOrEmpty(line)) return new ScriptRun(false, "", "The script host gave no result: " + stderr.GetAwaiter().GetResult().Trim(), [], [], sw.ElapsedMilliseconds);
            return JsonSerializer.Deserialize<ScriptRun>(line, Json) ?? new ScriptRun(false, "", "The script host's result could not be read.", [], [], sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new ScriptRun(false, "", "The script could not be run: " + e.Message, [], [], sw.ElapsedMilliseconds);
        }
        finally { try { File.Delete(jobFile); } catch (IOException) { } }
    }

    /// <summary>Runs a tool's tests, each in its own process.</summary>
    public static List<ScriptTestResult> RunTests(ScriptToolDef def, string code, IReadOnlyList<string> readableFolders, string outDir)
    {
        var results = new List<ScriptTestResult>();
        foreach (var t in def.Tests)
        {
            var r = Execute(code, t.Args, readableFolders, outDir, TimeSpan.FromSeconds(30));
            bool passed; string msg;
            if (t.ExpectError) { passed = !r.Ok; msg = passed ? "failed as expected" : "it should have failed but returned a result"; }
            else if (!r.Ok) { passed = false; msg = r.Error; }
            else if (!string.IsNullOrEmpty(t.ExpectContains) && !r.Output.Contains(t.ExpectContains, StringComparison.Ordinal)) { passed = false; msg = $"the result does not contain '{t.ExpectContains}'"; }
            else { passed = true; msg = "ok"; }
            results.Add(new ScriptTestResult(t.Name, passed, msg, r.Output.Length > 500 ? r.Output[..500] + "…" : r.Output));
        }
        return results;
    }
}

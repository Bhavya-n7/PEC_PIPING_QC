using System.Diagnostics;
using Pec.Qc.Core.Install;

namespace Pec.Qc.Core.Model;

public sealed record NavisExportResult(bool Ok, string Message, string? SnapshotPath, TimeSpan Elapsed, IReadOnlyList<string> Progress);

/// <summary>
/// Runs the Navisworks export: Pec.Qc.NavisRunner.exe (a .NET Framework program, because Navisworks' own API is) starts a hidden Navisworks on a COPY of the model,
/// the exporter plugin writes the snapshot, Navisworks closes. The user's open Navisworks and the original model file are not touched; nothing is saved.
/// </summary>
public static class NavisExportRunner
{
    public const string RunnerExe = "Pec.Qc.NavisRunner.exe";
    public const string PluginDll = "PecQcExporter.dll";

    /// <summary>The folder holding the runner and the plugin: next to the installed app (navis), or the build output when run from the source tree. Null when this install has none.</summary>
    public static string? FindToolsFolder()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PEC_QC_NAVIS_TOOLS");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);
        var b = AppContext.BaseDirectory.TrimEnd('\\', '/');
        candidates.Add(Path.Combine(b, "navis"));
        candidates.Add(Path.Combine(Path.GetDirectoryName(b) ?? b, "navis"));
        // from the source tree: src\Pec.Qc.Mcp\bin\Debug\net10.0\win-x64 -> src\Pec.Qc.NavisRunner\bin\*\net48
        for (var d = new DirectoryInfo(b); d is not null; d = d.Parent)
        {
            var src = Path.Combine(d.FullName, "src");
            if (!Directory.Exists(src)) continue;
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var runner = Path.Combine(src, "Pec.Qc.NavisRunner", "bin", cfg, "net48");
                var plugin = Path.Combine(src, "Pec.Qc.NavisExport", "bin", cfg, "net48");
                if (File.Exists(Path.Combine(runner, RunnerExe)) && File.Exists(Path.Combine(plugin, PluginDll))) return Stage(runner, plugin);
            }
            break;
        }
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, RunnerExe)) && File.Exists(Path.Combine(c, PluginDll)));
    }

    // both parts side by side in a temp folder, so the runner finds the plugin next to it like in an install
    private static string Stage(string runner, string plugin)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pecqc-navis-tools");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(runner)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        foreach (var f in Directory.GetFiles(plugin, "PecQcExporter.*")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        return dir;
    }

    /// <summary>Exports the model to a snapshot JSON file. Blocks until Navisworks has finished (a few minutes for a big model) or <paramref name="timeout"/> has passed.</summary>
    public static NavisExportResult Export(NavisworksInstall navis, string modelFile, string outFile, TimeSpan timeout, Action<string>? progress = null)
    {
        var started = DateTime.UtcNow;
        var log = new List<string>();
        var tools = FindToolsFolder();
        if (tools is null) return Fail("This installation has no Navisworks export tool (the 'navis' folder is missing). Install the latest version of the QC Assistant.");
        if (!File.Exists(modelFile)) return Fail($"The model file does not exist: {modelFile}");
        var ext = Path.GetExtension(modelFile).ToLowerInvariant();
        if (ext is not (".nwd" or ".nwf" or ".nwc")) return Fail("Give a Navisworks file (.nwd, .nwf or .nwc).");
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!); }
        catch (Exception e) { return Fail("Cannot write to the output folder: " + e.Message); }

        var psi = new ProcessStartInfo(Path.Combine(tools, RunnerExe))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = tools,
        };
        foreach (var a in new[] { "--navis", navis.Root, "--model", modelFile, "--out", outFile, "--plugin", Path.Combine(tools, PluginDll) }) psi.ArgumentList.Add(a);
        var err = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) { log.Add(l); progress?.Invoke(l); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) err.Add(l); };
        try { p.Start(); }
        catch (Exception e) { return Fail("Could not start the export tool: " + e.Message); }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            KillStrayNavisworks(p.StartTime);
            return Fail($"The export did not finish in {timeout.TotalMinutes:0} minutes and was stopped. A very large model may need a longer time.");
        }
        p.WaitForExit();
        var elapsed = DateTime.UtcNow - started;
        if (p.ExitCode == 0 && File.Exists(outFile)) return new(true, "Exported.", outFile, elapsed, log);
        var why = err.Count > 0 ? string.Join(" ", err).Replace("ERROR ", "") : $"the export tool stopped with code {p.ExitCode}";
        return Fail(why);

        NavisExportResult Fail(string m) => new(false, m, null, DateTime.UtcNow - started, log);
    }

    // after a kill the hidden Navisworks the runner started can be left running: stop those started after the runner did
    private static void KillStrayNavisworks(DateTime since)
    {
        foreach (var pr in Process.GetProcessesByName("Roamer"))
        {
            try { if (pr.StartTime >= since.AddSeconds(-1) && pr.MainWindowHandle == IntPtr.Zero) pr.Kill(true); } catch { }
        }
    }
}

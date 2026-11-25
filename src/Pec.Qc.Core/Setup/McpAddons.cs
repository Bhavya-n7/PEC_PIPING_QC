using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Setup;

public sealed record McpAddonDetect(string Folder, string? Contains = null);

/// <summary>One third-party MCP server that Setup can install for an Autodesk program, through the team's own one-click installer script (bundled unmodified).</summary>
public sealed record McpAddon(string Id, string Title, string Description, string File, string Sha256, IReadOnlyList<McpAddonDetect> Detect, string Source, bool ClosesApps);

/// <summary>What Setup shows for one installer: whether the Autodesk program it is for was found on this PC, and whether the script is the one the release was built with.</summary>
public sealed record McpAddonStatus(McpAddon Addon, bool Detected, string DetectedAs);

public sealed record McpAddonResult(string Id, bool Ran, int? ExitCode, string Message)
{
    public bool Ok => Ran && ExitCode == 0;
}

/// <summary>
/// The MCP servers of other products that the company wants on every PC next to the QC tools: AutoCAD, Civil 3D, Plant 3D, Navisworks and Revit. Each has its own installer
/// script (it downloads the server from its publisher, installs the plug-in into the Autodesk program and registers the server with Claude). Setup lists them next to
/// what it sets up, pre-ticks those whose program is on this PC, checks the script against the SHA-256 in the manifest the release was built with, and runs it in its
/// own window (the scripts ask questions and may close the Autodesk program and Claude). They are never run by an automatic update and never removed by an uninstall.
/// </summary>
public static class McpAddons
{
    public const string FolderName = "mcp-addons";
    public const string ManifestFile = "manifest.json";
    public const string ResultsFile = "installed.json";
    public const string DryRunVariable = "PEC_QC_ADDONS_DRYRUN";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private sealed record Manifest(int Version, string? Note, List<ManifestAddon>? Addons);
    private sealed record ManifestAddon(string Id, string Title, string Description, string File, string Sha256, List<McpAddonDetect>? Detect, string? Source, bool ClosesApps);

    private static IReadOnlyList<McpAddon> From(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(json, Json);
            return (m?.Addons ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.File) && a.File == Path.GetFileName(a.File))
                .Select(a => new McpAddon(a.Id, a.Title, a.Description, a.File, a.Sha256 ?? "", a.Detect ?? [], a.Source ?? "", a.ClosesApps)).ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The installers listed in a folder that holds manifest.json (the installed app's mcp-addons folder). Empty when there is none.</summary>
    public static IReadOnlyList<McpAddon> Load(string folder)
    {
        var f = Path.Combine(folder, ManifestFile);
        try { return File.Exists(f) ? From(File.ReadAllText(f)) : []; } catch (IOException) { return []; }
    }

    /// <summary>The installers listed in payload.zip, read without unpacking it (Setup shows them before it installs).</summary>
    public static IReadOnlyList<McpAddon> LoadFromZip(string zipPath)
    {
        try
        {
            using var z = ZipFile.OpenRead(zipPath);
            var e = z.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').Equals($"{FolderName}/{ManifestFile}", StringComparison.OrdinalIgnoreCase));
            if (e is null) return [];
            using var r = new StreamReader(e.Open());
            return From(r.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return []; }
    }

    public static string DefaultAutodeskRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");

    /// <summary>Is the Autodesk program this installer is for installed? (a folder under Program Files\Autodesk that matches a rule, and the sub-folder the rule asks for)</summary>
    public static McpAddonStatus Detect(McpAddon a, string? autodeskRoot = null)
    {
        var root = autodeskRoot ?? DefaultAutodeskRoot();
        if (Directory.Exists(root))
            foreach (var rule in a.Detect)
            {
                Regex rx;
                try { rx = new Regex(rule.Folder, RegexOptions.IgnoreCase); } catch (ArgumentException) { continue; }
                foreach (var d in Directory.EnumerateDirectories(root).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(d);
                    if (!rx.IsMatch(name)) continue;
                    if (rule.Contains is { Length: > 0 } sub && !Directory.Exists(Path.Combine(d, sub))) continue;
                    return new McpAddonStatus(a, true, name + (rule.Contains is { Length: > 0 } s ? " (" + s + ")" : ""));
                }
            }
        return new McpAddonStatus(a, false, "");
    }

    public static IReadOnlyList<McpAddonStatus> Statuses(IEnumerable<McpAddon> addons, string? autodeskRoot = null) => addons.Select(a => Detect(a, autodeskRoot)).ToList();

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>True only when the script in the folder is exactly the one the manifest names (nothing else is ever run).</summary>
    public static bool ScriptMatches(string folder, McpAddon a)
    {
        var f = Path.Combine(folder, a.File);
        return File.Exists(f) && a.Sha256.Length > 0 && string.Equals(Sha256Of(f), a.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool DryRun => Environment.GetEnvironmentVariable(DryRunVariable) is "1" or "true";

    /// <summary>
    /// Runs one installer in its own console window and waits for it (it asks questions). The script is checked first; a changed or missing script is refused. The result
    /// is added to <c>installed.json</c> next to the scripts. With PEC_QC_ADDONS_DRYRUN nothing is started.
    /// </summary>
    public static McpAddonResult Run(string folder, McpAddon a, Action<string>? log = null, TimeSpan? timeout = null, bool? dryRun = null)
    {
        log ??= _ => { };
        var script = Path.Combine(folder, a.File);
        if (!File.Exists(script)) return Record(folder, new McpAddonResult(a.Id, false, null, $"{a.Title}: the installer script {a.File} is missing from the package."));
        if (!ScriptMatches(folder, a)) return Record(folder, new McpAddonResult(a.Id, false, null, $"{a.Title}: the installer script does not match the one this release was built with, so it was not run."));
        if (dryRun ?? DryRun) return new McpAddonResult(a.Id, false, null, $"{a.Title}: would run {a.File} (dry run: nothing was started).");
        try
        {
            log($"{a.Title}: starting its installer in a new window. Answer its questions there; this window continues when it finishes.");
            using var p = Process.Start(new ProcessStartInfo(script) { UseShellExecute = true, WorkingDirectory = folder })
                ?? throw new InvalidOperationException("the installer did not start");
            if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(40)).TotalMilliseconds))
                return Record(folder, new McpAddonResult(a.Id, true, null, $"{a.Title}: still running after the time limit; Setup does not wait any longer. Check its window."));
            return Record(folder, new McpAddonResult(a.Id, true, p.ExitCode, p.ExitCode == 0 ? $"{a.Title}: finished." : $"{a.Title}: its installer reported a problem (exit code {p.ExitCode}). Its log is in your temp folder; run {a.File} again from {folder} after fixing it."));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Record(folder, new McpAddonResult(a.Id, false, null, $"{a.Title}: could not be started: {e.Message}"));
        }
    }

    private sealed record Stored(string Id, string When, bool Ran, int? ExitCode, string Message);

    private static McpAddonResult Record(string folder, McpAddonResult r)
    {
        try
        {
            var file = Path.Combine(folder, ResultsFile);
            var all = File.Exists(file) ? (JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(file), Json) ?? []) : [];
            all.Add(new Stored(r.Id, DateTime.Now.ToString("s"), r.Ran, r.ExitCode, r.Message));
            File.WriteAllText(file, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* the record is a convenience */ }
        return r;
    }

    /// <summary>The ids that were installed successfully at some time on this PC (from installed.json).</summary>
    public static IReadOnlySet<string> InstalledIds(string folder)
    {
        try
        {
            var file = Path.Combine(folder, ResultsFile);
            var all = File.Exists(file) ? JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(file), Json) ?? [] : [];
            return all.Where(s => s.Ran && s.ExitCode == 0).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException) { return new HashSet<string>(); }
    }
}

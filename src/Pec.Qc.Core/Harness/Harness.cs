using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pec.Qc.Core.McpInstall;

namespace Pec.Qc.Core.Harness;

/// <summary>A CLI harness: a small command-line program (made with the CLI-Anything method) that lets Claude drive another piece of software, with a SKILL.md that says how.</summary>
public sealed record HarnessInfo(string Name, string Description, string Software, string EntryPoint, string Version, string Author, string EmployeeCode, string Created, string Sha256, string Source = "built", string Requires = "", bool TestsPassed = false)
{
    public string Folder { get; init; } = "";
}

public sealed record HarnessCheck(bool Ok, IReadOnlyList<string> Problems, string Name, string EntryPoint, string? SkillMd);

/// <summary>Harnesses shared on the office folder (tools\harnesses\&lt;name&gt;) and what this PC has installed from them.</summary>
public sealed class HarnessStore(Settings settings)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Regex NameRule = new("^[a-z][a-z0-9_-]{1,40}$", RegexOptions.Compiled);
    private static readonly string[] SkipDirs = ["__pycache__", ".git", ".venv", "venv", "env", "node_modules", "dist", "build", ".pytest_cache", ".mypy_cache"];
    public const long MaxZipBytes = 50L * 1024 * 1024;

    public string SharedDir => Path.Combine(settings.EffectiveToolsDirectory, "harnesses");
    public static string LocalRoot => Path.Combine(Settings.DataDirectory, "harness");
    public static string LocalSrc(string name) => Path.Combine(LocalRoot, "src", name);
    private static string InstalledFile => Path.Combine(LocalRoot, "installed.json");

    public static bool IsValidName(string name) => NameRule.IsMatch(name ?? "");

    // ----- checking a harness folder before it is shared
    public static HarnessCheck Check(string folder)
    {
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return new HarnessCheck(false, ["The folder does not exist: " + folder], "", "", null);
        var setup = new[] { "setup.py", "pyproject.toml" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);
        if (setup is null) problems.Add("There is no setup.py or pyproject.toml: the harness must be an installable Python package (pip install -e .).");
        var entry = "";
        if (setup is not null) { var m = Regex.Match(File.ReadAllText(setup), @"cli-anything-([a-z0-9_\-]+)"); if (m.Success) entry = m.Value; else problems.Add("The package does not define a command named cli-anything-<software> (console_scripts entry point)."); }
        var skill = Directory.EnumerateFiles(folder, "SKILL.md", SearchOption.AllDirectories).FirstOrDefault(f => !f.Split(Path.DirectorySeparatorChar).Any(p => SkipDirs.Contains(p)));
        if (skill is null) problems.Add("There is no SKILL.md: Claude needs it to know how to use the commands.");
        var name = entry.StartsWith("cli-anything-") ? entry["cli-anything-".Length..] : "";
        if (name.Length > 0 && !IsValidName(name)) problems.Add($"The name '{name}' is not allowed (lowercase letters, digits, - and _, 2-41 characters).");
        return new HarnessCheck(problems.Count == 0, problems, name, entry, skill);
    }

    public static string HashFolder(string folder)
    {
        using var sha = SHA256.Create();
        foreach (var f in Files(folder).OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
            sha.TransformBlock(Encoding.UTF8.GetBytes(rel + "\n"), 0, Encoding.UTF8.GetByteCount(rel + "\n"), null, 0);
            var b = File.ReadAllBytes(f); sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static IEnumerable<string> Files(string folder)
    {
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(folder, f).Split(Path.DirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p) || p.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase))) continue;
            if (f.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    // ----- sharing
    public string Publish(string folder, HarnessInfo info, HarnessCheck check)
    {
        if (!settings.HasShared) throw new InvalidOperationException("No shared folder is set: there is nowhere to share it.");
        var dir = Path.Combine(SharedDir, info.Name); Directory.CreateDirectory(dir);
        var tmp = Path.Combine(Path.GetTempPath(), "pecqc-harness-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var f in Files(folder)) z.CreateEntryFromFile(f, Path.GetRelativePath(folder, f).Replace('\\', '/'), CompressionLevel.Optimal);
            if (new FileInfo(tmp).Length > MaxZipBytes) throw new InvalidDataException($"The harness is larger than {MaxZipBytes / 1048576} MB: remove data files and caches.");
            var sha = McpFiles.Sha256(tmp);
            var target = Path.Combine(dir, "package.zip");
            if (File.Exists(target)) { Directory.CreateDirectory(Path.Combine(dir, "old")); File.Copy(target, Path.Combine(dir, "old", $"package-{DateTime.Now:yyyyMMdd-HHmmss}.zip"), true); }
            File.Copy(tmp, target, true);
            if (check.SkillMd is not null) File.Copy(check.SkillMd, Path.Combine(dir, "SKILL.md"), true);
            File.WriteAllText(Path.Combine(dir, "harness.json"), JsonSerializer.Serialize(info with { Sha256 = sha }, Json));
            return dir;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    public IReadOnlyList<HarnessInfo> Shared()
    {
        var list = new List<HarnessInfo>();
        if (!Directory.Exists(SharedDir)) return list;
        foreach (var d in Directory.GetDirectories(SharedDir))
            try
            {
                var f = Path.Combine(d, "harness.json");
                if (File.Exists(f) && File.Exists(Path.Combine(d, "package.zip")) && JsonSerializer.Deserialize<HarnessInfo>(File.ReadAllText(f), Json) is { } i) list.Add(i with { Folder = d });
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        return list;
    }

    public HarnessInfo? SharedHarness(string name) => Shared().FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Unpacks a shared harness after checking its hash against the one recorded when it was shared. Returns the folder to pip-install.</summary>
    public string Extract(HarnessInfo info)
    {
        var zip = Path.Combine(info.Folder, "package.zip");
        if (!File.Exists(zip)) throw new FileNotFoundException("The shared package is missing.");
        if (!McpFiles.Sha256(zip).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The shared package of '{info.Name}' is not the one that was shared (its checksum differs): it was not installed. Tell the person who shared it.");
        var dest = LocalSrc(info.Name);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        McpFiles.Extract(zip, dest);
        return dest;
    }

    // ----- what this PC has installed
    public sealed record Installed(string Name, string EntryPoint, string Sha256, string Source, string When);

    public IReadOnlyList<Installed> InstalledHere()
    {
        try { return File.Exists(InstalledFile) ? JsonSerializer.Deserialize<List<Installed>>(File.ReadAllText(InstalledFile), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    public void MarkInstalled(Installed i)
    {
        var all = InstalledHere().Where(x => !x.Name.Equals(i.Name, StringComparison.OrdinalIgnoreCase)).Append(i).ToList();
        Directory.CreateDirectory(LocalRoot); File.WriteAllText(InstalledFile, JsonSerializer.Serialize(all, Json));
    }

    // ----- a harness that was tested here: publishing needs the tests to have passed on exactly this content
    private static string TestedFile => Path.Combine(LocalRoot, "tested.json");
    public static void RecordTest(string folder, string sha, bool passed)
    {
        var d = LoadTested(); d[Path.GetFullPath(folder)] = passed ? sha : "";
        Directory.CreateDirectory(LocalRoot); File.WriteAllText(TestedFile, JsonSerializer.Serialize(d, Json));
    }
    public static bool PassedTests(string folder, string sha) => LoadTested().TryGetValue(Path.GetFullPath(folder), out var s) && s.Length > 0 && s.Equals(sha, StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, string> LoadTested()
    {
        try { return File.Exists(TestedFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TestedFile), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }
}

public sealed record HubEntry(string Name, string DisplayName, string Version, string Description, string Requires, string Category, string InstallCmd, string EntryPoint, string? Homepage);

/// <summary>CLI-Hub: the list of ready-made CLI harnesses that the CLI-Anything project publishes (registry.json in its repository), read live with a copy shipped in the package as the fallback.</summary>
public static class CliHub
{
    public const string RegistryUrl = "https://raw.githubusercontent.com/HKUDS/CLI-Anything/main/registry.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<HubEntry> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("clis", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return arr.EnumerateArray().Select(e => new HubEntry(S(e, "name"), S(e, "display_name"), S(e, "version"), S(e, "description"), S(e, "requires"), S(e, "category"), S(e, "install_cmd"), S(e, "entry_point"), S(e, "homepage")))
                .Where(h => h.Name.Length > 0 && h.InstallCmd.Length > 0).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static async Task<(IReadOnlyList<HubEntry> Entries, string From)> LoadAsync(IMcpHttp http, string? fallbackFile, CancellationToken ct)
    {
        try { var live = Parse(await http.GetStringAsync(RegistryUrl, ct)); if (live.Count > 0) return (live, "the CLI-Hub registry on GitHub"); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { }
        if (fallbackFile is not null && File.Exists(fallbackFile)) return (Parse(File.ReadAllText(fallbackFile)), "the copy of the CLI-Hub registry shipped with the assistant (no internet)");
        return ([], "nothing (no internet and no copy)");
    }

    public static IReadOnlyList<HubEntry> Search(IReadOnlyList<HubEntry> all, string query)
    {
        var words = (query ?? "").ToLowerInvariant().Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return all;
        return all.Select(h => (h, score: words.Count(w => (h.Name + " " + h.DisplayName + " " + h.Description + " " + h.Category).Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.h.Name).Select(x => x.h).ToList();
    }

    /// <summary>"pip install git+https://github.com/HKUDS/CLI-Anything.git#subdirectory=x/agent-harness" becomes an address that pip can install without git: the repository's zip archive with the same sub-folder. Anything else is refused.</summary>
    public static string? ToPipSpec(string installCmd)
    {
        var m = Regex.Match(installCmd ?? "", @"^pip install git\+https://github\.com/HKUDS/CLI-Anything\.git(?:@([\w.\-]+))?#subdirectory=([\w./\-]+)$");
        if (m.Success && !m.Groups[2].Value.Contains("..")) return $"https://github.com/HKUDS/CLI-Anything/archive/{(m.Groups[1].Success ? m.Groups[1].Value : "refs/heads/main")}.zip#subdirectory={m.Groups[2].Value}";
        var p = Regex.Match(installCmd ?? "", @"^pip install ([A-Za-z0-9_.\-]+(?:==[\w.\-]+)?)$");
        return p.Success ? p.Groups[1].Value : null;
    }
}

public sealed record HarnessRun(int ExitCode, string Output, bool TimedOut);

public interface IHarnessRunner
{
    Task<HarnessRun> RunAsync(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct);
}

public sealed class HarnessProcessRunner : IHarnessRunner
{
    public async Task<HarnessRun> RunAsync(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workDir is not null) psi.WorkingDirectory = workDir;
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; psi.Environment["PYTHONUTF8"] = "1";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + Path.GetFileName(exe));
        p.StandardInput.Close();
        var sb = new StringBuilder(); var gate = new object();
        void Pump(StreamReader r) { string? l; while ((l = r.ReadLine()) is not null) lock (gate) { if (sb.Length < 400_000) sb.AppendLine(l); } }
        var t1 = Task.Run(() => Pump(p.StandardOutput)); var t2 = Task.Run(() => Pump(p.StandardError));
        using var reg = ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch (InvalidOperationException) { } await Task.WhenAll(t1, t2); return new HarnessRun(-1, sb.ToString(), true); }
        await Task.WhenAll(t1, t2);
        return new HarnessRun(p.ExitCode, sb.ToString(), false);
    }
}

/// <summary>A private Python environment for harnesses (built with uv, which also fetches Python itself), so nothing depends on the PC's own Python and nothing is installed system-wide.</summary>
public sealed class HarnessEnv(IMcpHttp http, IHarnessRunner runner, string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(HarnessStore.LocalRoot, "env");
    public string Python => Path.Combine(Root, "Scripts", "python.exe");
    public string Scripts => Path.Combine(Root, "Scripts");
    private static readonly Dictionary<string, string> UvEnv = new() { ["UV_SYSTEM_CERTS"] = "true", ["UV_NATIVE_TLS"] = "true", ["UV_PYTHON"] = "3.12" };

    public static string UvHome => Path.Combine(HarnessStore.LocalRoot, "uv");
    public static string? FindUv()
    {
        var local = Path.Combine(UvHome, "uv.exe"); if (File.Exists(local)) return local;
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var f in new[] { Path.Combine(up, ".local", "bin", "uv.exe"), Path.Combine(up, ".cargo", "bin", "uv.exe") }) if (File.Exists(f)) return f;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { var f = Path.Combine(dir.Trim('"'), "uv.exe"); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        return null;
    }

    /// <summary>uv, found or downloaded (checked against its published SHA-256) into the assistant's own folder.</summary>
    public async Task<string> EnsureUvAsync(Action<string> log, CancellationToken ct)
    {
        if (FindUv() is { } found) return found;
        const string url = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";
        log("downloading uv (the Python package tool)");
        var tmp = Path.Combine(Path.GetTempPath(), "pecqc-uv-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        try
        {
            var zip = Path.Combine(tmp, "uv.zip");
            await http.DownloadAsync(url, zip, null, ct);
            McpFiles.VerifyHash(zip, McpFiles.ExpectedHash(await http.GetStringAsync(url + ".sha256", ct)), "uv");
            var x = Path.Combine(tmp, "x"); McpFiles.Extract(zip, x);
            Directory.CreateDirectory(UvHome);
            foreach (var exe in new[] { "uv.exe", "uvx.exe" })
                if (Directory.EnumerateFiles(x, exe, SearchOption.AllDirectories).FirstOrDefault() is { } f) File.Copy(f, Path.Combine(UvHome, exe), true);
            return File.Exists(Path.Combine(UvHome, "uv.exe")) ? Path.Combine(UvHome, "uv.exe") : throw new McpInstallException("uv.exe was not found in the downloaded package.");
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }

    public async Task EnsureAsync(Action<string> log, CancellationToken ct)
    {
        if (File.Exists(Python)) return;
        var uv = await EnsureUvAsync(log, ct);
        log("creating the harness environment (Python 3.12; the first time this downloads Python, about a minute)");
        var r = await runner.RunAsync(uv, ["venv", "--python", "3.12", Root], null, UvEnv, TimeSpan.FromMinutes(10), ct);
        if (r.ExitCode != 0 || !File.Exists(Python)) throw new McpInstallException("The harness environment could not be created: " + Tail(r.Output));
    }

    /// <summary>pip-installs a package (a folder, a zip address with #subdirectory=, or a name) into the environment.</summary>
    public async Task InstallAsync(string spec, bool editable, Action<string> log, CancellationToken ct, params string[] extra)
    {
        await EnsureAsync(log, ct);
        var uv = FindUv() ?? throw new McpInstallException("uv is missing.");
        var args = new List<string> { "pip", "install", "--python", Python };
        if (editable) args.Add("-e");
        args.Add(spec); args.AddRange(extra);
        log("installing " + (spec.Length > 90 ? spec[..90] + "..." : spec));
        var r = await runner.RunAsync(uv, args, null, UvEnv, TimeSpan.FromMinutes(15), ct);
        if (r.ExitCode != 0) throw new McpInstallException("The installation failed: " + Tail(r.Output));
    }

    public string? EntryExe(string entryPoint) { var f = Path.Combine(Scripts, entryPoint + ".exe"); return File.Exists(f) ? f : null; }

    public Task<HarnessRun> RunAsync(string exe, IReadOnlyList<string> args, string? workDir, TimeSpan timeout, CancellationToken ct) => runner.RunAsync(exe, args, workDir, UvEnv, timeout, ct);

    public static string Tail(string s) { s = (s ?? "").Trim(); return s.Length > 1200 ? "..." + s[^1200..] : s; }
}

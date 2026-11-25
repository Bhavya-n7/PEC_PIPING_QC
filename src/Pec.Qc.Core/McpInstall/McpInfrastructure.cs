using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.McpInstall;

/// <summary>A step failed in a way the user can understand and act on (the message is shown as it is).</summary>
public sealed class McpInstallException(string message, bool needsClose = false) : Exception(message)
{
    /// <summary>The Autodesk program (or Claude) is open and has to be closed first.</summary>
    public bool NeedsClose { get; } = needsClose;
}

public interface IMcpHttp
{
    Task DownloadAsync(string url, string file, Action<long, long?>? progress, CancellationToken ct);
    Task<string> GetStringAsync(string url, CancellationToken ct);
    /// <summary>The address a request ends up at after redirects (GitHub's /releases/latest ends at /releases/tag/vX).</summary>
    Task<string> FinalUrlAsync(string url, CancellationToken ct);
}

public sealed class McpHttp : IMcpHttp
{
    private static readonly HttpClient Client = Make();
    private static HttpClient Make()
    {
        try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
        var c = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials }) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PEC-QC-Assistant-Setup/1.0");
        return c;
    }

    private static async Task<T> Retry<T>(Func<Task<T>> f, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await f(); }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    public Task DownloadAsync(string url, string file, Action<long, long?>? progress, CancellationToken ct) => Retry(async () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var r = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        var total = r.Content.Headers.ContentLength;
        await using var src = await r.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buf = new byte[1 << 16]; long done = 0; int n; var last = Environment.TickCount64;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n;
            if (progress is not null && Environment.TickCount64 - last > 250) { progress(done, total); last = Environment.TickCount64; }
        }
        progress?.Invoke(done, total);
        return true;
    }, ct);

    public Task<string> GetStringAsync(string url, CancellationToken ct) => Retry(async () =>
    {
        // some release files are served as octet-stream: decode the bytes ourselves
        var b = await Client.GetByteArrayAsync(url, ct);
        return Encoding.UTF8.GetString(b);
    }, ct);

    public Task<string> FinalUrlAsync(string url, CancellationToken ct) => Retry(async () =>
    {
        using var r = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        return r.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
    }, ct);
}

public sealed record McpProbeResult(bool Ok, string Server, int Tools, string Error);

public interface IMcpProbe
{
    Task<McpProbeResult> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Starts an MCP server and asks it what it is and which tools it has: the same first two messages any MCP client sends.</summary>
public sealed class McpProbe : IMcpProbe
{
    public async Task<McpProbeResult> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("did not start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return new McpProbeResult(false, "", 0, "The server could not be started: " + e.Message); }
        using var _ = p;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        var err = p.StandardError.ReadToEndAsync();
        try
        {
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"pec-qc-installer\",\"version\":\"1\"}}}");
            await p.StandardInput.FlushAsync();
            var r0 = await ReadReply(p, 0, cts.Token);
            if (r0 is null) return new McpProbeResult(false, "", 0, "No answer to the first message. " + Tail(err));
            var name = $"{r0["result"]?["serverInfo"]?["name"]} {r0["result"]?["serverInfo"]?["version"]}".Trim();
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");
            await p.StandardInput.FlushAsync();
            var r1 = await ReadReply(p, 1, cts.Token);
            var tools = r1?["result"]?["tools"] as JsonArray;
            return tools is null ? new McpProbeResult(false, name, 0, "The server did not list its tools. " + Tail(err)) : new McpProbeResult(true, name, tools.Count, "");
        }
        catch (OperationCanceledException) { return new McpProbeResult(false, "", 0, $"No answer within {timeout.TotalSeconds:0} seconds."); }
        catch (IOException e) { return new McpProbeResult(false, "", 0, e.Message); }
        finally { try { p.StandardInput.Close(); } catch (IOException) { } if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch (InvalidOperationException) { } } }
    }

    private static string Tail(Task<string> err) { try { var s = err.Wait(1500) ? err.Result.Trim() : ""; return s.Length > 300 ? s[^300..] : s; } catch { return ""; } }

    private static async Task<JsonNode?> ReadReply(Process p, int id, CancellationToken ct)
    {
        while (true)
        {
            var line = await p.StandardOutput.ReadLineAsync(ct);
            if (line is null) return null;
            line = line.Trim();
            if (!line.StartsWith('{')) continue;
            try { var n = JsonNode.Parse(line); if (n?["id"]?.ToString() == id.ToString()) return n; } catch (JsonException) { }
        }
    }
}

public sealed record HostApp(string Family, int Year, string Product, string Dir);

/// <summary>Everything an installer touches on the PC, so a test can point it at a temporary folder and fake the network.</summary>
public sealed class McpEnv
{
    public string AppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string Temp { get; init; } = Path.GetTempPath();
    /// <summary>Where the assistant keeps its own list of MCP servers (read by the chat).</summary>
    public string DataDir { get; init; } = Settings.DataDirectory;
    public IMcpHttp Http { get; init; } = new McpHttp();
    public IMcpProbe Probe { get; init; } = new McpProbe();
    public Func<string, bool> IsRunning { get; init; } = name => Process.GetProcessesByName(name).Length > 0;
    public Func<IReadOnlyList<HostApp>>? HostsOverride { get; init; }
    public string Plugins => Path.Combine(AppData, "Autodesk", "ApplicationPlugins");
    public string RegistryFile => Path.Combine(DataDir, "mcp-servers.json");

    public IReadOnlyList<HostApp> Hosts() => HostsOverride?.Invoke() ?? HostDetector.Find(ProgramFiles);
}

/// <summary>Which Autodesk programs are on this PC: AutoCAD and the programs built on it (Civil 3D, Plant 3D ...), Navisworks Manage and Revit.</summary>
public static class HostDetector
{
    private static readonly Dictionary<int, string> AcadSeries = new() { [2022] = "R24.1", [2023] = "R24.2", [2024] = "R24.3", [2025] = "R25.0", [2026] = "R25.1", [2027] = "R26.0" };

    public static IReadOnlyList<HostApp> Find(string programFiles)
    {
        var list = new List<HostApp>();
        var root = Path.Combine(programFiles, "Autodesk");
        for (var y = 2022; y <= 2027; y++)
        {
            var acad = Path.Combine(root, $"AutoCAD {y}");
            if (File.Exists(Path.Combine(acad, "acad.exe")))
            {
                var products = RegistryProducts(y);
                if (products.Count == 0) products.Add(File.Exists(Path.Combine(acad, "AeccDbMgd.dll")) ? "AutoCAD Civil 3D " + y : "AutoCAD " + y);
                foreach (var p in products) list.Add(new HostApp("autocad", y, p, acad));
            }
            var nav = Path.Combine(root, $"Navisworks Manage {y}");
            if (File.Exists(Path.Combine(nav, "Autodesk.Navisworks.Api.dll"))) list.Add(new HostApp("navisworks", y, "Navisworks Manage " + y, nav));
            var rev = Path.Combine(root, $"Revit {y}");
            if (File.Exists(Path.Combine(rev, "Revit.exe"))) list.Add(new HostApp("revit", y, "Revit " + y, rev));
        }
        return list;
    }

    private static List<string> RegistryProducts(int year)
    {
        var l = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows()) return l;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Autodesk\AutoCAD\{AcadSeries[year]}");
            if (key is null) return l;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("ProductName") is string n && n.Length > 0 && !l.Contains(n)) l.Add(n);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return l;
    }
}

/// <summary>Messages and progress of one installation, shown by the window and written to a log file.</summary>
public sealed class McpContext(McpEnv env, string stamp, Action<string> log, Action<string> step, Action<int> percent, CancellationToken ct)
{
    public McpEnv Env { get; } = env;
    public string Stamp { get; } = stamp;
    public CancellationToken Ct { get; } = ct;
    public void Info(string m) => log("   " + m);
    public void Ok(string m) => log("  ok  " + m);
    public void Warn(string m) => log("  !!  " + m);
    public void Step(string title) => step(title);
    public void Percent(int p) => percent(Math.Clamp(p, 0, 100));

    public async Task DownloadAsync(string url, string file, string label)
    {
        Info("downloading " + label);
        var last = -1;
        await Env.Http.DownloadAsync(url, file, (d, t) =>
        {
            if (t is > 0) { var p = (int)(d * 100 / t.Value); if (p != last) { last = p; Percent(p); } }
        }, Ct);
        Percent(100);
    }
}

public static class McpFiles
{
    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The expected hash for a file name from a checksum list ("hash  name" per line), or from a single-hash file.</summary>
    public static string? ExpectedHash(string text, string? fileName = null)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            var m = Regex.Match(line, @"^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$");
            if (!m.Success) continue;
            if (fileName is null || !m.Groups[2].Success || m.Groups[2].Value.Trim().Equals(fileName, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    public static void VerifyHash(string file, string? expected, string what)
    {
        if (expected is null) throw new McpInstallException($"No published checksum was found for {what}, so it was not installed.");
        if (!Sha256(file).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new McpInstallException($"The download of {what} does not match its published checksum (damaged or changed), so nothing was installed.");
    }

    /// <summary>Unzips without letting an entry write outside the target folder.</summary>
    public static void Extract(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zip);
        foreach (var e in z.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new McpInstallException("The package contains a file that would be written outside its folder: " + e.FullName);
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    public static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Replaces a folder with a new one: the old one is moved aside first (a running program keeps its files, a locked folder fails whole) and put back if the copy fails.</summary>
    public static void ReplaceDir(string source, string target, string stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staging = target + ".staging-" + stamp; var backup = target + ".backup-" + stamp;
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        CopyDir(source, staging);
        var moved = false;
        try
        {
            if (Directory.Exists(target)) { Directory.Move(target, backup); moved = true; }
            Directory.Move(staging, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (moved && !Directory.Exists(target)) Directory.Move(backup, target);
            try { Directory.Delete(staging, true); } catch (IOException) { }
            throw new McpInstallException("A file of the old installation is in use. Close the Autodesk program and Claude, then try again. (" + e.Message + ")", needsClose: true);
        }
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<string> LatestTagAsync(IMcpHttp http, string repo, string fallback, CancellationToken ct)
    {
        try
        {
            var final = await http.FinalUrlAsync($"https://github.com/{repo}/releases/latest", ct);
            var m = Regex.Match(final, @"/releases/tag/(v?\d+\.\d+\.\d+)");
            if (m.Success) return m.Groups[1].Value;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { }
        return fallback;
    }
}

/// <summary>The MCP servers the assistant's own chat may use (the ones installed here), kept in the data folder.</summary>
public sealed record McpServerEntry(string Command, string[]? Args = null, Dictionary<string, string>? Env = null);

public sealed class McpServerRegistry(string file)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static McpServerRegistry ForDataDirectory() => new(Path.Combine(Settings.DataDirectory, "mcp-servers.json"));

    public Dictionary<string, McpServerEntry> Load()
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, McpServerEntry>>(File.ReadAllText(file), Json) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    public void Upsert(string name, McpServerEntry entry)
    {
        var all = Load(); all[name] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(all, Json));
    }

    public void Remove(string name) { var all = Load(); if (all.Remove(name)) File.WriteAllText(file, JsonSerializer.Serialize(all, Json)); }
}

/// <summary>Tells Claude (Claude Desktop's three possible settings locations, and the assistant's own list) where an MCP server is.</summary>
public static class ClaudeRegistration
{
    public static IEnumerable<string> DesktopConfigs(McpEnv env)
    {
        yield return Path.Combine(env.AppData, "Claude", "claude_desktop_config.json");
        var pk = Path.Combine(env.LocalAppData, "Packages");
        if (Directory.Exists(pk)) foreach (var d in Directory.GetDirectories(pk, "Claude_*")) yield return Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
        var cd = Path.Combine(env.LocalAppData, "Claude-Data");
        if (Directory.Exists(cd)) foreach (var d in Directory.GetDirectories(cd)) yield return Path.Combine(d, "claude_desktop_config.json");
    }

    /// <summary>Adds or replaces one server in every settings file (the Roaming one is created when missing, the others only when that Claude exists) and in the assistant's own list. Existing settings are kept and each changed file is backed up first.</summary>
    public static int Register(McpEnv env, string name, string command, string[] args, IDictionary<string, string>? serverEnv, McpContext ctx)
    {
        var written = 0;
        foreach (var f in DesktopConfigs(env))
        {
            var dir = Path.GetDirectoryName(f)!;
            var roaming = f.StartsWith(Path.Combine(env.AppData, "Claude") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) { if (roaming) Directory.CreateDirectory(dir); else continue; }
            JsonNode root = new JsonObject();
            if (File.Exists(f))
            {
                try { var t = File.ReadAllText(f); if (t.Trim().Length > 0) root = JsonNode.Parse(t) ?? new JsonObject(); }
                catch (JsonException) { ctx.Warn("Not valid JSON, left unchanged: " + f); continue; }
                File.Copy(f, f + ".bak-" + ctx.Stamp, true);
            }
            var obj = root as JsonObject ?? new JsonObject();
            var servers = obj["mcpServers"] as JsonObject ?? new JsonObject();
            var entry = new JsonObject { ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) };
            var keep = (servers[name] as JsonObject)?["env"] as JsonObject;               // keep extra settings someone added
            if (serverEnv is { Count: > 0 } || keep is not null)
            {
                var e = keep is null ? new JsonObject() : JsonNode.Parse(keep.ToJsonString())!.AsObject();
                if (serverEnv is not null) foreach (var (k, v) in serverEnv) e[k] = v;
                entry["env"] = e;
            }
            servers[name] = entry; obj["mcpServers"] = servers;
            File.WriteAllText(f, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            ctx.Ok("Claude settings updated: " + f);
            written++;
        }
        // Claude Desktop shows local servers under Settings > Developer
        var dev = Path.Combine(env.AppData, "Claude", "developer_settings.json");
        try
        {
            JsonObject d = new();
            if (File.Exists(dev)) { try { d = JsonNode.Parse(File.ReadAllText(dev)) as JsonObject ?? new(); } catch (JsonException) { } File.Copy(dev, dev + ".bak-" + ctx.Stamp, true); }
            d["allowDevTools"] = true;
            Directory.CreateDirectory(Path.GetDirectoryName(dev)!);
            File.WriteAllText(dev, d.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ctx.Warn("Could not enable Developer Mode: " + e.Message); }
        new McpServerRegistry(env.RegistryFile).Upsert(name, new McpServerEntry(command, args, serverEnv is null ? null : new Dictionary<string, string>(serverEnv)));
        ctx.Ok("Added to the assistant's own list of servers (the chat can use it): " + name);
        return written;
    }
}

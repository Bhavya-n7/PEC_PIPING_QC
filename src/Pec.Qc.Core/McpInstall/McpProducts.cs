using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.McpInstall;

/// <summary>One MCP server (with its plug-in) that the team's Autodesk programs can use, installed by this program itself: no scripts, no console window, no questions.</summary>
public abstract class McpProduct
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string Source { get; }
    /// <summary>The Autodesk programs on this PC that this server is for (empty = none found).</summary>
    public abstract IReadOnlyList<HostApp> Hosts(McpEnv env);
    public abstract bool IsInstalled(McpEnv env);
    public abstract Task InstallAsync(McpContext c);
    protected abstract string HostLabel { get; }
    public string HostLabelText => HostLabel;

    protected static void EnsureClosed(McpContext c, string what, params string[] processes)
    {
        foreach (var p in processes)
            if (c.Env.IsRunning(p)) throw new McpInstallException($"{what} is open. Save your work, close it, and press Install again.", needsClose: true);
    }

    protected static async Task<int> RunAsync(McpContext c, string exe, IEnumerable<string> args, string? workDir = null, IDictionary<string, string>? env = null, int timeoutMinutes = 20)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workDir is not null) psi.WorkingDirectory = workDir;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new McpInstallException("Could not start " + Path.GetFileName(exe));
        p.StandardInput.Close();
        void Pump(StreamReader r) { string? l; while ((l = r.ReadLine()) is not null) if (l.Length > 0) c.Info(l.Length > 300 ? l[..300] : l); }
        var t1 = Task.Run(() => Pump(p.StandardOutput)); var t2 = Task.Run(() => Pump(p.StandardError));
        using var reg = c.Ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        if (!p.WaitForExit(timeoutMinutes * 60_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new McpInstallException($"{Path.GetFileName(exe)} did not finish in {timeoutMinutes} minutes and was stopped."); }
        await Task.WhenAll(t1, t2);
        c.Ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    internal static async Task ProbeOrThrowAsync(McpContext c, string what, string exe, string[] args, Dictionary<string, string>? env, int seconds = 90)
    {
        c.Step("Testing " + what);
        McpProbeResult r = new(false, "", 0, "");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            r = await c.Env.Probe.RunAsync(exe, args, env, TimeSpan.FromSeconds(seconds), c.Ct);
            if (r.Ok) break;
            c.Warn($"attempt {attempt}: {r.Error}");
        }
        if (!r.Ok) throw new McpInstallException($"{what} was installed but did not answer a test message ({r.Error}). Windows (Smart App Control / antivirus) may be blocking it: ask IT, and send the log.");
        c.Ok($"{what} answered: {r.Server}, {r.Tools} tools");
    }
}

/// <summary>dwg-mcp and nwd-mcp (Bimwright): a plug-in bundle for the Autodesk program plus a server, from the publisher's release on GitHub.</summary>
public sealed record BimwrightSpec(string Repo, string ZipPrefix, string BundleName, string ServerFolder, string Exe, string ClaudeName, string Fallback, string Label, string[] Processes, bool PatchPlatform);

public static class Bimwright
{
    public static string ServerExe(McpEnv env, BimwrightSpec s) => Path.Combine(env.LocalAppData, "Bimwright", s.ServerFolder, "server", "current", s.Exe);
    public static string BundleDir(McpEnv env, BimwrightSpec s) => Path.Combine(env.Plugins, s.BundleName);
    public static bool IsInstalled(McpEnv env, BimwrightSpec s) => File.Exists(ServerExe(env, s)) && File.Exists(Path.Combine(BundleDir(env, s), "PackageContents.xml"));

    public static async Task<string> InstallAsync(McpContext c, BimwrightSpec s, IReadOnlyList<HostApp> hosts)
    {
        if (hosts.Count == 0) throw new McpInstallException($"{s.Label} was not found on this PC.");
        EnsureClosed(c, s);
        c.Step($"Finding the latest {s.ClaudeName}");
        var tag = await McpFiles.LatestTagAsync(c.Env.Http, s.Repo, s.Fallback, c.Ct);
        c.Ok("release " + tag);
        var work = Path.Combine(c.Env.Temp, $"PEC-QC-{s.ClaudeName}-{c.Stamp}"); Directory.CreateDirectory(work);
        try
        {
            var zipName = $"{s.ZipPrefix}.Setup-{tag}-win-x64.zip";
            var zip = Path.Combine(work, zipName);
            c.Step($"Downloading {s.ClaudeName} {tag}");
            await c.DownloadAsync($"https://github.com/{s.Repo}/releases/download/{tag}/{zipName}", zip, zipName);
            var sha = McpFiles.ExpectedHash(await c.Env.Http.GetStringAsync($"https://github.com/{s.Repo}/releases/download/{tag}/{zipName}.sha256", c.Ct), zipName);
            McpFiles.VerifyHash(zip, sha, zipName);
            c.Ok("checksum verified");
            c.Step("Installing the plug-in and the server");
            var setup = Path.Combine(work, "setup");
            McpFiles.Extract(zip, setup);
            var bundleSrc = Path.Combine(setup, "bundle"); var serverSrc = Path.Combine(setup, "server");
            if (!File.Exists(Path.Combine(bundleSrc, "PackageContents.xml")) || !File.Exists(Path.Combine(serverSrc, s.Exe)))
                throw new McpInstallException($"The package {zipName} does not have the expected content (bundle and {s.Exe}).");
            var target = BundleDir(c.Env, s);
            try { if (Directory.Exists(target)) Directory.Delete(target, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new McpInstallException($"The old plug-in is in use ({e.Message}). Close {s.Label} and Claude, then press Install again.", needsClose: true); }
            McpFiles.CopyDir(bundleSrc, target);
            c.Ok("plug-in: " + target);
            var serverRoot = Path.GetDirectoryName(ServerExe(c.Env, s))!;
            McpFiles.ReplaceDir(serverSrc, serverRoot, c.Stamp);
            c.Ok("server: " + ServerExe(c.Env, s));
            if (s.PatchPlatform && hosts.Any(h => Regex.IsMatch(h.Product, "Civil|Map|Plant|Architecture|MEP|Electrical|Mechanical|Raster|Advance Steel|P&ID|Utility", RegexOptions.IgnoreCase)))
            {
                // Civil 3D, Plant 3D and the other AutoCAD-based programs only load bundles marked "AutoCAD*"
                var pc = Path.Combine(target, "PackageContents.xml"); var xml = File.ReadAllText(pc);
                if (xml.Contains("Platform=\"AutoCAD\"")) { File.WriteAllText(pc, xml.Replace("Platform=\"AutoCAD\"", "Platform=\"AutoCAD*\""), new UTF8Encoding(true)); c.Ok("plug-in enabled for " + string.Join(", ", hosts.Select(h => h.Product).Distinct())); }
            }
            await McpProduct.ProbeOrThrowAsync(c, s.ClaudeName, ServerExe(c.Env, s), [], null);
            c.Step("Connecting Claude");
            ClaudeRegistration.Register(c.Env, s.ClaudeName, ServerExe(c.Env, s), [], null, c);
            return tag;
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static void EnsureClosed(McpContext c, BimwrightSpec s)
    {
        foreach (var p in s.Processes)
            if (c.Env.IsRunning(p)) throw new McpInstallException($"{s.Label} is open. Save your work, close it, and press Install again.", needsClose: true);
    }
}

public sealed class AutoCadMcp : McpProduct
{
    public static readonly BimwrightSpec Spec = new("bimwright/dwg-mcp", "DwgMcp", "Bimwright.Dwg.bundle", "dwg-mcp", "dwg-mcp.exe", "dwg-mcp", "v2.1.1", "AutoCAD", ["acad"], PatchPlatform: true);
    public override string Id => "autocad";
    public override string Title => "AutoCAD";
    public override string Description => "Lets Claude read the open drawing in AutoCAD 2022-2027 (dwg-mcp).";
    public override string Source => "dwg-mcp by Bimwright (Apache-2.0)";
    protected override string HostLabel => "AutoCAD 2022-2027";
    public override IReadOnlyList<HostApp> Hosts(McpEnv env) => env.Hosts().Where(h => h.Family == "autocad").ToList();
    public override bool IsInstalled(McpEnv env) => Bimwright.IsInstalled(env, Spec);
    public override Task InstallAsync(McpContext c) => Bimwright.InstallAsync(c, Spec, Hosts(c.Env));
}

public sealed class Plant3dMcp : McpProduct
{
    public override string Id => "plant3d";
    public override string Title => "AutoCAD Plant 3D";
    public override string Description => "Lets Claude read the open Plant 3D drawing (the same dwg-mcp server, enabled for Plant 3D).";
    public override string Source => "dwg-mcp by Bimwright (Apache-2.0)";
    protected override string HostLabel => "Plant 3D 2022-2027";
    public override IReadOnlyList<HostApp> Hosts(McpEnv env) => env.Hosts().Where(h => h.Family == "autocad" && h.Product.Contains("Plant", StringComparison.OrdinalIgnoreCase)).ToList();
    public override bool IsInstalled(McpEnv env) => Bimwright.IsInstalled(env, AutoCadMcp.Spec) && Hosts(env).Count > 0;
    public override Task InstallAsync(McpContext c) => Bimwright.InstallAsync(c, AutoCadMcp.Spec, Hosts(c.Env));
}

public sealed class NavisworksMcp : McpProduct
{
    public static readonly BimwrightSpec Spec = new("bimwright/nwd-mcp", "NwdMcp", "Bimwright.Nwd.bundle", "nwd-mcp", "nwd-mcp.exe", "nwd-mcp", "v2.5.1", "Navisworks", ["Roamer"], PatchPlatform: false);
    public const string CoordRepo = "HorizunGroup/naviscoord-mcp";
    public override string Id => "navisworks";
    public override string Title => "Navisworks Manage";
    public override string Description => "Model browsing, properties, search sets and viewpoints (nwd-mcp), and clash coordination (NavisCoord).";
    public override string Source => "nwd-mcp by Bimwright (Apache-2.0), NavisCoord by HorizunGroup (MIT)";
    protected override string HostLabel => "Navisworks Manage 2022-2027";
    public override IReadOnlyList<HostApp> Hosts(McpEnv env) => env.Hosts().Where(h => h.Family == "navisworks").ToList();
    private static string CoordRoot(McpEnv env) => Path.Combine(env.LocalAppData, "NavisCoord", "portable");
    public override bool IsInstalled(McpEnv env) => Bimwright.IsInstalled(env, Spec);

    public override async Task InstallAsync(McpContext c)
    {
        var hosts = Hosts(c.Env);
        await Bimwright.InstallAsync(c, Spec, hosts);
        await InstallCoordAsync(c, hosts);
    }

    private static async Task InstallCoordAsync(McpContext c, IReadOnlyList<HostApp> hosts)
    {
        c.Step("Finding the latest NavisCoord");
        var tag = await McpFiles.LatestTagAsync(c.Env.Http, CoordRepo, "v1.1.2", c.Ct); var ver = tag.TrimStart('v');
        var baseUrl = $"https://github.com/{CoordRepo}/releases/download/{tag}";
        var sums = await c.Env.Http.GetStringAsync($"{baseUrl}/SHA256SUMS.txt", c.Ct);
        var work = Path.Combine(c.Env.Temp, $"PEC-QC-NavisCoord-{c.Stamp}"); Directory.CreateDirectory(work);
        try
        {
            foreach (var y in hosts.Select(h => h.Year).Distinct().Where(y => y is >= 2024 and <= 2026))
            {
                var an = $"NavisCoord-{ver}-addin-NW{y}.zip"; var az = Path.Combine(work, an);
                c.Step($"NavisCoord add-in for Navisworks {y}");
                await c.DownloadAsync($"{baseUrl}/{an}", az, an);
                McpFiles.VerifyHash(az, McpFiles.ExpectedHash(sums, an), an);
                var stage = Path.Combine(work, "nc-" + y); McpFiles.Extract(az, stage);
                var dest = Path.Combine(c.Env.AppData, "Autodesk", $"Navisworks Manage {y}", "Plugins", "NavisCoord");
                McpFiles.CopyDir(stage, dest);                                            // merged: the user's own profiles stay
                c.Ok($"add-in for Navisworks {y}: {dest}");
            }
            var rn = $"naviscoord-runtime-{ver}-win-x64.zip"; var rz = Path.Combine(work, rn);
            c.Step("NavisCoord server (with its own Python)");
            await c.DownloadAsync($"{baseUrl}/{rn}", rz, rn);
            McpFiles.VerifyHash(rz, McpFiles.ExpectedHash(sums, rn), rn);
            var root = Path.Combine(CoordRoot(c.Env), ver);
            if (Directory.Exists(root)) { try { Directory.Move(root, root + ".backup-" + c.Stamp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new McpInstallException("The old NavisCoord server is in use. Close Claude and try again.", needsClose: true); } }
            McpFiles.Extract(rz, root);
            var exe = Directory.EnumerateFiles(root, "naviscoord-mcp.exe", SearchOption.AllDirectories).FirstOrDefault() ?? throw new McpInstallException($"naviscoord-mcp.exe was not found in {rn}.");
            c.Ok("server: " + exe);
            await ProbeOrThrowAsync(c, "NavisCoord", exe, [], null);
            c.Step("Connecting Claude");
            ClaudeRegistration.Register(c.Env, "NavisCoord", exe, [], new Dictionary<string, string> { ["NAVISCOORD_READ_ONLY"] = "0" }, c);
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}

public sealed class RevitMcp : McpProduct
{
    public const string Repo = "sharafutdinovdi/revit-model-mcp";
    public const string Fallback = "2.1.0";
    public override string Id => "revit";
    public override string Title => "Revit";
    public override string Description => "Lets Claude read the open Revit model (revit-model-mcp, Revit 2022-2027).";
    public override string Source => "revit-model-mcp by sharafutdinovdi";
    protected override string HostLabel => "Revit 2022-2027";
    public override IReadOnlyList<HostApp> Hosts(McpEnv env) => env.Hosts().Where(h => h.Family == "revit").ToList();
    private static string Bundle(McpEnv env) => Path.Combine(env.Plugins, "RevitModelMcp.bundle");
    private static string LocalBin(McpEnv env) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
    public override bool IsInstalled(McpEnv env) => File.Exists(Path.Combine(Bundle(env), "PackageContents.xml"));

    public override async Task InstallAsync(McpContext c)
    {
        var hosts = Hosts(c.Env);
        if (hosts.Count == 0) throw new McpInstallException("Revit was not found on this PC.");
        EnsureClosed(c, "Revit", "Revit");
        var work = Path.Combine(c.Env.Temp, $"PEC-QC-Revit-{c.Stamp}"); Directory.CreateDirectory(work);
        var uvEnv = new Dictionary<string, string> { ["UV_SYSTEM_CERTS"] = "true", ["UV_NATIVE_TLS"] = "true", ["UV_PYTHON"] = "3.12" };
        try
        {
            c.Step("Python package runner (uv)");
            var uv = FindUv(c.Env);
            if (uv is null)
            {
                const string url = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";
                var zip = Path.Combine(work, "uv.zip");
                await c.DownloadAsync(url, zip, "uv");
                McpFiles.VerifyHash(zip, McpFiles.ExpectedHash(await c.Env.Http.GetStringAsync(url + ".sha256", c.Ct)), "uv");
                var tmp = Path.Combine(work, "uv"); McpFiles.Extract(zip, tmp);
                Directory.CreateDirectory(LocalBin(c.Env));
                foreach (var exe in new[] { "uv.exe", "uvx.exe", "uvw.exe" })
                    if (Directory.EnumerateFiles(tmp, exe, SearchOption.AllDirectories).FirstOrDefault() is { } f) File.Copy(f, Path.Combine(LocalBin(c.Env), exe), true);
                uv = Path.Combine(LocalBin(c.Env), "uv.exe");
                if (!File.Exists(uv)) throw new McpInstallException("uv.exe was not found in the downloaded package.");
                AddToUserPath(LocalBin(c.Env), c);
            }
            c.Ok("uv: " + uv);

            c.Step("Finding the latest Revit add-in");
            var ver = Fallback;
            try { var j = JsonNode.Parse(await c.Env.Http.GetStringAsync("https://pypi.org/pypi/revit-model-mcp/json", c.Ct)); var v = j?["info"]?["version"]?.ToString(); if (v is not null && Regex.IsMatch(v, @"^\d+\.\d+\.\d+$")) ver = v; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { c.Warn("Could not read the latest version, using " + ver); }
            var tag = "v" + ver; var bundleZip = $"revit-model-mcp-{ver}-managed.bundle.zip";
            string sums;
            try { sums = await c.Env.Http.GetStringAsync($"https://github.com/{Repo}/releases/download/{tag}/SHA256SUMS.txt", c.Ct); }
            catch (HttpRequestException) when (ver != Fallback) { ver = Fallback; tag = "v" + ver; bundleZip = $"revit-model-mcp-{ver}-managed.bundle.zip"; c.Warn("The newest release has no checksums yet: using " + ver); sums = await c.Env.Http.GetStringAsync($"https://github.com/{Repo}/releases/download/{tag}/SHA256SUMS.txt", c.Ct); }
            c.Step("Downloading the Revit add-in " + ver);
            var bz = Path.Combine(work, bundleZip);
            await c.DownloadAsync($"https://github.com/{Repo}/releases/download/{tag}/{bundleZip}", bz, bundleZip);
            McpFiles.VerifyHash(bz, McpFiles.ExpectedHash(sums, bundleZip), bundleZip);
            c.Ok("checksum verified");

            c.Step("Installing the Revit add-in");
            for (var y = 2022; y <= 2027; y++)                                           // old MSI / manual copies load first and show the wrong version
                foreach (var n in new[] { "RevitModelMcp.addin", "RevitModelMcp" })
                {
                    var old = Path.Combine(c.Env.AppData, "Autodesk", "Revit", "Addins", y.ToString(), n);
                    try { if (File.Exists(old)) File.Move(old, old + ".bak-" + c.Stamp); else if (Directory.Exists(old)) Directory.Move(old, old + ".bak-" + c.Stamp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Warn("Could not move aside " + old); }
                }
            var tmpB = Path.Combine(work, "bundle"); McpFiles.Extract(bz, tmpB);
            var src = Path.Combine(tmpB, "RevitModelMcp.bundle");
            if (!File.Exists(Path.Combine(src, "PackageContents.xml"))) throw new McpInstallException("The add-in package has no RevitModelMcp.bundle\\PackageContents.xml.");
            McpFiles.ReplaceDir(src, Bundle(c.Env), c.Stamp);
            foreach (var h in hosts) c.Ok(File.Exists(Path.Combine(Bundle(c.Env), "Contents", h.Year.ToString(), "RevitModelMcp.addin")) ? $"add-in ready for Revit {h.Year}" : $"this package has no add-in for Revit {h.Year}");

            c.Step("Installing the Revit server (the first time takes a few minutes)");
            var spec = $"revit-model-mcp=={ver}"; string? toolExe = null;
            for (var attempt = 1; attempt <= 3 && toolExe is null; attempt++)
            {
                c.Info($"uv tool install {spec} (attempt {attempt} of 3)");
                var code = await RunAsync(c, uv, ["tool", "install", "--force", "--python", "3.12", spec], env: uvEnv);
                if (code == 0)
                {
                    var bin = LocalBin(c.Env);
                    var cand = Path.Combine(bin, "revit-model-mcp.exe");
                    if (File.Exists(cand)) { toolExe = cand; break; }
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), c.Ct);
            }
            if (toolExe is null) throw new McpInstallException("The Revit server could not be installed (see the log). If the office firewall is blocking Python packages, ask IT to install its certificate.");
            c.Ok("server: " + toolExe);
            var srvEnv = new Dictionary<string, string> { ["REVIT_MCP_HOST"] = "local", ["REVIT_MCP_NO_UPDATE_CHECK"] = "1", ["UV_SYSTEM_CERTS"] = "true", ["UV_NATIVE_TLS"] = "true", ["UV_PYTHON"] = "3.12" };
            await ProbeOrThrowAsync(c, "the Revit server", toolExe, [], srvEnv, seconds: 420);
            c.Step("Connecting Claude");
            ClaudeRegistration.Register(c.Env, "revit-model-mcp", toolExe, [], srvEnv, c);
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static string? FindUv(McpEnv env)
    {
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var f in new[] { Path.Combine(LocalBin(env), "uv.exe"), Path.Combine(up, ".cargo", "bin", "uv.exe") }) if (File.Exists(f)) return f;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { var f = Path.Combine(dir.Trim('"'), "uv.exe"); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        return null;
    }

    private static void AddToUserPath(string dir, McpContext c)
    {
        try
        {
            var p = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            if (!p.Split(';').Contains(dir, StringComparer.OrdinalIgnoreCase)) { Environment.SetEnvironmentVariable("Path", (p.TrimEnd(';') + ";" + dir).TrimStart(';'), EnvironmentVariableTarget.User); c.Info("added to your PATH: " + dir); }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { c.Warn("Could not add uv to PATH: " + e.Message); }
    }
}

public sealed class Civil3dMcp : McpProduct
{
    public const string Owner = "barbosaihan", Repo = "civil3d-mcp", Commit = "344ad15ff517a19232fdf4eda2685292bdf723cf";   // the version the team tested
    public const int Port = 8080;
    private static readonly string[] References = ["accoremgd.dll", "AcDbMgd.dll", "acmgd.dll", "AecBaseMgd.dll", "AeccDbMgd.dll"];
    public override string Id => "civil3d";
    public override string Title => "Civil 3D";
    public override string Description => "Lets Claude work with the open Civil 3D drawing (builds the server and the plug-in on this PC, about 5-10 minutes the first time).";
    public override string Source => "civil3d-mcp by barbosaihan (MIT)";
    protected override string HostLabel => "Civil 3D 2025-2026";
    public override IReadOnlyList<HostApp> Hosts(McpEnv env) => env.Hosts().Where(h => h.Family == "autocad" && h.Product.Contains("Civil", StringComparison.OrdinalIgnoreCase) && h.Year >= 2025).ToList();
    private static string Root(McpEnv env) => Path.Combine(env.LocalAppData, "Civil3DMCP");
    private static string Bundle(McpEnv env) => Path.Combine(env.Plugins, "Civil3dMcp.bundle");
    public override bool IsInstalled(McpEnv env) => File.Exists(Path.Combine(Bundle(env), "PackageContents.xml")) && File.Exists(Path.Combine(Root(env), "civil3d-mcp", "build", "index.js"));

    public override async Task InstallAsync(McpContext c)
    {
        var hosts = Hosts(c.Env);
        if (hosts.Count == 0) throw new McpInstallException("Civil 3D 2025 or 2026 was not found on this PC.");
        EnsureClosed(c, "Civil 3D", "acad");
        var host = hosts.OrderByDescending(h => h.Year).First();
        var root = Root(c.Env); var src = Path.Combine(root, "civil3d-mcp"); var nodeDir = Path.Combine(root, "node"); var dotnetDir = Path.Combine(root, "dotnet");
        Directory.CreateDirectory(root);
        var work = Path.Combine(c.Env.Temp, $"PEC-QC-Civil3D-{c.Stamp}"); Directory.CreateDirectory(work);
        try
        {
            c.Step("Downloading the Civil 3D MCP source (tested version)");
            var zip = Path.Combine(work, "civil3d-mcp.zip");
            await c.DownloadAsync($"https://github.com/{Owner}/{Repo}/archive/{Commit}.zip", zip, "source");
            var ex = Path.Combine(work, "src"); McpFiles.Extract(zip, ex);
            var inner = Directory.GetDirectories(ex).First();
            if (Directory.Exists(src)) Directory.Move(src, src + ".backup-" + c.Stamp);
            Directory.Move(inner, src);

            c.Step("Node.js and the MCP server");
            var nodeExe = Path.Combine(nodeDir, "node.exe");
            if (!File.Exists(nodeExe))
            {
                var idx = JsonNode.Parse(await c.Env.Http.GetStringAsync("https://nodejs.org/dist/index.json", c.Ct))!.AsArray();
                var nv = idx.Select(n => n?["version"]?.ToString()).FirstOrDefault(v => v is not null && v.StartsWith("v22.")) ?? throw new McpInstallException("No Node.js 22 release was found.");
                var nz = Path.Combine(work, $"node-{nv}-win-x64.zip");
                await c.DownloadAsync($"https://nodejs.org/dist/{nv}/node-{nv}-win-x64.zip", nz, "Node.js " + nv);
                McpFiles.Extract(nz, work);
                if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, true);
                Directory.Move(Path.Combine(work, $"node-{nv}-win-x64"), nodeDir);
            }
            var npmCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
            var nodeEnv = new Dictionary<string, string> { ["PATH"] = nodeDir + ";" + Environment.GetEnvironmentVariable("PATH"), ["NPM_CONFIG_UPDATE_NOTIFIER"] = "false", ["NPM_CONFIG_FUND"] = "false" };
            c.Info("installing the server's packages and building (about a minute)");
            var e1 = await RunAsync(c, nodeExe, [npmCli, "ci", "--no-audit", "--no-fund"], src, nodeEnv);
            var e2 = e1 == 0 ? await RunAsync(c, nodeExe, [npmCli, "run", "build"], src, nodeEnv) : 1;
            var serverJs = Path.Combine(src, "build", "index.js");
            if (e1 != 0 || e2 != 0 || !File.Exists(serverJs)) throw new McpInstallException("The MCP server could not be built (see the log).");
            c.Ok("server built: " + serverJs);

            c.Step($"Building the Civil 3D plug-in for Civil 3D {host.Year}");
            var refDir = Path.Combine(src, "C_References"); Directory.CreateDirectory(refDir);
            foreach (var dll in References)
            {
                var f = Directory.EnumerateFiles(host.Dir, dll, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).FirstOrDefault()
                        ?? throw new McpInstallException($"{dll} was not found in {host.Dir}.");
                File.Copy(f, Path.Combine(refDir, dll), true);
            }
            var netMajor = 8;
            try { var asm = System.Reflection.Metadata.MetadataReaderProvider.FromMetadataStream(new MemoryStream(File.ReadAllBytes(Path.Combine(refDir, "AcDbMgd.dll")))); var rd = asm.GetMetadataReader(); foreach (var h in rd.AssemblyReferences) { var r = rd.GetAssemblyReference(h); if (rd.GetString(r.Name) == "System.Runtime" && r.Version.Major >= 8) netMajor = r.Version.Major; } }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or IOException) { c.Warn("Could not read the .NET version from AcDbMgd.dll, will detect it while building."); }
            c.Ok($"Civil 3D {host.Year} runs on .NET {netMajor}");

            var proj = Path.Combine(src, "plugin", "Civil3dMcpPlugin", "Civil3dMcpPlugin.csproj");
            var outDir = Path.Combine(work, "plugin-out"); var pluginDll = Path.Combine(outDir, "Civil3dMcpPlugin.dll");
            var built = false;
            for (var attempt = 1; attempt <= 2 && !built; attempt++)
            {
                var dotnet = await EnsureDotnetAsync(c, netMajor, dotnetDir, work);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                foreach (var d in new[] { "bin", "obj" }) { var p = Path.Combine(Path.GetDirectoryName(proj)!, d); if (Directory.Exists(p)) Directory.Delete(p, true); }
                File.WriteAllText(proj, Regex.Replace(File.ReadAllText(proj), "<TargetFramework>[^<]+</TargetFramework>", $"<TargetFramework>net{netMajor}.0-windows</TargetFramework>"), new UTF8Encoding(false));
                var denv = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" };
                var r1 = await RunAsync(c, dotnet, ["restore", proj, $"-p:Civil3DReferencesPath={refDir}"], env: denv);
                if (r1 == 0 && await RunAsync(c, dotnet, ["build", proj, "-c", "Release", "--no-restore", "-o", outDir, $"-p:Civil3DReferencesPath={refDir}"], env: denv) == 0 && File.Exists(pluginDll)) { built = true; break; }
                if (attempt == 1) { netMajor = Math.Max(netMajor + 1, 8); c.Warn($"Retrying the build for .NET {netMajor}"); }
            }
            if (!built) throw new McpInstallException("The Civil 3D plug-in could not be built (see the log).");
            foreach (var dll in References) try { File.Delete(Path.Combine(outDir, dll)); } catch (IOException) { }   // never ship Autodesk's files
            c.Ok("plug-in built");

            c.Step("Installing the plug-in into Civil 3D");
            var bundle = Bundle(c.Env); var contents = Path.Combine(bundle, "Contents");
            if (Directory.Exists(bundle)) { try { Directory.Delete(bundle, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new McpInstallException("The old plug-in is in use. Close Civil 3D and try again.", needsClose: true); } }
            McpFiles.CopyDir(outDir, contents);
            File.WriteAllText(Path.Combine(bundle, "PackageContents.xml"), $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <ApplicationPackage SchemaVersion="1.0" AppVersion="1.0.4" ProductCode="{6F3C1E2A-9B7D-4C1E-8A5F-2D4B6C8E0A13}" UpgradeCode="{0B8D5E71-3C2F-4A9E-B6D1-7E4F2A9C5B30}" Name="Civil 3D MCP" Description="MCP bridge between AI apps and Civil 3D (github.com/barbosaihan/civil3d-mcp)" Author="Petrocon" ProductType="Application" AutodeskProduct="Civil3D">
                  <CompanyDetails Name="Petrocon" />
                  <Components Description="Civil 3D 2025 and 2026">
                    <RuntimeRequirements OS="Win64" Platform="Civil3D" SeriesMin="R25.0" SeriesMax="R25.1" />
                    <ComponentEntry AppName="Civil3dMcpPlugin" Version="1.0.4" ModuleName="./Contents/Civil3dMcpPlugin.dll" AppDescription="Starts the MCP listener on port {Port}" LoadOnAutoCADStartup="True" />
                  </Components>
                </ApplicationPackage>
                """, new UTF8Encoding(false));
            c.Ok("plug-in: " + bundle);

            await ProbeOrThrowAsync(c, "the Civil 3D server", nodeExe, [serverJs], new Dictionary<string, string> { ["CIVIL3D_HOST"] = "localhost", ["CIVIL3D_PORT"] = Port.ToString() }, 60);
            c.Step("Connecting Claude");
            ClaudeRegistration.Register(c.Env, "civil3d", nodeExe, [serverJs], new Dictionary<string, string> { ["CIVIL3D_HOST"] = "localhost", ["CIVIL3D_PORT"] = Port.ToString() }, c);
        }
        finally { try { Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static async Task<string> EnsureDotnetAsync(McpContext c, int major, string dotnetDir, string work)
    {
        async Task<bool> Has(string exe)
        {
            if (!File.Exists(exe) && !Path.IsPathRooted(exe) is false) return false;
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo(exe, "--list-sdks") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            try { using var p = Process.Start(psi)!; var o = await p.StandardOutput.ReadToEndAsync(); p.WaitForExit(20000); return Regex.IsMatch(o, $@"(?m)^{major}\."); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
        var sys = Environment.GetEnvironmentVariable("PATH")!.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => { try { return Path.Combine(d.Trim('"'), "dotnet.exe"); } catch (ArgumentException) { return ""; } }).FirstOrDefault(File.Exists);
        if (sys is not null && await Has(sys)) return sys;
        var priv = Path.Combine(dotnetDir, "dotnet.exe");
        if (File.Exists(priv) && await Has(priv)) return priv;
        c.Info($"installing the .NET {major} SDK into {dotnetDir} (no administrator needed, about 250 MB)");
        var script = Path.Combine(work, "dotnet-install.ps1");
        await c.DownloadAsync("https://dot.net/v1/dotnet-install.ps1", script, "Microsoft's .NET installer");
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        await RunAsync(c, ps, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Channel", $"{major}.0", "-InstallDir", dotnetDir, "-NoPath"]);
        if (File.Exists(priv) && await Has(priv)) return priv;
        throw new McpInstallException($"The .NET {major} SDK could not be installed.");
    }
}

public sealed record McpProductStatus(McpProduct Product, bool HostFound, string HostText, bool Installed);
public sealed record McpInstallResult(string Id, bool Ok, bool NeedsClose, string Message, string LogFile);

public static class McpCatalog
{
    public static IReadOnlyList<McpProduct> All { get; } = [new AutoCadMcp(), new Plant3dMcp(), new Civil3dMcp(), new NavisworksMcp(), new RevitMcp()];
    public static McpProduct? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<McpProductStatus> Status(McpEnv env)
    {
        var hosts = env.Hosts();
        return All.Select(p =>
        {
            var h = p.Hosts(env);
            return new McpProductStatus(p, h.Count > 0, h.Count > 0 ? string.Join(", ", h.Select(x => x.Product).Distinct()) : p.HostLabelText + " not found on this PC", p.IsInstalled(env));
        }).ToList();
    }

    /// <summary>Installs one product. Never throws for an expected problem: the result says what happened and what to do.</summary>
    public static async Task<McpInstallResult> InstallAsync(McpProduct p, McpEnv env, Action<string> log, Action<string> step, Action<int> percent, CancellationToken ct = default)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var logFile = Path.Combine(env.Temp, $"PEC-QC-MCP-{p.Id}-{stamp}.log");
        void Both(string m) { try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] {m}{Environment.NewLine}"); } catch (IOException) { } log(m); }
        var ctx = new McpContext(env, stamp, Both, s => { Both("==> " + s); step(s); }, percent, ct);
        try
        {
            Both($"Installing {p.Title} on {Environment.MachineName} as {Environment.UserName}");
            await p.InstallAsync(ctx);
            Both("Finished");
            return new McpInstallResult(p.Id, true, false, $"{p.Title} is installed. Restart Claude (and open the Autodesk program) to use it.", logFile);
        }
        catch (McpInstallException e) { Both("FAILED: " + e.Message); return new McpInstallResult(p.Id, false, e.NeedsClose, e.Message, logFile); }
        catch (OperationCanceledException) { Both("Cancelled"); return new McpInstallResult(p.Id, false, false, "Cancelled.", logFile); }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Both("FAILED: " + e);
            var net = e is HttpRequestException ? " Check the internet connection (github.com, nodejs.org, pypi.org must be reachable)." : "";
            return new McpInstallResult(p.Id, false, false, $"{p.Title} could not be installed: {e.Message}.{net} The log has the details.", logFile);
        }
    }
}

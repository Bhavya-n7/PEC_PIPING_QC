using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pec.Qc.Core.McpInstall;

namespace Pec.Qc.Tests;

/// <summary>The program's own installer for the AutoCAD, Plant 3D, Navisworks, Revit and Civil 3D MCP servers, run against a fake network and temporary folders.</summary>
public class McpInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pecqc-mcpi-" + Guid.NewGuid().ToString("N"));
    public McpInstallTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeHttp : IMcpHttp
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Requested = [];
        public string LatestTag = "v9.9.9";
        public Task DownloadAsync(string url, string file, Action<long, long?>? progress, CancellationToken ct)
        {
            Requested.Add(url);
            if (!Files.TryGetValue(url, out var b)) throw new HttpRequestException("404 " + url);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, b); progress?.Invoke(b.Length, b.Length); return Task.CompletedTask;
        }
        public Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            Requested.Add(url);
            return Files.TryGetValue(url, out var b) ? Task.FromResult(Encoding.UTF8.GetString(b)) : throw new HttpRequestException("404 " + url);
        }
        public Task<string> FinalUrlAsync(string url, CancellationToken ct) => Task.FromResult(url.Replace("/releases/latest", "/releases/tag/" + LatestTag));
    }

    private sealed class FakeProbe : IMcpProbe
    {
        public bool Ok = true; public readonly List<string> Started = [];
        public Task<McpProbeResult> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct)
        { Started.Add(exe); return Task.FromResult(Ok ? new McpProbeResult(true, "fake 1.0", 12, "") : new McpProbeResult(false, "", 0, "blocked")); }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) foreach (var (n, c) in entries) { var e = z.CreateEntry(n); using var w = new StreamWriter(e.Open()); w.Write(c); }
        return ms.ToArray();
    }

    private const string Contents = "<ApplicationPackage><Components><RuntimeRequirements OS=\"Win64\" Platform=\"AutoCAD\" SeriesMin=\"R25.1\" SeriesMax=\"R25.1\"/></Components></ApplicationPackage>";

    private void Vendor(FakeHttp h, string repo, string prefix, string exe, string tag = "v9.9.9", bool badHash = false, params (string, string)[] extra)
    {
        var zip = Zip([("bundle/PackageContents.xml", Contents), ("bundle/Contents/2026/plugin.dll", "dll"), ($"server/{exe}", "exe " + tag), .. extra]);
        var name = $"{prefix}.Setup-{tag}-win-x64.zip";
        h.Files[$"https://github.com/{repo}/releases/download/{tag}/{name}"] = zip;
        h.Files[$"https://github.com/{repo}/releases/download/{tag}/{name}.sha256"] = Encoding.UTF8.GetBytes((badHash ? new string('0', 64) : Sha(zip)) + "  " + name);
    }

    private (McpEnv Env, FakeHttp Http, FakeProbe Probe, HashSet<string> Running) Pc(params HostApp[] hosts)
    {
        var http = new FakeHttp(); var probe = new FakeProbe(); var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var env = new McpEnv
        {
            AppData = Path.Combine(_root, "AppData"), LocalAppData = Path.Combine(_root, "Local"), ProgramFiles = Path.Combine(_root, "PF"), Temp = Path.Combine(_root, "Temp"), DataDir = Path.Combine(_root, "Data"),
            Http = http, Probe = probe, IsRunning = running.Contains, HostsOverride = () => hosts,
        };
        Directory.CreateDirectory(env.Temp); Directory.CreateDirectory(env.AppData);
        return (env, http, probe, running);
    }

    private static HostApp Acad(string product = "AutoCAD 2026") => new("autocad", 2026, product, @"C:\PF\Autodesk\AutoCAD 2026");

    private static Task<McpInstallResult> Install(string id, McpEnv env, List<string>? log = null) =>
        McpCatalog.InstallAsync(McpCatalog.Find(id)!, env, m => log?.Add(m), _ => { }, _ => { });

    private static JsonObject Cfg(McpEnv env) => JsonNode.Parse(File.ReadAllText(Path.Combine(env.AppData, "Claude", "claude_desktop_config.json")))!.AsObject();

    [Fact]
    public async Task AutoCAD_plugin_and_server_are_installed_tested_and_registered_and_other_Claude_servers_are_kept()
    {
        var (env, http, probe, _) = Pc(Acad());
        Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe");
        var cfg = Path.Combine(env.AppData, "Claude", "claude_desktop_config.json"); Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, """{"theme":"dark","mcpServers":{"other":{"command":"x.exe","args":["a"]}}}""");

        var r = await Install("autocad", env);
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(env.Plugins, "Bimwright.Dwg.bundle", "PackageContents.xml")));
        var exe = Path.Combine(env.LocalAppData, "Bimwright", "dwg-mcp", "server", "current", "dwg-mcp.exe");
        Assert.True(File.Exists(exe)); Assert.Equal(exe, Assert.Single(probe.Started));
        var j = Cfg(env);
        Assert.Equal("dark", (string?)j["theme"]); Assert.Equal("x.exe", (string?)j["mcpServers"]!["other"]!["command"]);          // what was there is kept
        Assert.Equal(exe, (string?)j["mcpServers"]!["dwg-mcp"]!["command"]);
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(cfg)!, "claude_desktop_config.json.bak-*"));                       // and backed up
        Assert.True((bool?)JsonNode.Parse(File.ReadAllText(Path.Combine(env.AppData, "Claude", "developer_settings.json")))!["allowDevTools"]);
        Assert.Equal(exe, new McpServerRegistry(env.RegistryFile).Load()["dwg-mcp"].Command);                                        // the assistant's own chat can use it
        Assert.True(McpCatalog.Status(env).Single(s => s.Product.Id == "autocad").Installed);
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!).Select(Path.GetFileName), n => n!.Contains(".staging-") || n.Contains(".backup-"));                   // no staging / backup left
        Assert.True(File.Exists(r.LogFile) && File.ReadAllText(r.LogFile).Contains("Finished"));
    }

    [Fact]
    public async Task Installing_again_replaces_the_old_version_cleanly()
    {
        var (env, http, _, _) = Pc(Acad());
        Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe", "v9.9.9");
        Assert.True((await Install("autocad", env)).Ok);
        var exe = Path.Combine(env.LocalAppData, "Bimwright", "dwg-mcp", "server", "current", "dwg-mcp.exe");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "stale.txt"), "old");
        http.LatestTag = "v10.0.0"; Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe", "v10.0.0");
        Assert.True((await Install("autocad", env)).Ok);
        Assert.Equal("exe v10.0.0", File.ReadAllText(exe)); Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "stale.txt")));
    }

    [Fact]
    public async Task Civil_and_Plant_products_get_the_plugin_enabled_but_plain_AutoCAD_is_left_as_published()
    {
        var (plain, hp, _, _) = Pc(Acad()); Vendor(hp, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe");
        Assert.True((await Install("autocad", plain)).Ok);
        Assert.Contains("Platform=\"AutoCAD\"", File.ReadAllText(Path.Combine(plain.Plugins, "Bimwright.Dwg.bundle", "PackageContents.xml")));
        Directory.Delete(_root, true); Directory.CreateDirectory(_root);
        var (plant, h2, _, _) = Pc(Acad("AutoCAD Plant 3D 2026")); Vendor(h2, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe");
        Assert.True((await Install("plant3d", plant)).Ok);
        Assert.Contains("Platform=\"AutoCAD*\"", File.ReadAllText(Path.Combine(plant.Plugins, "Bimwright.Dwg.bundle", "PackageContents.xml")));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_published_checksum_installs_nothing()
    {
        var (env, http, probe, _) = Pc(Acad());
        Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe", badHash: true);
        var r = await Install("autocad", env);
        Assert.False(r.Ok); Assert.Contains("checksum", r.Message);
        Assert.False(Directory.Exists(env.Plugins) && Directory.GetDirectories(env.Plugins).Length > 0); Assert.False(Directory.Exists(Path.Combine(env.LocalAppData, "Bimwright"))); Assert.Empty(probe.Started);
        Assert.False(File.Exists(env.RegistryFile));
    }

    [Fact]
    public async Task When_the_program_is_open_it_says_close_it_and_changes_nothing()
    {
        var (env, http, _, running) = Pc(Acad()); Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe"); running.Add("acad");
        var r = await Install("autocad", env);
        Assert.False(r.Ok); Assert.True(r.NeedsClose); Assert.Contains("close", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(env.LocalAppData, "Bimwright")));
        running.Clear(); Assert.True((await Install("autocad", env)).Ok);                                                            // pressing Install again works
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_reported_and_Claude_is_not_changed()
    {
        var (env, http, probe, _) = Pc(Acad()); Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe"); probe.Ok = false;
        var r = await Install("autocad", env);
        Assert.False(r.Ok); Assert.Contains("did not answer", r.Message); Assert.Contains("Smart App Control", r.Message);
        Assert.False(File.Exists(Path.Combine(env.AppData, "Claude", "claude_desktop_config.json")));
    }

    [Fact]
    public async Task A_program_that_is_not_installed_cannot_be_installed_for_and_the_status_says_so()
    {
        var (env, _, _, _) = Pc();
        var s = McpCatalog.Status(env); Assert.All(s, x => { Assert.False(x.HostFound); Assert.Contains("not found", x.HostText); });
        var r = await Install("revit", env);
        Assert.False(r.Ok); Assert.Contains("Revit was not found", r.Message);
    }

    [Fact]
    public async Task A_claude_settings_file_that_is_not_valid_json_is_left_alone()
    {
        var (env, http, _, _) = Pc(Acad()); Vendor(http, "bimwright/dwg-mcp", "DwgMcp", "dwg-mcp.exe");
        var cfg = Path.Combine(env.AppData, "Claude", "claude_desktop_config.json"); Directory.CreateDirectory(Path.GetDirectoryName(cfg)!); File.WriteAllText(cfg, "{ not json");
        var r = await Install("autocad", env);
        Assert.True(r.Ok); Assert.Equal("{ not json", File.ReadAllText(cfg));
    }

    [Fact]
    public void A_zip_that_tries_to_write_outside_its_folder_is_refused()
    {
        var z = Path.Combine(_root, "evil.zip"); File.WriteAllBytes(z, Zip(("..\\..\\evil.txt", "x")));
        var ex = Assert.Throws<McpInstallException>(() => McpFiles.Extract(z, Path.Combine(_root, "out")));
        Assert.Contains("outside", ex.Message); Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void Checksum_lists_are_read_by_file_name_or_as_a_single_hash()
    {
        var h = new string('a', 64); var h2 = new string('b', 64);
        Assert.Equal(h2, McpFiles.ExpectedHash($"{h}  one.zip\n{h2} *two.zip\n", "two.zip"));
        Assert.Equal(h, McpFiles.ExpectedHash(h + "  anything.zip"));
        Assert.Null(McpFiles.ExpectedHash("no hash here", "one.zip"));
    }

    [Fact]
    public async Task Navisworks_installs_nwd_mcp_and_NavisCoord_with_their_own_checks_and_registers_both()
    {
        var (env, http, probe, _) = Pc(new HostApp("navisworks", 2025, "Navisworks Manage 2025", @"C:\x"), new HostApp("navisworks", 2023, "Navisworks Manage 2023", @"C:\y"));
        Vendor(http, "bimwright/nwd-mcp", "NwdMcp", "nwd-mcp.exe");
        var baseUrl = "https://github.com/HorizunGroup/naviscoord-mcp/releases/download/v9.9.9";
        var addin = Zip(("NavisCoord.dll", "addin")); var rt = Zip(("bin/naviscoord-mcp.exe", "rt"));
        http.Files[$"{baseUrl}/NavisCoord-9.9.9-addin-NW2025.zip"] = addin; http.Files[$"{baseUrl}/naviscoord-runtime-9.9.9-win-x64.zip"] = rt;
        http.Files[$"{baseUrl}/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{Sha(addin)}  NavisCoord-9.9.9-addin-NW2025.zip\n{Sha(rt)}  naviscoord-runtime-9.9.9-win-x64.zip\n");
        var r = await Install("navisworks", env);
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(env.Plugins, "Bimwright.Nwd.bundle", "PackageContents.xml")));
        Assert.True(File.Exists(Path.Combine(env.AppData, "Autodesk", "Navisworks Manage 2025", "Plugins", "NavisCoord", "NavisCoord.dll")));
        Assert.False(Directory.Exists(Path.Combine(env.AppData, "Autodesk", "Navisworks Manage 2023")));                             // NavisCoord has no add-in for 2023
        var s = Cfg(env)["mcpServers"]!.AsObject();
        Assert.NotNull(s["nwd-mcp"]); Assert.EndsWith("naviscoord-mcp.exe", (string?)s["NavisCoord"]!["command"]); Assert.Equal("0", (string?)s["NavisCoord"]!["env"]!["NAVISCOORD_READ_ONLY"]);
        Assert.Equal(2, probe.Started.Count);
    }

    [Fact]
    public async Task A_missing_published_checksum_stops_NavisCoord_and_the_message_says_which_file()
    {
        var (env, http, _, _) = Pc(new HostApp("navisworks", 2025, "Navisworks Manage 2025", @"C:\x")); Vendor(http, "bimwright/nwd-mcp", "NwdMcp", "nwd-mcp.exe");
        var baseUrl = "https://github.com/HorizunGroup/naviscoord-mcp/releases/download/v9.9.9";
        http.Files[$"{baseUrl}/NavisCoord-9.9.9-addin-NW2025.zip"] = Zip(("a.dll", "a")); http.Files[$"{baseUrl}/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes("");
        var r = await Install("navisworks", env);
        Assert.False(r.Ok); Assert.Contains("NavisCoord-9.9.9-addin-NW2025.zip", r.Message);
    }

    [Fact]
    public void The_servers_that_are_installed_are_added_to_the_assistants_own_chat_configuration()
    {
        var reg = new McpServerRegistry(Path.Combine(_root, "mcp-servers.json"));
        reg.Upsert("dwg-mcp", new McpServerEntry(@"C:\a\dwg-mcp.exe", [], null));
        reg.Upsert("NavisCoord", new McpServerEntry(@"C:\b\nc.exe", ["--x"], new() { ["K"] = "V" }));
        var file = Pec.Qc.Core.Claude.ClaudeCli.WriteMcpConfig(Path.Combine(_root, "cfg"), @"C:\pec\Pec.Qc.Mcp.exe", null, reg.Load());
        var j = JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!.AsObject();
        Assert.Equal(@"C:\pec\Pec.Qc.Mcp.exe", (string?)j["pec-qc"]!["command"]); Assert.Equal(@"C:\a\dwg-mcp.exe", (string?)j["dwg-mcp"]!["command"]); Assert.Equal("V", (string?)j["NavisCoord"]!["env"]!["K"]);
        var args = Pec.Qc.Core.Claude.ClaudeCli.ChatArguments(file, "p", null, extraServers: reg.Load().Keys);
        Assert.Contains("mcp__dwg-mcp__*", args); Assert.Contains("mcp__NavisCoord__*", args); Assert.Contains("mcp__pec-qc__*", args);
    }

    [Fact]
    public async Task Real_AutoCAD_and_Navisworks_servers_are_downloaded_verified_installed_and_answer_when_the_network_test_is_switched_on()
    {
        if (Environment.GetEnvironmentVariable("PEC_QC_MCP_NET") != "1") return;                                                     // downloads about 130 MB from GitHub
        var env = new McpEnv
        {
            AppData = Path.Combine(_root, "AppData"), LocalAppData = Path.Combine(_root, "Local"), ProgramFiles = Path.Combine(_root, "PF"), Temp = Path.Combine(_root, "Temp"), DataDir = Path.Combine(_root, "Data"),
            IsRunning = _ => false,
            HostsOverride = () => [new HostApp("autocad", 2026, "AutoCAD 2026", @"C:\x"), new HostApp("navisworks", 2026, "Navisworks Manage 2026", @"C:\y")],
        };
        Directory.CreateDirectory(env.Temp);
        var log = new List<string>();
        var a = await Install("autocad", env, log);
        Assert.True(a.Ok, a.Message + "\n" + string.Join("\n", log.TakeLast(12)));
        Assert.Contains(log, l => l.Contains("answered:"));
        var n = await Install("navisworks", env, log);
        Assert.True(n.Ok, n.Message + "\n" + string.Join("\n", log.TakeLast(12)));
        var s = Cfg(env)["mcpServers"]!.AsObject();
        Assert.NotNull(s["dwg-mcp"]); Assert.NotNull(s["nwd-mcp"]); Assert.NotNull(s["NavisCoord"]);
    }
}

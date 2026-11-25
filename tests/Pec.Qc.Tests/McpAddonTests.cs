using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Tests;

/// <summary>The MCP-server installers Setup offers for the Autodesk programs: the bundled list is consistent, detection, the hash check, and running a script.</summary>
public class McpAddonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-addons-" + Guid.NewGuid().ToString("N"));
    public McpAddonTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string RepoFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "third-party", "mcp-installers"))) return Path.Combine(d.FullName, "third-party", "mcp-installers");
        throw new DirectoryNotFoundException("third-party\\mcp-installers not found above the test folder");
    }

    // ----- what is bundled
    [Fact]
    public void The_bundled_manifest_lists_the_five_installers_and_every_script_matches_its_hash()
    {
        var addons = McpAddons.Load(RepoFolder());
        Assert.Equal(new[] { "autocad", "civil3d", "navisworks", "plant3d", "revit" }, addons.Select(a => a.Id).Order().ToArray());
        Assert.Equal(addons.Count, addons.Select(a => a.Id).Distinct().Count());
        foreach (var a in addons)
        {
            Assert.True(File.Exists(Path.Combine(RepoFolder(), a.File)), a.File + " is missing");
            Assert.True(McpAddons.ScriptMatches(RepoFolder(), a), a.File + " does not match the manifest: run scripts\\make-mcp-addon-manifest.ps1");
            Assert.NotEmpty(a.Detect); Assert.NotEmpty(a.Source); Assert.NotEmpty(a.Description);
            foreach (var d in a.Detect) _ = new Regex(d.Folder);                               // every rule compiles
        }
        Assert.True(addons.Single(a => a.Id == "revit").Source.Contains("licence", StringComparison.OrdinalIgnoreCase), "the unclear licence of the Revit server is stated");
    }

    [Fact]
    public void Every_bundled_script_is_a_one_click_installer_for_a_normal_user_and_never_asks_for_administrator_rights()
    {
        foreach (var a in McpAddons.Load(RepoFolder()))
        {
            var text = File.ReadAllText(Path.Combine(RepoFolder(), a.File));
            Assert.DoesNotContain("-Verb RunAs", text);
            Assert.DoesNotContain("Add-MpPreference", text);                                   // no antivirus exclusions
        }
    }

    // ----- loading
    private static string FakeManifest(params (string Id, string File, string Hash, string Folder, string? Contains)[] items) => JsonSerializer.Serialize(new
    {
        version = 1,
        addons = items.Select(i => new { id = i.Id, title = i.Id + " MCP", description = "d", file = i.File, sha256 = i.Hash, detect = new[] { new { folder = i.Folder, contains = i.Contains } }, source = "s", closesApps = false }),
    });

    [Fact]
    public void The_list_is_read_from_a_folder_and_from_the_zip_without_unpacking_even_with_backslashes_in_the_entry_names()
    {
        var folder = Path.Combine(_dir, "f"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.cmd"), "echo a");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), FakeManifest(("a", "a.cmd", McpAddons.Sha256Of(Path.Combine(folder, "a.cmd")), "^X$", null)));
        Assert.Single(McpAddons.Load(folder));
        var zip = Path.Combine(_dir, "p.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            // Windows PowerShell's Compress-Archive writes entry names with backslashes
            using var w = new StreamWriter(z.CreateEntry("mcp-addons\\manifest.json").Open()); w.Write(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        }
        Assert.Equal("a", Assert.Single(McpAddons.LoadFromZip(zip)).Id);
        Assert.Empty(McpAddons.LoadFromZip(Path.Combine(_dir, "none.zip")));
        Assert.Empty(McpAddons.Load(Path.Combine(_dir, "nofolder")));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ not json");
        Assert.Empty(McpAddons.Load(folder));
    }

    [Fact]
    public void A_manifest_entry_that_names_a_path_instead_of_a_plain_file_is_ignored()
    {
        var folder = Path.Combine(_dir, "f"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), FakeManifest(("evil", "..\\..\\Windows\\x.cmd", "00", "^X$", null), ("ok", "ok.cmd", "00", "^X$", null)));
        Assert.Equal("ok", Assert.Single(McpAddons.Load(folder)).Id);
    }

    // ----- detection
    [Fact]
    public void An_installer_is_offered_ticked_only_when_its_Autodesk_program_is_on_the_PC()
    {
        var adsk = Path.Combine(_dir, "Autodesk");
        foreach (var d in new[] { "Navisworks Manage 2027", "AutoCAD 2026\\PLNT3D", "AutoCAD 2021", "AutoCAD 2025\\C3D", "Revit 2018" }) Directory.CreateDirectory(Path.Combine(adsk, d));
        var all = McpAddons.Load(RepoFolder());
        var st = McpAddons.Statuses(all, adsk).ToDictionary(s => s.Addon.Id);
        Assert.True(st["navisworks"].Detected); Assert.Equal("Navisworks Manage 2027", st["navisworks"].DetectedAs);
        Assert.True(st["autocad"].Detected);                 // 2026 counts
        Assert.True(st["plant3d"].Detected); Assert.Contains("PLNT3D", st["plant3d"].DetectedAs);
        Assert.True(st["civil3d"].Detected); Assert.Contains("2025", st["civil3d"].DetectedAs);
        Assert.False(st["revit"].Detected);                  // Revit 2018 is older than the supported 2022-2027
        // only AutoCAD 2021 (too old) and nothing else
        var old = Path.Combine(_dir, "Autodesk2"); Directory.CreateDirectory(Path.Combine(old, "AutoCAD 2021"));
        Assert.All(McpAddons.Statuses(all, old), s => Assert.False(s.Detected));
        Assert.All(McpAddons.Statuses(all, Path.Combine(_dir, "no-autodesk-at-all")), s => Assert.False(s.Detected));
        // Plant and Civil 3D need their own sub-folder: a plain AutoCAD 2026 is neither
        var plain = Path.Combine(_dir, "Autodesk3"); Directory.CreateDirectory(Path.Combine(plain, "AutoCAD 2026"));
        var p = McpAddons.Statuses(all, plain).ToDictionary(s => s.Addon.Id);
        Assert.True(p["autocad"].Detected); Assert.False(p["plant3d"].Detected); Assert.False(p["civil3d"].Detected);
    }

    // ----- running
    private (McpAddon Addon, string Folder) Fake(string body, string id = "fake")
    {
        var folder = Path.Combine(_dir, "run-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, id + ".cmd");
        File.WriteAllText(script, "@echo off\r\n" + body + "\r\n");
        return (new McpAddon(id, id + " MCP", "d", id + ".cmd", McpAddons.Sha256Of(script), [], "s", false), folder);
    }

    [Fact]
    public void A_script_that_matches_is_run_waited_for_and_its_exit_code_recorded()
    {
        var (a, folder) = Fake("echo ran> \"%~dp0marker.txt\"\r\nexit /b 0");
        var r = McpAddons.Run(folder, a, dryRun: false);
        Assert.True(r.Ok); Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(Path.Combine(folder, "marker.txt")));
        Assert.Contains("fake", McpAddons.InstalledIds(folder));
        var (b, f2) = Fake("exit /b 3", "failing");
        var rb = McpAddons.Run(f2, b, dryRun: false);
        Assert.False(rb.Ok); Assert.Equal(3, rb.ExitCode); Assert.Contains("exit code 3", rb.Message);
        Assert.Empty(McpAddons.InstalledIds(f2));                                              // a failed installer is not recorded as installed
        Assert.True(File.Exists(Path.Combine(f2, McpAddons.ResultsFile)));                     // but the attempt is
    }

    [Fact]
    public void A_script_that_was_changed_after_the_release_was_built_is_not_run()
    {
        var (a, folder) = Fake("echo ran> \"%~dp0marker.txt\"");
        File.AppendAllText(Path.Combine(folder, a.File), "echo tampered\r\n");
        var r = McpAddons.Run(folder, a, dryRun: false);
        Assert.False(r.Ran); Assert.Contains("does not match", r.Message);
        Assert.False(File.Exists(Path.Combine(folder, "marker.txt")));
        Assert.False(McpAddons.ScriptMatches(folder, a));
    }

    [Fact]
    public void A_missing_script_and_an_empty_hash_are_refused()
    {
        var (a, folder) = Fake("echo x");
        File.Delete(Path.Combine(folder, a.File));
        Assert.Contains("missing", McpAddons.Run(folder, a, dryRun: false).Message);
        var (b, f2) = Fake("echo ran> \"%~dp0marker.txt\"", "nohash");
        var unpinned = b with { Sha256 = "" };
        Assert.False(McpAddons.Run(f2, unpinned, dryRun: false).Ran);
        Assert.False(File.Exists(Path.Combine(f2, "marker.txt")));
    }

    [Fact]
    public void A_dry_run_starts_nothing()
    {
        var (a, folder) = Fake("echo ran> \"%~dp0marker.txt\"");
        var r = McpAddons.Run(folder, a, dryRun: true);
        Assert.False(r.Ran); Assert.Contains("dry run", r.Message);
        Assert.False(File.Exists(Path.Combine(folder, "marker.txt")));
    }
}

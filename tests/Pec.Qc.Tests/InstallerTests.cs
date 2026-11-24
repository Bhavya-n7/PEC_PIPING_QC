using System.Text.Json.Nodes;
using Pec.Qc.Core;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Tests;

/// <summary>Everything in a temp folder, with Claude Code faked: nothing touches this PC's real Claude configuration, shortcuts or registry.</summary>
[Collection("EnvVar")]
public class InstallerTests
{
    private sealed class Rig : IDisposable
    {
        public readonly TempDir T = new();
        public readonly List<string> Log = [];
        public readonly List<string> Ran = [];
        public (int, string) NextResult = (0, "");
        public string? Claude = @"C:\bin\claude.exe";
        public readonly InstallPaths Paths;
        public readonly string Payload;
        private readonly string? _oldData = Environment.GetEnvironmentVariable("PEC_QC_DATA");

        public Rig(bool withSkill = true)
        {
            Environment.SetEnvironmentVariable("PEC_QC_DATA", System.IO.Path.Combine(T.Path, "data"));
            Payload = System.IO.Path.Combine(T.Path, "payload");
            T.File("payload/PecQcAssistant.exe", "exe");
            T.File("payload/mcp/Pec.Qc.Mcp.exe", "mcp");
            if (withSkill) T.File("payload/skills/qc-platform/SKILL.md", "---\nname: qc-platform\ndescription: x\n---\nbundled body");
            T.File("payload/prompts/master-prompt.md", "BUNDLED MASTER");
            T.File("payload/prompts/quick-tasks.json", "[]");
            Paths = new InstallPaths(System.IO.Path.Combine(T.Path, "app"), System.IO.Path.Combine(T.Path, "claude", "skills"),
                System.IO.Path.Combine(T.Path, "desktop", "claude_desktop_config.json"), System.IO.Path.Combine(T.Path, "start"), System.IO.Path.Combine(T.Path, "data"));
        }

        public Installer Make() => new(Paths, Log.Add, (exe, args) => { Ran.Add(System.IO.Path.GetFileNameWithoutExtension(exe) + " " + string.Join(' ', args)); return NextResult; },
            writeRegistry: false, findClaude: () => Claude);

        public void Dispose() { Environment.SetEnvironmentVariable("PEC_QC_DATA", _oldData); T.Dispose(); }
    }

    private static InstallOptions Only(bool code = false, bool desktop = false, bool skills = false) => new(code, desktop, StartMenuShortcut: false, CopySkills: skills);

    [Fact]
    public void Install_copies_the_app_writes_a_manifest_and_seeds_defaults_without_overwriting_the_shared_version()
    {
        using var r = new Rig();
        var shared = System.IO.Path.Combine(r.T.Path, "server");
        r.T.File("server/skills/qc-platform/SKILL.md", "---\nname: qc-platform\n---\nSERVER VERSION");
        var folders = new InstallFolders(System.IO.Path.Combine(shared, "skills"), System.IO.Path.Combine(shared, "learning"), System.IO.Path.Combine(shared, "documents"), shared);

        var m = r.Make().Install(r.Payload, Only(), "0.1.0", folders);

        Assert.True(File.Exists(System.IO.Path.Combine(r.Paths.AppDir, "PecQcAssistant.exe")));
        Assert.True(File.Exists(System.IO.Path.Combine(r.Paths.AppDir, "mcp", "Pec.Qc.Mcp.exe")));
        Assert.Equal("0.1.0", m.Version);
        Assert.True(File.Exists(r.Make().ManifestPath));
        var saved = Settings.Load();
        Assert.Equal(shared, saved.SharedRoot);
        Assert.Contains("SERVER VERSION", File.ReadAllText(System.IO.Path.Combine(shared, "skills", "qc-platform", "SKILL.md")));   // not overwritten
        Assert.Equal("BUNDLED MASTER", File.ReadAllText(saved.EffectiveMasterPromptFile));                                          // seeded where missing
        Assert.True(Directory.Exists(System.IO.Path.Combine(shared, "projects")));                                                 // the QC layout exists
        Assert.True(File.Exists(System.IO.Path.Combine(shared, "hub.json")));
    }

    [Fact]
    public void An_incomplete_package_is_refused_before_anything_is_changed()
    {
        using var r = new Rig();
        File.Delete(System.IO.Path.Combine(r.Payload, "mcp", "Pec.Qc.Mcp.exe"));
        Assert.Throws<InvalidOperationException>(() => r.Make().Install(r.Payload, Only(code: true, desktop: true, skills: true), "0.1.0"));
        Assert.False(Directory.Exists(r.Paths.AppDir));
        Assert.Empty(r.Ran);
    }

    [Fact]
    public void Claude_Code_is_registered_at_user_scope_replacing_an_older_entry()
    {
        using var r = new Rig();
        var m = r.Make().Install(r.Payload, Only(code: true), "0.1.0");
        var mcp = System.IO.Path.Combine(r.Paths.AppDir, "mcp", "Pec.Qc.Mcp.exe");
        Assert.Equal(["claude mcp remove pec-qc --scope user", $"claude mcp add pec-qc --scope user -- {mcp}"], r.Ran);
        Assert.True(m.ClaudeCode);
    }

    [Fact]
    public void A_failed_Claude_Code_registration_is_reported_not_thrown_and_not_recorded()
    {
        using var r = new Rig();
        r.NextResult = (1, "boom");
        var m = r.Make().Install(r.Payload, Only(code: true), "0.1.0");
        Assert.False(m.ClaudeCode);
        Assert.Contains(r.Log, l => l.Contains("Could not register") && l.Contains("boom"));
    }

    [Fact]
    public void Without_Claude_Code_nothing_is_run_and_the_install_still_succeeds()
    {
        using var r = new Rig { Claude = null };
        var m = r.Make().Install(r.Payload, Only(code: true), "0.1.0");
        Assert.Empty(r.Ran);
        Assert.False(m.ClaudeCode);
        Assert.Contains(r.Log, l => l.Contains("Claude Code not found"));
    }

    [Fact]
    public void Claude_Desktop_gets_our_server_merged_in_with_a_backup_and_the_other_servers_kept()
    {
        using var r = new Rig();
        r.T.File("desktop/claude_desktop_config.json", """{ "theme": "dark", "mcpServers": { "other": { "command": "x" } } }""");
        var m = r.Make().Install(r.Payload, Only(desktop: true), "0.1.0");
        var root = JsonNode.Parse(File.ReadAllText(r.Paths.ClaudeDesktopConfig))!.AsObject();
        Assert.Equal("dark", (string?)root["theme"]);
        Assert.NotNull(root["mcpServers"]!["other"]);
        Assert.Equal("on", (string?)root["mcpServers"]!["pec-qc"]!["env"]!["PEC_QC_APPROVAL"]);
        Assert.True(m.ClaudeDesktop);
        Assert.NotNull(m.DesktopConfigBackup);
        Assert.Contains("theme", File.ReadAllText(m.DesktopConfigBackup!));      // the backup is the file as it was
    }

    [Fact]
    public void A_Claude_Desktop_config_that_is_not_valid_JSON_is_left_alone()
    {
        using var r = new Rig();
        r.T.File("desktop/claude_desktop_config.json", "{ not json");
        var m = r.Make().Install(r.Payload, Only(desktop: true), "0.1.0");
        Assert.False(m.ClaudeDesktop);
        Assert.Equal("{ not json", File.ReadAllText(r.Paths.ClaudeDesktopConfig));
    }

    [Fact]
    public void A_skill_of_the_same_name_that_the_user_made_is_never_overwritten_but_our_own_is_refreshed()
    {
        using var r = new Rig();
        var userSkill = System.IO.Path.Combine(r.Paths.ClaudeSkillsDir, "qc-platform", "SKILL.md");
        r.T.File("claude/skills/qc-platform/SKILL.md", "THE USER'S OWN SKILL");
        var first = r.Make().Install(r.Payload, Only(skills: true), "0.1.0");
        Assert.Equal("THE USER'S OWN SKILL", File.ReadAllText(userSkill));
        Assert.Empty(first.Skills);
        Assert.Contains(r.Log, l => l.Contains("was not installed by this assistant"));

        // a skill we installed earlier is ours: the update refreshes it
        using var r2 = new Rig();
        r2.Make().Install(r2.Payload, Only(skills: true), "0.1.0");
        var target = System.IO.Path.Combine(r2.Paths.ClaudeSkillsDir, "qc-platform", "SKILL.md");
        Assert.Contains("bundled body", File.ReadAllText(target));
        File.WriteAllText(System.IO.Path.Combine(r2.Payload, "skills", "qc-platform", "SKILL.md"), "---\nname: qc-platform\n---\nNEW BODY");
        var second = r2.Make().Install(r2.Payload, Only(skills: true), "0.1.1");
        Assert.Contains("NEW BODY", File.ReadAllText(target));
        Assert.Single(second.Skills);
    }

    [Fact]
    public void An_update_keeps_the_choices_made_at_the_first_install()
    {
        var chosen = new InstallManifest("0.1.0", DateTime.Now, "x", [], ClaudeCode: false, ClaudeDesktop: true, ShortcutPath: null, DesktopConfigBackup: null);
        var o = InstallOptions.FromManifest(chosen);
        Assert.False(o.RegisterClaudeCode); Assert.True(o.RegisterClaudeDesktop); Assert.False(o.StartMenuShortcut); Assert.False(o.CopySkills);
        var all = InstallOptions.FromManifest(chosen with { ClaudeCode = true, ShortcutPath = @"C:\x.lnk", Skills = [@"C:\s"] });
        Assert.True(all.RegisterClaudeCode); Assert.True(all.StartMenuShortcut); Assert.True(all.CopySkills);
    }

    [Fact]
    public void Uninstall_removes_only_what_the_install_added_and_keeps_the_user_data()
    {
        using var r = new Rig();
        r.T.File("desktop/claude_desktop_config.json", """{ "mcpServers": { "other": { "command": "x" } } }""");
        var inst = r.Make();
        inst.Install(r.Payload, Only(code: true, desktop: true, skills: true), "0.1.0");
        var settings = Settings.Load(); settings.UserName = "Keep Me"; settings.Save();
        r.Ran.Clear();

        inst.Uninstall();

        Assert.False(Directory.Exists(r.Paths.AppDir));
        Assert.False(Directory.Exists(System.IO.Path.Combine(r.Paths.ClaudeSkillsDir, "qc-platform")));
        Assert.Equal(["claude mcp remove pec-qc --scope user"], r.Ran);
        var root = JsonNode.Parse(File.ReadAllText(r.Paths.ClaudeDesktopConfig))!.AsObject();
        Assert.Null(root["mcpServers"]!["pec-qc"]);
        Assert.NotNull(root["mcpServers"]!["other"]);                                         // a colleague's server is untouched
        Assert.Equal("Keep Me", Settings.Load().UserName);                                    // settings and chats stay
    }

    [Fact]
    public void Uninstall_closes_a_running_assistant_first_so_the_folder_can_really_be_removed()
    {
        // a real process whose image lives in the app folder and is called PecQcAssistant.exe (a copy of ping.exe, waiting on a long loop)
        using var r = new Rig();
        var ping = System.IO.Path.Combine(Environment.SystemDirectory, "PING.EXE");
        File.Copy(ping, System.IO.Path.Combine(r.Payload, "PecQcAssistant.exe"), true);
        var inst = r.Make();
        inst.Install(r.Payload, Only(), "0.1.0");
        var exe = System.IO.Path.Combine(r.Paths.AppDir, "PecQcAssistant.exe");
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.False(running.HasExited);
            inst.Uninstall();
            Assert.True(running.WaitForExit(15000), "the running assistant was not closed");
            Assert.False(Directory.Exists(r.Paths.AppDir), "the app folder was left behind");
        }
        finally { if (!running.HasExited) running.Kill(true); }
    }

    [Fact]
    public void A_Claude_Desktop_config_that_only_existed_because_of_the_install_is_removed_with_it()
    {
        using var r = new Rig();
        var inst = r.Make();
        inst.Install(r.Payload, Only(desktop: true), "0.1.0");
        Assert.True(File.Exists(r.Paths.ClaudeDesktopConfig));
        inst.Uninstall();
        Assert.False(File.Exists(r.Paths.ClaudeDesktopConfig));
    }

    [Fact]
    public void Uninstall_can_also_remove_the_user_data_when_asked()
    {
        using var r = new Rig();
        var inst = r.Make();
        inst.Install(r.Payload, Only(), "0.1.0", new InstallFolders("", "", ""));
        Assert.True(File.Exists(System.IO.Path.Combine(r.Paths.DataDir, "settings.json")));
        inst.Uninstall(removeUserData: true);
        Assert.False(Directory.Exists(r.Paths.DataDir));
    }

    [Fact]
    public void Copying_a_folder_works_when_its_name_appears_twice_in_the_path()
    {
        using var t = new TempDir();
        var from = System.IO.Path.Combine(t.Path, "app", "app");
        t.File("app/app/app/a.txt", "1"); t.File("app/app/b.txt", "2");
        var to = System.IO.Path.Combine(t.Path, "out");
        Installer.CopyDirectory(from, to);
        Assert.Equal("1", File.ReadAllText(System.IO.Path.Combine(to, "app", "a.txt")));
        Assert.Equal("2", File.ReadAllText(System.IO.Path.Combine(to, "b.txt")));
    }

    [Fact]
    public void The_readiness_check_requires_Claude_Code_and_the_package_but_not_Navisworks()
    {
        using var r = new Rig { Claude = null };
        var items = r.Make().Check(r.Payload, r.T.Path);
        Assert.True(items.Single(i => i.Name == "Claude Code").Required);
        Assert.False(items.Single(i => i.Name == "Claude Code").Ok);
        Assert.False(items.Single(i => i.Name == "Navisworks Manage").Required);
        Assert.True(items.Single(i => i.Name == "Assistant files").Ok);
        Assert.True(items.Single(i => i.Name == "Shared office folder").Ok);
        File.Delete(System.IO.Path.Combine(r.Payload, "PecQcAssistant.exe"));
        Assert.False(r.Make().Check(r.Payload).Single(i => i.Name == "Assistant files").Ok);
    }

    [Fact]
    public void Installing_Claude_Code_runs_the_official_installer_only_when_it_is_missing()
    {
        using var r = new Rig { Claude = null };
        var inst = r.Make();
        Assert.Null(inst.InstallClaudeCode(out var err));     // the fake installer "ran" but claude.exe still is not there
        Assert.Contains("claude.exe was not found", err);
        Assert.Single(r.Ran);
        Assert.Contains("claude.ai/install.ps1", r.Ran[0]);

        r.Claude = @"C:\bin\claude.exe"; r.Ran.Clear();
        Assert.Equal(@"C:\bin\claude.exe", inst.InstallClaudeCode(out _));
        Assert.Empty(r.Ran);                                    // already there: nothing is run
    }
}

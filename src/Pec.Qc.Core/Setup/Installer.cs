using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Install;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Core.Setup;

public sealed record InstallPaths(string AppDir, string ClaudeSkillsDir, string ClaudeDesktopConfig, string StartMenuDir, string DataDir)
{
    /// <summary>Per-user locations. PEC_QC_APPDIR moves only the app folder (used to test an install without touching a real one).</summary>
    public static InstallPaths Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var app = Environment.GetEnvironmentVariable("PEC_QC_APPDIR");
        return new(string.IsNullOrWhiteSpace(app) ? Path.Combine(local, "Programs", "PEC QC Assistant") : app,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills"),
            Path.Combine(roaming, "Claude", "claude_desktop_config.json"),
            Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "PEC"),
            Settings.DataDirectory);
    }
}

public sealed record CheckItem(string Name, bool Ok, string Detail, bool Required);

public sealed record InstallOptions(bool RegisterClaudeCode = true, bool RegisterClaudeDesktop = true, bool StartMenuShortcut = true, bool CopySkills = true)
{
    /// <summary>An update keeps what the user chose at the first install: nothing new is registered, copied or added behind their back.</summary>
    public static InstallOptions FromManifest(InstallManifest m) =>
        new(m.ClaudeCode, m.ClaudeDesktop, m.ShortcutPath is not null, m.Skills.Count > 0);
}

/// <summary>What the installer did, so that uninstall can undo exactly that.</summary>
public sealed record InstallManifest(string Version, DateTime Installed, string AppDir, IReadOnlyList<string> Skills,
    bool ClaudeCode, bool ClaudeDesktop, string? ShortcutPath, string? DesktopConfigBackup);

/// <summary>
/// Per-user install (no administrator rights): copies the app (with the MCP server, skills, prompts and help), registers the MCP server
/// with Claude Code and Claude Desktop, copies the skills for Claude Code, adds a Start-menu shortcut and an uninstall entry.
/// Every file outside the app folder that is changed is backed up first, and a file that is not ours is never overwritten.
/// </summary>
public sealed class Installer
{
    public const string McpName = ClaudeCli.McpName;
    public const string AppExe = "PecQcAssistant.exe";
    public const string SetupExe = "PecQcSetup.exe";
    private readonly InstallPaths _p;
    private readonly bool _writeRegistry;
    private readonly Action<string> _log;
    private readonly Func<string, IReadOnlyList<string>, (int Code, string Output)> _run;
    private readonly Func<string?> _findClaude;

    public Installer(InstallPaths? paths = null, Action<string>? log = null, Func<string, IReadOnlyList<string>, (int, string)>? run = null, bool writeRegistry = true, Func<string?>? findClaude = null)
    {
        _findClaude = findClaude ?? (() => ClaudeCli.Find());
        _writeRegistry = writeRegistry;
        _p = paths ?? InstallPaths.Default();
        _log = log ?? (_ => { });
        _run = run ?? RunProcess;
    }

    public string ManifestPath => Path.Combine(_p.AppDir, "install-manifest.json");

    /// <summary>The manifest of the install that is on this PC now, or null (first install, or an unreadable file).</summary>
    public InstallManifest? ReadManifest()
    {
        try { return File.Exists(ManifestPath) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(ManifestPath)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public List<CheckItem> Check(string? payloadDir = null, string? sharedFolder = null)
    {
        var items = new List<CheckItem>();
        var claude = _findClaude();
        items.Add(new("Claude Code", claude is not null, claude ?? "Not installed. Setup can install it for you (tick the option below), or install it from https://claude.com/claude-code. It is needed to sign in and chat.", true));
        var nw = NavisworksInstall.Find();
        items.Add(new("Navisworks Manage", nw is not null, nw is null ? "Not found. Needed for isometric and GA checks (they read the 3D model through it). Install it later, or set its folder in Settings. Works with an education or commercial licence, as your own licence allows." : $"{nw.Name} in {nw.Root}", false));
        if (!string.IsNullOrWhiteSpace(sharedFolder))
        {
            var st = SharedHub.Status(sharedFolder);
            items.Add(new("Shared office folder", st.Reachable && st.Writable, !st.Reachable ? $"Cannot be reached: {sharedFolder}. The assistant will use local folders until it can." : st.Writable ? sharedFolder : "Reachable but read only for you: you can use what is in it, but cannot save shared items.", false));
        }
        if (payloadDir is not null)
        {
            var ok = File.Exists(Path.Combine(payloadDir, AppExe)) && AssistantSetup.FindMcpExe(payloadDir) is not null;
            items.Add(new("Assistant files", ok, ok ? "Found next to the installer." : $"{AppExe} or mcp\\Pec.Qc.Mcp.exe is missing from the installer package.", true));
        }
        return items;
    }

    /// <param name="payloadDir">Folder with PecQcAssistant.exe, mcp\ and skills\ (extracted payload).</param>
    public InstallManifest Install(string payloadDir, InstallOptions options, string? version = null, InstallFolders? folders = null)
    {
        if (AssistantSetup.FindMcpExe(payloadDir) is null || !File.Exists(Path.Combine(payloadDir, AppExe)))
            throw new InvalidOperationException("The installer package is incomplete (app or MCP server missing).");

        version ??= Updates.CurrentVersion;
        var previous = ReadManifest();
        StopRunningApp();
        CleanupAside(_p.AppDir);
        _log($"Installing version {version}: copying the app to {_p.AppDir}");
        CopyDirectory(payloadDir, _p.AppDir);
        var mcpExe = AssistantSetup.FindMcpExe(_p.AppDir)!;

        if (folders is not null)
        {
            // the chosen folders are saved as the assistant's settings; bundled skills, master prompt and quick tasks are copied in
            // only where nothing exists yet, so a shared folder that already holds newer versions is never overwritten
            var st = Settings.Load();
            if (!string.IsNullOrWhiteSpace(folders.Shared))
            {
                st.SharedRoot = folders.Shared.Trim();
                var hub = SharedHub.Ensure(st.SharedRoot);
                _log(hub.Reachable ? $"Shared folder: {st.SharedRoot}" + (hub.Writable ? "" : " (read only: new tools and learning cannot be saved there)") : $"Shared folder not reachable: {st.SharedRoot} (the assistant will use local folders until it is)");
            }
            // separate folders only when they differ from what the shared folder would give
            var derived = new Settings { SharedRoot = st.SharedRoot };
            string? Own(string chosen, string def) => string.IsNullOrWhiteSpace(chosen) || chosen.Equals(def, StringComparison.OrdinalIgnoreCase) ? null : chosen.Trim();
            st.SkillsDirectory = Own(folders.Skills, derived.EffectiveSkillsDirectory);
            st.KnowledgeDirectory = Own(folders.Learning, derived.EffectiveKnowledgeDirectory);
            st.DocumentsDirectory = Own(folders.Documents, derived.EffectiveDocumentsDirectory);
            st.Save();
            PromptBuilder.SeedDefaults(st, _p.AppDir);
            _log($"Skills: {st.EffectiveSkillsDirectory}");
            _log($"Learning: {st.EffectiveKnowledgeDirectory}");
            _log($"Documents: {st.EffectiveDocumentsDirectory}");
            if (st.HasShared) _log($"Custom tools: {st.EffectiveToolsDirectory} (tools made on any PC are shared here)");
        }

        // skills for Claude Code (~/.claude/skills): ours are refreshed, a skill of the same name that is NOT ours is left alone
        var skills = new List<string>();
        if (options.CopySkills)
        {
            var src = Path.Combine(_p.AppDir, "skills");
            var ours = new HashSet<string>(previous?.Skills ?? [], StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(src))
                foreach (var dir in Directory.GetDirectories(src))
                {
                    var target = Path.Combine(_p.ClaudeSkillsDir, Path.GetFileName(dir));
                    if (Directory.Exists(target) && !ours.Contains(target))
                    {
                        _log($"Skill '{Path.GetFileName(dir)}' already exists in {_p.ClaudeSkillsDir} and was not installed by this assistant: left as it is.");
                        continue;
                    }
                    CopyDirectory(dir, target);
                    skills.Add(target);
                }
            _log($"Copied {skills.Count} skills to {_p.ClaudeSkillsDir}");
        }

        var claudeCode = false;
        if (options.RegisterClaudeCode)
        {
            var claude = _findClaude();
            if (claude is null) _log("Claude Code not found: the MCP server was not registered with it (the assistant app does not need that).");
            else
            {
                _run(claude, ["mcp", "remove", McpName, "--scope", "user"]);          // idempotent: ignore "not found"
                var (code, output) = _run(claude, ["mcp", "add", McpName, "--scope", "user", "--", mcpExe]);
                claudeCode = code == 0;
                _log(claudeCode ? "Registered the MCP server with Claude Code." : "Could not register with Claude Code: " + output.Trim());
            }
        }

        string? backup = null; var desktop = false;
        if (options.RegisterClaudeDesktop)
        {
            (desktop, backup) = RegisterClaudeDesktop(mcpExe);
            _log(desktop ? "Registered the MCP server with Claude Desktop (restart Claude Desktop to see it)." : "Claude Desktop config not changed.");
        }

        string? shortcut = null;
        if (options.StartMenuShortcut)
        {
            shortcut = Path.Combine(_p.StartMenuDir, "PEC QC Assistant.lnk");
            CreateShortcut(shortcut, Path.Combine(_p.AppDir, AppExe), _p.AppDir);
            _log("Added a Start-menu shortcut.");
        }

        var m = new InstallManifest(version, DateTime.Now, _p.AppDir, skills, claudeCode, desktop, shortcut, backup ?? previous?.DesktopConfigBackup);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        WriteUninstallEntry(version);
        _log("Done.");
        return m;
    }

    /// <summary>An upgrade replaces files that a running assistant has open: ask it to close first.</summary>
    public void StopRunningApp()
    {
        var exe = Path.Combine(_p.AppDir, AppExe);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
        {
            try
            {
                if (!string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                _log("Closing the running assistant...");
                if (!p.CloseMainWindow() || !p.WaitForExit(8000)) { p.Kill(true); p.WaitForExit(5000); }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // the MCP server may still be held by Claude Code or Claude Desktop sessions: they are not killed; CopyFile moves a locked file aside, and the session keeps using the old copy until it restarts
    }

    /// <summary>
    /// Installs Claude Code with Anthropic's official installer (https://claude.ai/install.ps1; per user, no administrator rights,
    /// no Node.js needed, needs internet). Returns the path of claude.exe, or null with the reason in <paramref name="error"/>.
    /// </summary>
    public string? InstallClaudeCode(out string? error)
    {
        error = null;
        if (_findClaude() is { } existing) { _log($"Claude Code is already installed: {existing}"); return existing; }
        _log("Installing Claude Code with the official installer (needs internet, takes a minute)...");
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var (code, output) = _run(ps, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "irm https://claude.ai/install.ps1 | iex"]);
        var found = _findClaude();
        if (found is null)
        {
            error = code == 0 ? "The installer finished but claude.exe was not found." : $"The Claude Code installer failed (exit code {code}). Check the internet connection, or install it by hand from https://claude.com/claude-code. {output.Trim()}";
            _log(error);
            return null;
        }
        _log($"Claude Code installed: {found}");
        return found;
    }

    /// <summary>Removes what the install added: the Claude registrations, the skills it copied, the shortcut, the uninstall entry and the app folder. Settings, chats and lessons are kept unless asked.</summary>
    public void Uninstall(bool removeUserData = false)
    {
        StopRunningApp();       // a running assistant holds its files open: without this the folder cannot be removed
        var m = ReadManifest();
        if (m is not null)
        {
            foreach (var s in m.Skills) TryDelete(s);
            if (m.ClaudeCode && _findClaude() is { } claude) _run(claude, ["mcp", "remove", McpName, "--scope", "user"]);
            if (m.ClaudeDesktop) UnregisterClaudeDesktop();
            if (m.ShortcutPath is not null && File.Exists(m.ShortcutPath)) File.Delete(m.ShortcutPath);
            _log("Removed the registrations, skills and shortcut.");
        }
        RemoveUninstallEntry();
        if (removeUserData) TryDelete(_p.DataDir);
        foreach (var f in Directory.Exists(_p.AppDir) ? Directory.GetFiles(_p.AppDir, "*", SearchOption.AllDirectories) : [])
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        TryDelete(_p.AppDir);
    }

    // ---- Claude Desktop: merge our server into mcpServers, keep everything else, back the file up

    public (bool Changed, string? Backup) RegisterClaudeDesktop(string mcpExe)
    {
        var file = _p.ClaudeDesktopConfig;
        JsonObject root;
        if (File.Exists(file))
        {
            try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { _log("claude_desktop_config.json is not valid JSON: left alone."); return (false, null); }
        }
        else root = new JsonObject();
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        servers[McpName] = new JsonObject
        {
            ["command"] = mcpExe,
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject { ["PEC_QC_APPROVAL"] = "on" },
        };
        root["mcpServers"] = servers;
        var backup = SafeWrite.Backup(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (true, backup);
    }

    private void UnregisterClaudeDesktop()
    {
        var file = _p.ClaudeDesktopConfig;
        if (!File.Exists(file)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || root["mcpServers"] is not JsonObject servers || !servers.Remove(McpName)) return;
            if (servers.Count == 0 && root.Count == 1) { File.Delete(file); return; }      // the file only existed because of us: do not leave an empty one behind
            SafeWrite.Backup(file);
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException) { }
    }

    // ---- helpers

    public static void ExtractPayload(string zipFile, string targetDir)
    {
        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        ZipFile.ExtractToDirectory(zipFile, targetDir);
    }

    /// <summary>Copies a folder tree by relative path (a folder name that appears twice in a path is not a problem).</summary>
    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) CopyFile(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    public const string AsideMarker = ".old-";

    /// <summary>
    /// Copies a file over an existing one. A program that is running holds its files open (the MCP server is kept alive by Claude Code and Claude Desktop sessions, which this installer must not kill):
    /// Windows does not allow overwriting such a file but does allow renaming it, so the old file is moved aside and the new one put in its place. The running program keeps the old copy until it ends;
    /// the leftover is removed by <see cref="CleanupAside"/>.
    /// </summary>
    public static void CopyFile(string from, string to)
    {
        try { File.Copy(from, to, true); return; }
        catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && File.Exists(to)) { }
        File.Move(to, to + AsideMarker + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
        File.Copy(from, to, true);
    }

    /// <summary>Deletes the files that <see cref="CopyFile"/> moved aside, as far as they are no longer in use. Returns how many were removed.</summary>
    public static int CleanupAside(string folder)
    {
        var n = 0;
        if (!Directory.Exists(folder)) return 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*" + AsideMarker + "*", SearchOption.AllDirectories))
                try { File.Delete(f); n++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return n;
    }

    private static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static (int, string) RunProcess(string exe, IReadOnlyList<string> args)
    {
        var psi = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", exe } } : new ProcessStartInfo(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return (p.ExitCode, o);
    }

    private static void CreateShortcut(string lnk, string target, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        // WScript.Shell is the standard way to make a .lnk without extra packages
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t is null) return;
        dynamic sh = Activator.CreateInstance(t)!;
        dynamic s = sh.CreateShortcut(lnk);
        s.TargetPath = target; s.WorkingDirectory = workDir; s.Description = "PEC QC Assistant"; s.Save();
    }

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PEC QC Assistant";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void WriteUninstallEntryWin(string version)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "PEC QC Assistant");
        k.SetValue("DisplayVersion", version);
        k.SetValue("Publisher", "PEC");
        k.SetValue("InstallLocation", _p.AppDir);
        k.SetValue("UninstallString", $"\"{Path.Combine(_p.AppDir, "Setup", SetupExe)}\" /uninstall");
    }

    private void WriteUninstallEntry(string version) { if (_writeRegistry && OperatingSystem.IsWindows()) WriteUninstallEntryWin(version); }
    private void RemoveUninstallEntry() { if (_writeRegistry && OperatingSystem.IsWindows()) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
}

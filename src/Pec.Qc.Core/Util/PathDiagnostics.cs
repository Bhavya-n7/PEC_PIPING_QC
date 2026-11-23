using System.Text;

namespace Pec.Qc.Core.Util;

/// <summary>
/// One plain log of WHERE things were written (lessons, tools, activity, skills) and where the app looked: <c>%APPDATA%\PEC\QcAssistant\diagnostics.log</c>.
/// When a colleague says "my lessons are not on the shared folder", this file shows the exact folder each write went to and why one failed.
/// Never throws, never holds a secret (only paths, sizes and the user's employee code), cut when it grows past 512 KB.
/// </summary>
public static class PathDiagnostics
{
    private static readonly object Gate = new();
    public static string LogFile => Path.Combine(Settings.DataDirectory, "diagnostics.log");

    public static void Note(string source, string what, string? path = null, string? problem = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  [").Append(source).Append("] ").Append(what);
            if (path is not null) sb.Append("  ").Append(path).Append(Reach(path));
            if (problem is not null) sb.Append("  PROBLEM: ").Append(problem.Replace('\n', ' '));
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.DataDirectory);
                var f = LogFile;
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.WriteAllText(f, File.ReadAllText(f)[^(128 * 1024)..]);
                File.AppendAllText(f, sb.AppendLine().ToString());
            }
        }
        catch (Exception) { /* diagnostics must never break the work */ }
    }

    private static string Reach(string path)
    {
        try { return File.Exists(path) ? $"  (file, {new FileInfo(path).Length} bytes)" : Directory.Exists(path) ? "  (folder, found)" : "  (not found)"; }
        catch (Exception) { return "  (cannot be checked)"; }
    }

    /// <summary>At start: every folder the app will use, so a wrong or offline path is visible at once.</summary>
    public static void Snapshot(Settings s)
    {
        var who = UserIdentity.Current(s);
        Note("start", $"version {Setup.Updates.CurrentVersion}, user {who.EmployeeCode} on {Environment.MachineName}, shared folder {(s.HasShared ? "set" : "NOT set (everything stays on this PC)")}");
        Note("folder", "data", Settings.DataDirectory);
        if (s.HasShared)
        {
            var r = s.SharedRoot!;
            Note("folder", "shared root", r);
            foreach (var sub in new[] { "learning", "learning\\inbox", "activity", "tools\\csharp", "skills", "contributions", "audit" }) Note("folder", "shared " + sub, Path.Combine(r, sub));
        }
        Note("folder", "local lessons", Path.Combine(Settings.DataDirectory, "learning"));
        Note("folder", "local tools", s.LocalToolsDirectory);
        Note("folder", "local activity", Path.Combine(Settings.DataDirectory, "activity"));
        Note("folder", "skills (effective)", s.EffectiveSkillsDirectory);
        Note("folder", "learning (effective)", s.EffectiveKnowledgeDirectory);
    }
}

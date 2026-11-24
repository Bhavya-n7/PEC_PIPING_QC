namespace Pec.Qc.Core.Knowledge;

public sealed record OfflineSyncReport(bool SharedReachable, int Files, int Lines, int Skipped, IReadOnlyList<string> Problems)
{
    public override string ToString() => !SharedReachable ? "shared folder not reachable: nothing synced" : $"{Lines} line(s) from {Files} file(s) sent to the shared folder" + (Skipped > 0 ? $", {Skipped} already there" : "");
}

/// <summary>
/// Lessons and activity are written to the shared folder; when it is away they are kept on this PC (the writers fall back to the local data folder). This sends them
/// back once the share is reachable: every local line that is not already in the shared file is appended to it (files on the share cannot be deleted, so nothing is
/// ever overwritten or removed there), and only then is the local copy removed. Running it twice, or from two PCs, adds nothing twice.
/// </summary>
public static class OfflineQueue
{
    private static readonly string[] Folders = [Path.Combine("learning", "inbox"), "activity"];

    public static OfflineSyncReport Sync(Settings settings)
    {
        if (!settings.HasShared) return new OfflineSyncReport(true, 0, 0, 0, []);
        var shared = settings.SharedRoot!;
        var local = Settings.DataDirectory;
        if (string.Equals(Path.GetFullPath(shared).TrimEnd('\\'), Path.GetFullPath(local).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return new OfflineSyncReport(true, 0, 0, 0, []);
        // is the share there at all? (a missing or offline share must not make the local copies disappear)
        try { if (!Directory.Exists(shared)) return new OfflineSyncReport(false, 0, 0, 0, []); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new OfflineSyncReport(false, 0, 0, 0, []); }

        int files = 0, lines = 0, skipped = 0;
        var problems = new List<string>();
        foreach (var folder in Folders)
        {
            var src = Path.Combine(local, folder);
            if (!Directory.Exists(src)) continue;
            foreach (var file in Directory.GetFiles(src, "*.jsonl", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file);
                var target = Path.Combine(shared, folder, rel);
                try
                {
                    var mine = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToList();
                    var there = File.Exists(target) ? new HashSet<string>(File.ReadAllLines(target)) : [];
                    var missing = mine.Where(l => !there.Contains(l)).ToList();
                    if (missing.Count > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.AppendAllText(target, string.Concat(missing.Select(l => l + "\n")));
                    }
                    // verify before removing the local copy: every line must now be on the share
                    var now = new HashSet<string>(File.ReadAllLines(target));
                    if (!mine.All(now.Contains)) { problems.Add($"{rel}: not all lines reached the shared folder; the local copy was kept"); continue; }
                    File.Delete(file);
                    files++; lines += missing.Count; skipped += mine.Count - missing.Count;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{rel}: {e.Message}");
                    if (!Directory.Exists(shared)) return new OfflineSyncReport(false, files, lines, skipped, problems);
                }
            }
        }
        return new OfflineSyncReport(true, files, lines, skipped, problems);
    }
}

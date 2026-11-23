using System.Text;

namespace Pec.Qc.Core.Util;

/// <summary>Result of a planned or performed change to one file.</summary>
public sealed record FileChange(string Path, bool Applied, string? BackupPath, string Diff, int LinesChanged);

/// <summary>
/// Every write goes through here: build a diff (dry run), and when applied take a backup
/// <c>&lt;file&gt;.bak_YYYYMMDD-HHMMSS</c> (the form the PEC team already uses) before writing.
/// </summary>
public static class SafeWrite
{
    public static string BackupName(string path, DateTime? when = null) =>
        $"{path}.bak_{(when ?? DateTime.Now):yyyyMMdd-HHmmss}";

    /// <summary>Copy <paramref name="path"/> to a timestamped backup; returns the backup path (or null when the file does not exist).</summary>
    public static string? Backup(string path)
    {
        if (!File.Exists(path)) return null;
        var target = BackupName(path);
        for (var i = 1; File.Exists(target); i++) target = BackupName(path) + "_" + i;
        File.Copy(path, target);
        return target;
    }

    /// <summary>Plan (apply=false) or perform (apply=true) replacing a text file's content.</summary>
    public static FileChange WriteText(string path, string newText, bool apply, Encoding? encoding = null)
    {
        var old = File.Exists(path) ? File.ReadAllText(path) : "";
        var (diff, n) = LineDiff.Unified(old, newText, Path.GetFileName(path));
        if (!apply || n == 0) return new FileChange(path, false, null, diff, n);
        var backup = Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, newText, encoding ?? DetectEncoding(path));
        return new FileChange(path, true, backup, diff, n);
    }

    /// <summary>Keep the file's own encoding (BOM or not) when rewriting it.</summary>
    public static Encoding DetectEncoding(string path)
    {
        if (!File.Exists(path)) return new UTF8Encoding(false);
        Span<byte> head = stackalloc byte[3];
        using var fs = File.OpenRead(path);
        var n = fs.Read(head);
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new UTF8Encoding(true);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
        return Encoding.Latin1; // Isogen text files are plain 8-bit ASCII
    }
}

/// <summary>Small line diff (LCS) good enough for configuration files of a few thousand lines.</summary>
public static class LineDiff
{
    public static (string Text, int Changed) Unified(string oldText, string newText, string name, int context = 2)
    {
        var a = Split(oldText);
        var b = Split(newText);
        // trim common head/tail to keep the LCS table small
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        var am = a[pre..(a.Length - suf)];
        var bm = b[pre..(b.Length - suf)];
        if (am.Length == 0 && bm.Length == 0) return ("(no change)", 0);

        var ops = new List<(char Op, string Line)>();
        if ((long)am.Length * bm.Length > 4_000_000)
        {
            ops.AddRange(am.Select(l => ('-', l)));
            ops.AddRange(bm.Select(l => ('+', l)));
        }
        else
        {
            var lcs = new int[am.Length + 1, bm.Length + 1];
            for (int i = am.Length - 1; i >= 0; i--)
                for (int j = bm.Length - 1; j >= 0; j--)
                    lcs[i, j] = am[i] == bm[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < am.Length || y < bm.Length)
            {
                if (x < am.Length && y < bm.Length && am[x] == bm[y]) { ops.Add((' ', am[x])); x++; y++; }
                else if (x < am.Length && (y == bm.Length || lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(('-', am[x])); x++; }
                else { ops.Add(('+', bm[y])); y++; }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- {name}").AppendLine($"+++ {name} (new)");
        sb.AppendLine($"@@ line {pre + 1} @@");
        foreach (var l in a.Skip(Math.Max(0, pre - context)).Take(Math.Min(context, pre))) sb.AppendLine("  " + l);
        int changed = 0;
        foreach (var (op, line) in ops)
        {
            if (op != ' ') changed++;
            sb.AppendLine($"{op} {line}");
        }
        foreach (var l in a.Skip(a.Length - suf).Take(context)) sb.AppendLine("  " + l);
        return (sb.ToString().TrimEnd(), changed);
    }

    private static string[] Split(string s) =>
        s.Length == 0 ? [] : s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}

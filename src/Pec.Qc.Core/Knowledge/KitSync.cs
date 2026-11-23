using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Knowledge;

public enum KitKind { Skill, Command, Agent }

public sealed record KitItem(KitKind Kind, string Name, string Path, string Hash);
public sealed record KitPublishResult(IReadOnlyList<string> Published, IReadOnlyList<string> Unchanged, IReadOnlyDictionary<string, IReadOnlyList<string>> Blocked);
public sealed record KitPullResult(IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> KeptLocal);
public sealed record Contribution(string Contributor, KitKind Kind, string Name, string Path);

/// <summary>
/// Skills, slash commands and agents that users create with Claude (in their own .claude folder) are shared through the office folder:
/// PUBLISH copies them to a review inbox (contributions\&lt;user@machine&gt;\...), an administrator APPROVES them into skills\ commands\ agents\ (backing up what
/// they replace), and PULL installs the approved ones into every other user's .claude folder. Nothing a user made reaches the others without review,
/// and nothing is published that looks like it holds a server name, address, password or key. Hooks and settings are never synchronised.
/// </summary>
public sealed class KitSync
{
    /// <summary>The skills that ship with the product: never published as someone's contribution.</summary>
    public static readonly string[] ProductSkills =
    [
        "smart3d-engineer", "smart3d-modeling", "smart3d-admin", "smart3d-specs", "smart3d-isometrics", "smart3d-ga-drawings",
        "smart3d-reports", "smart3d-qc", "smart3d-troubleshoot", "model-conversion", "2d-to-3d", "smart3d-share-skills", "smart3d-graphify",
    ];

    private readonly string _claudeDir, _shared, _who, _manifestFile;

    public KitSync(string claudeDir, string sharedRoot, string user, string machine, string? manifestFile = null)
    {
        _claudeDir = claudeDir; _shared = sharedRoot;
        _who = Safe(user) + "@" + Safe(machine);
        _manifestFile = manifestFile ?? System.IO.Path.Combine(Settings.DataDirectory, "kit-manifest.json");
    }

    public static KitSync ForThisPc(Settings s) => new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        s.SharedRoot ?? throw new InvalidOperationException("No shared folder is set."), Environment.UserName, Environment.MachineName);

    private static string Safe(string s) => Regex.Replace(s, @"[^A-Za-z0-9._-]", "_");
    private static string Folder(KitKind k) => k switch { KitKind.Skill => "skills", KitKind.Command => "commands", _ => "agents" };

    // ------------------------------------------------------------------ discovery

    /// <summary>What lives in a .claude (or shared) root: skills are folders with a SKILL.md; commands and agents are single .md files.</summary>
    public static IEnumerable<KitItem> Discover(string root, string? skillsDir = null)
    {
        var sk = skillsDir ?? System.IO.Path.Combine(root, "skills");
        if (Directory.Exists(sk))
            foreach (var d in Directory.GetDirectories(sk).Where(d => !System.IO.Path.GetFileName(d).Contains(".bak_") && File.Exists(System.IO.Path.Combine(d, "SKILL.md"))))
                yield return new(KitKind.Skill, System.IO.Path.GetFileName(d), d, HashOf(d));
        foreach (var (kind, sub) in new[] { (KitKind.Command, "commands"), (KitKind.Agent, "agents") })
        {
            var dir = System.IO.Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.md").Where(f => !System.IO.Path.GetFileName(f).Contains(".bak_")))
                yield return new(kind, System.IO.Path.GetFileNameWithoutExtension(f), f, HashOf(f));
        }
    }

    public static string HashOf(string path)
    {
        using var sha = SHA256.Create();
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray() : [path];
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            var rel = Directory.Exists(path) ? System.IO.Path.GetRelativePath(path, f).Replace('\\', '/') : "";
            sb.Append(rel).Append('\n').Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)))).Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    // ------------------------------------------------------------------ privacy scan

    private static readonly (Regex Re, string Why)[] Patterns =
    [
        (new(@"(?<![\d.])(?!127\.0\.0\.1|0\.0\.0\.0)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\d.])"), "an IP address"),
        (new(@"\\\\[A-Za-z0-9._-]+\\[A-Za-z0-9$._-]+"), "a network share path"),
        (new(@"(?i)\b(password|passwd|pwd|secret|api[_-]?key|client[_-]?secret|token)\b\s*[:=]\s*[""']?[^\s""']{6,}"), "a password, secret or key"),
        (new(@"sk-ant-[A-Za-z0-9_-]{10,}|AKIA[0-9A-Z]{12,}|-----BEGIN [A-Z ]*PRIVATE KEY-----"), "a key or private key"),
        (new(@"(?i)\b(server|data source|sqlserver)\s*=\s*[^;\s""']{3,}"), "a SQL server name"),
    ];

    public static IReadOnlyList<string> Scan(string path)
    {
        var found = new List<string>();
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path];
        foreach (var f in files)
        {
            string text; try { text = File.ReadAllText(f); } catch (IOException) { continue; }
            foreach (var (re, why) in Patterns)
                if (re.IsMatch(text)) found.Add($"{System.IO.Path.GetFileName(f)}: {why}");
        }
        return found.Distinct().ToList();
    }

    // ------------------------------------------------------------------ publish (user)

    /// <summary>The user's own items: not shipped with the product and not identical to what the company already approved.</summary>
    public IReadOnlyList<KitItem> Candidates()
    {
        var approved = Discover(_shared).ToDictionary(i => (i.Kind, i.Name), i => i.Hash);
        return Discover(_claudeDir)
            .Where(i => !(i.Kind == KitKind.Skill && ProductSkills.Contains(i.Name, StringComparer.OrdinalIgnoreCase)))
            .Where(i => !(approved.TryGetValue((i.Kind, i.Name), out var h) && h == i.Hash))
            .ToList();
    }

    public KitPublishResult Publish(bool apply)
    {
        var published = new List<string>(); var unchanged = new List<string>(); var blocked = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var item in Candidates())
        {
            var label = $"{Folder(item.Kind)}/{item.Name}";
            var scan = Scan(item.Path);
            if (scan.Count > 0) { blocked[label] = scan; continue; }
            var dest = System.IO.Path.Combine(_shared, "contributions", _who, Folder(item.Kind), item.Kind == KitKind.Skill ? item.Name : item.Name + ".md");
            if ((Directory.Exists(dest) || File.Exists(dest)) && HashOf(dest) == item.Hash) { unchanged.Add(label); continue; }
            if (apply) CopyAny(item.Path, dest);
            published.Add(label);
        }
        return new(published, unchanged, blocked);
    }

    // ------------------------------------------------------------------ review (administrator)

    public IReadOnlyList<Contribution> Pending()
    {
        var root = System.IO.Path.Combine(_shared, "contributions");
        if (!Directory.Exists(root)) return [];
        var list = new List<Contribution>();
        foreach (var who in Directory.GetDirectories(root))
            foreach (var i in Discover(who, System.IO.Path.Combine(who, "skills")))
                list.Add(new(System.IO.Path.GetFileName(who), i.Kind, i.Name, i.Path));
        return list;
    }

    /// <summary>Moves one contribution into the approved folders (the replaced item is kept as .bak_&lt;date&gt;) and removes it from the inbox.</summary>
    public string Approve(string contributor, KitKind kind, string name)
    {
        var c = Pending().FirstOrDefault(x => x.Contributor.Equals(contributor, StringComparison.OrdinalIgnoreCase) && x.Kind == kind && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"No pending {kind} '{name}' from {contributor}.");
        var dest = System.IO.Path.Combine(_shared, Folder(kind), kind == KitKind.Skill ? c.Name : c.Name + ".md");
        if (Directory.Exists(dest) || File.Exists(dest))
        {
            var bak = dest + ".bak_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            for (var n = 2; Directory.Exists(bak) || File.Exists(bak); n++) bak = dest + ".bak_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + n;
            if (Directory.Exists(dest)) Directory.Move(dest, bak); else File.Move(dest, bak);
        }
        CopyAny(c.Path, dest);
        if (Directory.Exists(c.Path)) Directory.Delete(c.Path, true); else File.Delete(c.Path);
        return dest;
    }

    public void Reject(string contributor, KitKind kind, string name)
    {
        var c = Pending().FirstOrDefault(x => x.Contributor.Equals(contributor, StringComparison.OrdinalIgnoreCase) && x.Kind == kind && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"No pending {kind} '{name}' from {contributor}.");
        if (Directory.Exists(c.Path)) Directory.Delete(c.Path, true); else File.Delete(c.Path);
    }

    // ------------------------------------------------------------------ pull (everyone)

    /// <summary>Installs the approved items into this user's .claude folder. A local item the user changed is never overwritten.</summary>
    public KitPullResult Pull(bool apply = true)
    {
        var manifest = LoadManifest();
        var added = new List<string>(); var updated = new List<string>(); var kept = new List<string>();
        foreach (var item in Discover(_shared))
        {
            var key = $"{Folder(item.Kind)}/{item.Name}";
            var local = System.IO.Path.Combine(_claudeDir, Folder(item.Kind), item.Kind == KitKind.Skill ? item.Name : item.Name + ".md");
            var exists = Directory.Exists(local) || File.Exists(local);
            if (!exists)
            {
                if (apply) { CopyAny(item.Path, local); manifest[key] = item.Hash; }
                added.Add(key); continue;
            }
            var localHash = HashOf(local);
            if (localHash == item.Hash) { manifest[key] = item.Hash; continue; }
            // the company copy changed; replace the local one only if the user has not touched it since the last pull
            if (manifest.TryGetValue(key, out var last) && last == localHash)
            {
                if (apply) { CopyAny(item.Path, local, replace: true); manifest[key] = item.Hash; }
                updated.Add(key);
            }
            else kept.Add(key);
        }
        if (apply) SaveManifest(manifest);
        return new(added, updated, kept);
    }

    private Dictionary<string, string> LoadManifest()
    {
        try { return File.Exists(_manifestFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_manifestFile)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    private void SaveManifest(Dictionary<string, string> m)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_manifestFile)!);
        File.WriteAllText(_manifestFile, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyAny(string src, string dest, bool replace = false)
    {
        if (replace) { if (Directory.Exists(dest)) Directory.Delete(dest, true); else if (File.Exists(dest)) File.Delete(dest); }
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var t = System.IO.Path.Combine(dest, System.IO.Path.GetRelativePath(src, f));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(t)!);
                File.Copy(f, t, true);
            }
        }
        else { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!); File.Copy(src, dest, true); }
    }
}

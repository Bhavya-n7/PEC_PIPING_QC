using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Knowledge;

public sealed record Lesson(string Id, string Ts, string User, string Machine, string Kind, string Tool, string Text, string Context, string Outcome, string EmployeeCode = "", string Company = "");

/// <summary>
/// Self-learning store (same file formats as the prototype's learn.py / tools\learn_curate.py).
/// Clients append lessons to <c>&lt;shared&gt;\learning\inbox\yyyy-MM\user-machine.jsonl</c>; a curator approves them into
/// <c>learning\approved\lessons.jsonl</c> and <c>skill\LEARNED.md</c>. Lessons are text only; secrets are scrubbed. Nothing in the store is ever executed.
/// </summary>
public sealed class LessonStore(Settings settings)
{
    public static readonly string[] Kinds = ["correction", "success", "failure", "preference", "gotcha", "recipe"];
    private static readonly Regex Secret = new(@"(secret|password|passwd|pwd|token|api[_-]?key|client[_-]?secret|connection[_ -]?string|access[_-]?key)\s*[:=]\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // credentials that stand alone: bearer tokens, Anthropic / OpenAI style keys, AWS access keys, private key blocks, user:password@host
    private static readonly Regex Standalone = new(@"(Bearer\s+[A-Za-z0-9._~+/=-]{16,}|\bsk-[A-Za-z0-9_-]{16,}|\bAKIA[0-9A-Z]{16}\b|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)|://[^/\s:@]+:[^/\s@]+@)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string LearningRoot => settings.HasShared ? Path.Combine(settings.SharedRoot!, "learning") : Path.Combine(Settings.DataDirectory, "learning");

    public static string Scrub(string? text)
    {
        var t = Secret.Replace(text ?? "", m => m.Groups[1].Value + "=<redacted>");
        t = Standalone.Replace(t, "<redacted>");
        return t.Length > 4000 ? t[..4000] : t;
    }

    public (Lesson Lesson, string File, bool Central) Record(string kind, string text, string tool = "", string context = "", string outcome = "")
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("kind must be one of " + string.Join(", ", Kinds));
        var ts = DateTime.UtcNow.ToString("s") + "Z";
        var who = UserIdentity.Current(settings);
        var user = who.Name;
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(user + ts + text)))[..12].ToLowerInvariant();
        var l = new Lesson(id, ts, user, Environment.MachineName, kind, tool, Scrub(text), Scrub(context), Scrub(outcome), who.EmployeeCode, who.Company);
        var line = JsonSerializer.Serialize(l, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n";
        var name = $"{who.FileKey}-{Environment.MachineName}.jsonl";
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var file = Path.Combine(LearningRoot, "inbox", month, name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, line);
            Util.PathDiagnostics.Note("lesson", $"recorded {kind} {l.Id} ({(settings.HasShared ? "shared folder" : "this PC only")})", file);
            return (l, file, settings.HasShared);
        }
        catch (Exception e) when (settings.HasShared && e is IOException or UnauthorizedAccessException)
        {
            // the shared folder is unreachable: keep the lesson on this PC instead of losing it, and say so in the diagnostics
            Util.PathDiagnostics.Note("lesson", $"shared folder not writable, kept {l.Id} on this PC", file, e.Message);
            var local = Path.Combine(Settings.DataDirectory, "learning", "inbox", month, name);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.AppendAllText(local, line);
            Util.PathDiagnostics.Note("lesson", $"recorded {kind} {l.Id} (this PC only, shared was offline)", local);
            return (l, local, false);
        }
    }

    public IReadOnlyList<Lesson> Approved()
    {
        var list = new List<Lesson>();
        var p = Path.Combine(LearningRoot, "approved", "lessons.jsonl");
        if (!File.Exists(p)) return list;
        foreach (var line in File.ReadLines(p))
        {
            try { if (JsonSerializer.Deserialize<Lesson>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } l) list.Add(l); }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Everything every user has recorded (all machines, all months), newest first, without repeats. Lessons help the whole team the moment they are saved:
    /// they do not wait for a curator.</summary>
    public IReadOnlyList<Lesson> Inbox()
    {
        var rejected = Rejected();
        var seen = new HashSet<string>();
        return RawInbox().Where(l => !rejected.Contains(l.Id)).Where(l => seen.Add(NormalKey(l.Text))).ToList();
    }

    /// <summary>The ids the curator rejected: they are hidden from everyone.</summary>
    public HashSet<string> Rejected()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var p = Path.Combine(LearningRoot, "rejected.jsonl");
        if (!File.Exists(p)) return ids;
        foreach (var line in File.ReadLines(p))
        {
            try { using var d = JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } v) ids.Add(v); }
            catch (JsonException) { }
        }
        return ids;
    }

    /// <summary>Every lesson on the inbox (all machines, all months, this PC's offline copies too), newest first, repeats included.</summary>
    public IReadOnlyList<Lesson> RawInbox()
    {
        // the shared inbox, plus lessons kept on this PC while the shared folder was offline
        var roots = new[] { Path.Combine(LearningRoot, "inbox"), Path.Combine(Settings.DataDirectory, "learning", "inbox") }.Distinct().Where(Directory.Exists).ToList();
        var all = new List<Lesson>();
        foreach (var f in roots.SelectMany(r => Directory.GetFiles(r, "*.jsonl", SearchOption.AllDirectories)))
        {
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    try { if (JsonSerializer.Deserialize<Lesson>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } l && l.Text.Length > 0) all.Add(l); }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
        }
        return all.OrderByDescending(l => l.Ts).GroupBy(l => l.Id).Select(g => g.First()).ToList();
    }

    public static string NormalKey(string text) { var t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim(); return t.Length > 80 ? t[..80] : t; }

    /// <summary>Approved lessons plus the live inbox.</summary>
    public IReadOnlyList<Lesson> AllLessons() => Approved().Concat(Inbox()).GroupBy(l => l.Id).Select(g => g.First()).ToList();

    public IReadOnlyList<Lesson> Search(string query, int limit = 10)
    {
        var words = Regex.Matches((query ?? "").ToLowerInvariant(), @"\w+").Select(m => m.Value).Where(w => w.Length > 2).ToList();
        return AllLessons()
            .Select(l => (l, score: words.Sum(w => Count((l.Text + " " + l.Context + " " + l.Tool).ToLowerInvariant(), w))))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(limit).Select(x => x.l).ToList();
    }

    private static int Count(string hay, string w) { var n = 0; for (var i = hay.IndexOf(w, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(w, i + w.Length, StringComparison.Ordinal)) n++; return n; }

    /// <summary>The curated company-wide LEARNED.md (shared folder, else the copy that ships with the app), or empty.</summary>
    public string Pull(int latest = 30, int maxChars = 9000)
    {
        var curated = "";
        foreach (var p in new[]
                 {
                     settings.HasShared ? Path.Combine(settings.SharedRoot!, "skill", "LEARNED.md") : "",
                     Path.Combine(AppContext.BaseDirectory, "skills", "LEARNED.md"),
                 })
            if (p.Length > 0 && File.Exists(p)) { curated = File.ReadAllText(p); break; }

        // what colleagues learned in the last days: corrections, recipes, gotchas and failures help the next person at once
        var approvedIds = Approved().Select(l => l.Id).ToHashSet();
        var live = Inbox().Where(l => !approvedIds.Contains(l.Id)).Where(l => l.Kind is "correction" or "recipe" or "gotcha" or "failure" or "preference").Take(latest).ToList();
        if (live.Count == 0) return curated;
        var sb = new StringBuilder(curated.TrimEnd());
        if (sb.Length > 0) sb.AppendLine().AppendLine();
        sb.AppendLine("## Latest lessons from the team (NOT YET REVIEWED by the QC lead: use them as hints and say that they are unreviewed; a lesson that disagrees with a project document or the curated list above is not followed)");
        foreach (var l in live)
        {
            var text = l.Text.Length > 700 ? l.Text[..700] + "…" : l.Text;
            sb.AppendLine($"- [{l.Kind}{(l.Tool.Length > 0 ? ", " + l.Tool : "")}] {text}");
        }
        var all = sb.ToString();
        return all.Length > maxChars ? all[..maxChars] + "\n…" : all;
    }
}

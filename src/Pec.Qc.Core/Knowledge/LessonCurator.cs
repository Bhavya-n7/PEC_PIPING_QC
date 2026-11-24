using System.Text;
using System.Text.Json;

namespace Pec.Qc.Core.Knowledge;

/// <summary>A lesson waiting for the curator. Strong = several different people recorded the same thing (the most reliable signal that it is a real rule).</summary>
public sealed record PendingLesson(Lesson Lesson, int People, bool Strong);

public sealed record CurationResult(int Approved, int Rejected, IReadOnlyList<string> NotFound, string? LearnedFile);

/// <summary>
/// Curation of the team's lessons. Lessons are usable by everyone the moment they are recorded (learn_pull shows the latest), so the curator is not a gate; approving
/// a lesson makes it part of the permanent company list (<c>learning\approved\lessons.jsonl</c> and the generated <c>skill\LEARNED.md</c> that every PC reads at
/// the start), rejecting it removes it from everyone's view. The previous LEARNED.md is kept in <c>learning\history</c>. Who decided what is in <c>learning\decisions.jsonl</c>.
/// </summary>
public sealed class LessonCurator(Settings settings)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private string Root => settings.HasShared ? Path.Combine(settings.SharedRoot!, "learning") : Path.Combine(Settings.DataDirectory, "learning");
    private string LearnedFile => settings.HasShared ? Path.Combine(settings.SharedRoot!, "skill", "LEARNED.md") : Path.Combine(Settings.DataDirectory, "skill", "LEARNED.md");

    public IReadOnlyList<PendingLesson> Pending()
    {
        var store = new LessonStore(settings);
        var approved = store.Approved().Select(l => l.Id).ToHashSet();
        var raw = store.RawInbox();
        var people = raw.GroupBy(l => LessonStore.NormalKey(l.Text)).ToDictionary(g => g.Key, g => g.Select(l => l.EmployeeCode.Length > 0 ? l.EmployeeCode : l.User).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return store.Inbox().Where(l => !approved.Contains(l.Id)).Select(l =>
        {
            var n = people.GetValueOrDefault(LessonStore.NormalKey(l.Text), 1);
            return new PendingLesson(l, n, n >= 2);
        }).OrderByDescending(p => p.Strong).ThenByDescending(p => p.Lesson.Kind == "correction").ThenByDescending(p => p.Lesson.Ts).ToList();
    }

    public CurationResult Approve(IEnumerable<string> ids, string by)
    {
        var want = ids.Select(i => i.Trim()).Where(i => i.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = Pending().Where(p => want.Contains(p.Lesson.Id)).ToList();
        var notFound = want.Where(w => !pending.Any(p => p.Lesson.Id.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (pending.Count > 0)
        {
            var dir = Path.Combine(Root, "approved");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "lessons.jsonl"), string.Concat(pending.Select(p => JsonSerializer.Serialize(p.Lesson, Json) + "\n")));
            Decide(pending.Select(p => p.Lesson.Id), "approved", by);
        }
        return new CurationResult(pending.Count, 0, notFound, pending.Count > 0 ? RebuildLearned() : null);
    }

    public CurationResult Reject(IEnumerable<string> ids, string by, string reason = "")
    {
        var want = ids.Select(i => i.Trim()).Where(i => i.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var store = new LessonStore(settings);
        var known = store.RawInbox().Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = want.Where(known.Contains).ToList();
        if (hit.Count > 0)
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(Path.Combine(Root, "rejected.jsonl"), string.Concat(hit.Select(i => JsonSerializer.Serialize(new { id = i, by, ts = Now(), reason }, Json) + "\n")));
            Decide(hit, "rejected", by, reason);
        }
        return new CurationResult(0, hit.Count, want.Where(w => !hit.Contains(w)).ToList(), null);
    }

    private static string Now() => DateTime.UtcNow.ToString("s") + "Z";

    private void Decide(IEnumerable<string> ids, string what, string by, string reason = "")
    {
        Directory.CreateDirectory(Root);
        File.AppendAllText(Path.Combine(Root, "decisions.jsonl"), string.Concat(ids.Select(i => JsonSerializer.Serialize(new { id = i, decision = what, by, ts = Now(), reason }, Json) + "\n")));
    }

    /// <summary>Writes LEARNED.md from the approved lessons (newest first within each kind). The old file is kept in learning\history first.</summary>
    public string RebuildLearned()
    {
        var approved = new LessonStore(settings).Approved();
        var sb = new StringBuilder();
        sb.AppendLine("# What the QC team has learned").AppendLine();
        sb.AppendLine("Generated from the lessons the QC lead approved. Apply these before generic advice; where one disagrees with a project document, show both and let the QC lead decide.").AppendLine();
        foreach (var kind in new[] { "correction", "gotcha", "recipe", "preference", "failure", "success" })
        {
            var items = approved.Where(l => l.Kind == kind).OrderByDescending(l => l.Ts).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine($"## {char.ToUpperInvariant(kind[0])}{kind[1..]}s").AppendLine();
            foreach (var l in items) sb.AppendLine($"- {l.Text.Replace("\n", " ").Trim()}" + (l.Context.Length > 0 ? $" ({l.Context.Replace("\n", " ").Trim()})" : "") + (l.Tool.Length > 0 ? $" [{l.Tool}]" : ""));
            sb.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(LearnedFile)!);
        if (File.Exists(LearnedFile))
        {
            var hist = Path.Combine(Root, "history");
            Directory.CreateDirectory(hist);
            File.Copy(LearnedFile, Path.Combine(hist, $"LEARNED-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md"), true);
        }
        File.WriteAllText(LearnedFile, sb.ToString());
        return LearnedFile;
    }
}

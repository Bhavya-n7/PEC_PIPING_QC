using System.Text;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Knowledge;

public sealed record KnowledgeEntry(string Id, string Title, string Category, string Source, string Client, DateTime Saved, string Text);

/// <summary>
/// What the team teaches the assistant: corrections, client rules, option meanings. Plain Markdown files with a small header,
/// in the knowledge folder (local, or a shared drive). It holds data only; it never changes program code.
/// </summary>
public sealed class KnowledgeStore
{
    public static readonly string[] Categories = ["client-rule", "correction", "option-meaning", "how-to", "context", "decision", "lesson", "mistake", "skill-proposal", "note"];
    private readonly string _dir;
    private readonly string _ctx;

    /// <param name="directory">Learning folder (lessons, decisions, client rules...).</param>
    /// <param name="contextDirectory">Folder for entries of category "context"; same folder when not given.</param>
    public KnowledgeStore(string? directory = null, string? contextDirectory = null)
    {
        var s = directory is null ? Settings.Load() : null;
        _dir = directory ?? s!.EffectiveKnowledgeDirectory;
        _ctx = contextDirectory ?? (s?.EffectiveContextDirectory ?? _dir);
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_ctx);
    }

    public static KnowledgeStore From(Settings s) => new(s.EffectiveKnowledgeDirectory, s.EffectiveContextDirectory);

    private string DirFor(string category) => category == "context" ? _ctx : _dir;
    private IEnumerable<string> Dirs => _ctx.Equals(_dir, StringComparison.OrdinalIgnoreCase) ? [_dir] : [_dir, _ctx];

    public string Directory_ => _dir;

    public static string IdFor(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? (s.Length > 60 ? s[..60].TrimEnd('-') : s) : "entry";

    /// <summary>Save (or update, when the title already exists) one entry. <paramref name="source"/> says where it comes from (document and page, or who said it).</summary>
    public KnowledgeEntry Save(string title, string text, string category, string source, string client = "")
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(text)) throw new ArgumentException("A title and a text are needed.");
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Say where this comes from (document and page, or who told you). Unsourced rules are not stored.");
        if (!Categories.Contains(category)) throw new ArgumentException($"Category must be one of: {string.Join(", ", Categories)}.");
        var e = new KnowledgeEntry(IdFor(title), title.Trim(), category, source.Trim(), client.Trim(), DateTime.Now, text.Trim());
        var sb = new StringBuilder()
            .AppendLine("---").AppendLine($"title: {Line(e.Title)}").AppendLine($"category: {e.Category}")
            .AppendLine($"source: {Line(e.Source)}").AppendLine($"client: {Line(e.Client)}").AppendLine($"saved: {e.Saved:yyyy-MM-dd HH:mm}")
            .AppendLine("---").AppendLine().AppendLine(e.Text);
        File.WriteAllText(Path.Combine(DirFor(e.Category), e.Id + ".md"), sb.ToString());
        // a changed category must not leave the old file behind in the other folder
        foreach (var d in Dirs.Where(d => !d.Equals(DirFor(e.Category), StringComparison.OrdinalIgnoreCase)))
            if (File.Exists(Path.Combine(d, e.Id + ".md"))) File.Delete(Path.Combine(d, e.Id + ".md"));
        return e;
    }

    public bool Delete(string id)
    {
        var f = Dirs.Select(d => Path.Combine(d, Path.GetFileName(id) + ".md")).FirstOrDefault(File.Exists);
        if (f is null) return false;
        File.Delete(f);
        return true;
    }

    public KnowledgeEntry? Get(string id) => All().FirstOrDefault(e => e.Id == id);

    public List<KnowledgeEntry> All() =>
        Dirs.SelectMany(d => Directory.GetFiles(d, "*.md")).Select(Read).OfType<KnowledgeEntry>().OrderBy(e => e.Category).ThenBy(e => e.Title).ToList();

    public List<(KnowledgeEntry Entry, double Score)> Recall(string query, int max = 5)
    {
        var all = All();
        var b = new Bm25<KnowledgeEntry>();
        foreach (var e in all) b.Add(e, $"{e.Title} {e.Title} {e.Category} {e.Client} {e.Text}");
        return b.Search(query, max);
    }

    private static string Line(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private static KnowledgeEntry? Read(string file)
    {
        var text = File.ReadAllText(file);
        var m = Regex.Match(text, @"^---\r?\n(?<h>.*?)\r?\n---\r?\n(?<b>.*)$", RegexOptions.Singleline);
        if (!m.Success) return null;
        string H(string k) => Regex.Match(m.Groups["h"].Value, $@"^{k}:\s*(.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        DateTime.TryParse(H("saved"), out var saved);
        return new KnowledgeEntry(Path.GetFileNameWithoutExtension(file), H("title"), H("category"), H("source"), H("client"), saved, m.Groups["b"].Value.Trim());
    }
}

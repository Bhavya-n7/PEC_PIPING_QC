using System.Text.Json;

namespace Pec.Qc.Core.Claude;

public sealed class StoredMessage
{
    public string Role { get; set; } = "";          // user | assistant | system
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

public sealed class StoredChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "New chat";
    public bool TitleChosenByUser { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>The Claude Code session this chat continues (--resume); null until the first answer.</summary>
    public string? ClaudeSessionId { get; set; }
    public string? Project { get; set; }
    public List<StoredMessage> Messages { get; set; } = [];
}

public sealed record ChatSummary(string Id, string Title, DateTime Updated, int Messages);

/// <summary>
/// The user's chat history: one small JSON file per chat in the assistant's data folder (private to this PC and Windows user, never put on the shared folder).
/// A chat is saved after every message, so closing the app never loses a conversation, and an old one can be opened and continued.
/// </summary>
public sealed class ChatHistory
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public ChatHistory(string? directory = null) => _dir = directory ?? Path.Combine(Settings.DataDirectory, "chats");

    public string Directory_ => _dir;
    private string FileFor(string id) => Path.Combine(_dir, SafeId(id) + ".json");
    private static string SafeId(string id) => new(id.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The title a chat gets from its first message: the first line, tidied and cut at a word.</summary>
    public static string TitleFrom(string text, int max = 48)
    {
        var line = (text ?? "").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return "New chat";
        if (line.Length <= max) return line;
        var cut = line[..max]; var sp = cut.LastIndexOf(' ');
        return (sp > max / 2 ? cut[..sp] : cut).TrimEnd(' ', ',', '.', ';', ':') + "…";
    }

    public void Save(StoredChat chat)
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var file = FileFor(chat.Id); var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(chat, Json));
            File.Move(tmp, file, true);                       // a crash never leaves half a chat behind
        }
    }

    public StoredChat? Load(string id)
    {
        try { var f = FileFor(id); return File.Exists(f) ? JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(f)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public void Delete(string id) { lock (Gate) { try { var f = FileFor(id); if (File.Exists(f)) File.Delete(f); } catch (IOException) { } } }

    public void Rename(string id, string title)
    {
        var c = Load(id); if (c is null || string.IsNullOrWhiteSpace(title)) return;
        c.Title = title.Trim(); c.TitleChosenByUser = true; Save(c);
    }

    /// <summary>All chats, newest activity first. A damaged file is skipped, not fatal.</summary>
    public IReadOnlyList<ChatSummary> List()
    {
        if (!System.IO.Directory.Exists(_dir)) return [];
        var list = new List<ChatSummary>();
        foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json"))
        {
            try { var c = JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(f)); if (c is not null) list.Add(new(c.Id, c.Title, c.Updated, c.Messages.Count)); }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return list.OrderByDescending(s => s.Updated).ToList();
    }

    /// <summary>Chats whose title or text contains the query (every word of it).</summary>
    public IReadOnlyList<ChatSummary> Search(string query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return List();
        var hits = new List<ChatSummary>();
        foreach (var s in List())
        {
            var c = Load(s.Id); if (c is null) continue;
            var hay = c.Title + "\n" + string.Join("\n", c.Messages.Select(m => m.Text));
            if (words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) hits.Add(s);
        }
        return hits;
    }

    /// <summary>"Today", "Yesterday", "Previous 7 days" or "Older": the headings of the history list.</summary>
    public static string Group(DateTime when, DateTime now)
    {
        var d = (now.Date - when.Date).Days;
        return d <= 0 ? "Today" : d == 1 ? "Yesterday" : d <= 7 ? "Previous 7 days" : "Older";
    }

    /// <summary>Removes the oldest chats beyond a limit (the user's own pinned work is not at risk: 300 chats is far more than anyone keeps).</summary>
    public int Prune(int keep = 300)
    {
        var all = List(); var n = 0;
        foreach (var s in all.Skip(keep)) { Delete(s.Id); n++; }
        return n;
    }

    /// <summary>The last messages as plain text, to give Claude the context back when its own session is gone.</summary>
    public static string ContextFor(StoredChat chat, int messages = 12, int maxChars = 6000)
    {
        var take = chat.Messages.Where(m => m.Role is "user" or "assistant").TakeLast(messages).ToList();
        if (take.Count == 0) return "";
        var sb = new System.Text.StringBuilder("[Earlier in this chat (the previous session could not be resumed):]\n");
        foreach (var m in take) sb.Append(m.Role == "user" ? "User: " : "Assistant: ").AppendLine(m.Text.Length > 1200 ? m.Text[..1200] + "…" : m.Text);
        var s = sb.ToString();
        return (s.Length > maxChars ? s[^maxChars..] : s) + "[End of earlier messages]\n\n";
    }
}

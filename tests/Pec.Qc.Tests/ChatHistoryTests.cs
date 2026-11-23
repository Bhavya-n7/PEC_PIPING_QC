using Pec.Qc.Core.Claude;

namespace Pec.Qc.Tests;

public class ChatHistoryTests
{
    private static StoredChat Chat(string title, params (string Role, string Text)[] msgs) =>
        new() { Title = title, Messages = msgs.Select(m => new StoredMessage { Role = m.Role, Text = m.Text }).ToList() };

    [Fact] public void A_chat_is_saved_loaded_and_survives_a_restart()
    {
        using var t = new TempDir(); var h = new ChatHistory(t.Path);
        var c = Chat("Pump P-102", ("user", "build a pump"), ("assistant", "done")); c.ClaudeSessionId = "abc-123"; c.Site = "Test server";
        h.Save(c);
        var again = new ChatHistory(t.Path).Load(c.Id)!;                       // a new instance = the app restarted
        Assert.Equal("Pump P-102", again.Title); Assert.Equal("abc-123", again.ClaudeSessionId); Assert.Equal(2, again.Messages.Count);
        Assert.Equal("assistant", again.Messages[1].Role);
    }

    [Fact] public void The_list_is_newest_first_and_a_damaged_file_is_skipped()
    {
        using var t = new TempDir(); var h = new ChatHistory(t.Path);
        var old = Chat("old", ("user", "a")); old.Updated = DateTime.Now.AddDays(-3); h.Save(old);
        var fresh = Chat("fresh", ("user", "b")); h.Save(fresh);
        File.WriteAllText(System.IO.Path.Combine(t.Path, "broken.json"), "{ not json");
        Assert.Equal(["fresh", "old"], h.List().Select(s => s.Title));
    }

    [Fact] public void Rename_marks_the_title_as_chosen_and_delete_removes_the_chat()
    {
        using var t = new TempDir(); var h = new ChatHistory(t.Path);
        var c = Chat("first words", ("user", "x")); h.Save(c);
        h.Rename(c.Id, "  Tank farm review "); var r = h.Load(c.Id)!;
        Assert.Equal("Tank farm review", r.Title); Assert.True(r.TitleChosenByUser);
        h.Rename(c.Id, "   ");                                                  // an empty name changes nothing
        Assert.Equal("Tank farm review", h.Load(c.Id)!.Title);
        h.Delete(c.Id); Assert.Null(h.Load(c.Id)); Assert.Empty(h.List());
    }

    [Fact] public void Search_finds_words_in_titles_and_in_the_conversation()
    {
        using var t = new TempDir(); var h = new ChatHistory(t.Path);
        h.Save(Chat("Pump P-102", ("user", "model the motor fins"), ("assistant", "built 31 parts")));
        h.Save(Chat("Catalog audit", ("user", "check the specs")));
        Assert.Equal(["Pump P-102"], h.Search("MOTOR fins").Select(s => s.Title));          // any case, every word
        Assert.Equal(["Catalog audit"], h.Search("catalog").Select(s => s.Title));
        Assert.Empty(h.Search("zebra"));
        Assert.Equal(2, h.Search("").Count);
    }

    [Theory]
    [InlineData("Build 2 equipment and 1 pipe in system U11", "Build 2 equipment and 1 pipe in system U11")]
    [InlineData("", "New chat")] [InlineData("  \n\n ", "New chat")]
    [InlineData("first line\nsecond line", "first line")]
    public void The_title_comes_from_the_first_line(string text, string expected) => Assert.Equal(expected, ChatHistory.TitleFrom(text));

    [Fact] public void A_long_first_message_is_cut_at_a_word()
    {
        var t = ChatHistory.TitleFrom("Please check the whole piping catalog for missing parts and report every single one of them");
        Assert.True(t.Length <= 49); Assert.EndsWith("…", t); Assert.DoesNotContain("  ", t);
        Assert.StartsWith("Please check the whole piping catalog", t);
    }

    [Fact] public void History_headings_follow_the_date()
    {
        var now = new DateTime(2026, 11, 21, 15, 0, 0);
        Assert.Equal("Today", ChatHistory.Group(now.AddHours(-3), now));
        Assert.Equal("Yesterday", ChatHistory.Group(now.AddDays(-1), now));
        Assert.Equal("Previous 7 days", ChatHistory.Group(now.AddDays(-5), now));
        Assert.Equal("Older", ChatHistory.Group(now.AddDays(-30), now));
    }

    [Fact] public void Context_for_a_lost_session_has_the_last_messages_and_skips_notices()
    {
        var c = Chat("x", ("user", "one"), ("system", "Approved: build"), ("assistant", "two"), ("user", "three"));
        var ctx = ChatHistory.ContextFor(c);
        Assert.Contains("User: one", ctx); Assert.Contains("Assistant: two", ctx); Assert.DoesNotContain("Approved", ctx);
        Assert.Equal("", ChatHistory.ContextFor(Chat("empty")));
    }

    [Fact] public void Prune_keeps_the_newest()
    {
        using var t = new TempDir(); var h = new ChatHistory(t.Path);
        for (var i = 0; i < 5; i++) { var c = Chat("c" + i, ("user", "m")); c.Updated = DateTime.Now.AddMinutes(-i); h.Save(c); }
        Assert.Equal(2, h.Prune(3));
        Assert.Equal(["c0", "c1", "c2"], h.List().Select(s => s.Title));
    }
}

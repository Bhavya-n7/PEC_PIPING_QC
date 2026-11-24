using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Tests;

public class ActivityAndIdentityTests
{
    private static Settings At(string root) => new() { SharedRoot = root };
    private static readonly string[] Vars = [UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar];

    [Fact] public void Identity_comes_from_the_environment_then_the_settings_then_the_windows_user()
    {
        var s = new Settings { UserName = "Anita Rao", EmployeeCode = "E1234", Company = "Petrocon" };
        var prev = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
            var w = UserIdentity.Current(s);
            Assert.Equal("Anita Rao (E1234)", w.Display); Assert.True(w.IsComplete); Assert.Equal("E1234", w.FileKey);
            Environment.SetEnvironmentVariable(UserIdentity.CodeVar, "E9999");
            Assert.Equal("E9999", UserIdentity.Current(s).EmployeeCode);
            Environment.SetEnvironmentVariable(UserIdentity.CodeVar, null);
            var none = UserIdentity.Current(new Settings());
            Assert.Equal(Environment.UserName, none.Name); Assert.False(none.IsComplete);              // no code yet: the app asks
        }
        finally { for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], prev[i]); }
    }

    [Fact] public void The_file_key_never_contains_odd_characters()
    {
        Assert.Equal("E123", new UserIdentity("A", "E1*2?3", "C").FileKey);
        Assert.Equal("E-12_3", new UserIdentity("A", "E-12_3", "C").FileKey);
    }

    [Fact] public void Activity_is_written_to_the_shared_folder_with_who_asked_and_can_be_read_back_per_person()
    {
        using var t = new TempDir(); var s = At(t.Path);
        var ann = new UserIdentity("Anita Rao", "E1234", "Petrocon"); var bob = new UserIdentity("Bob Das", "E2222", "Petrocon");
        ActivityLog.Write(s, ann, "prompt", "Create a slab next to SLAB-01", chatId: "c1", project: "Test project", detail: "looks like a correction");
        ActivityLog.Write(s, ann, "tool", "model_run_csharp", "model_run_csharp", chatId: "c1", detail: "{\"write\":true}");
        ActivityLog.Write(s, ann, "result", "Sequence contains no matching element", "model_run_csharp", ok: false, chatId: "c1");
        ActivityLog.Write(s, bob, "prompt", "Model health check", chatId: "c2");
        var file = Directory.GetFiles(System.IO.Path.Combine(t.Path, "activity"), "E1234-*.jsonl", SearchOption.AllDirectories).Single();
        Assert.Contains("Anita Rao", File.ReadAllText(file));

        var day = DateTime.UtcNow.AddDays(-1);
        Assert.Equal(4, ActivityLog.Read(s, day).Count);
        Assert.Equal(3, ActivityLog.Read(s, day, who: "E1234").Count);
        Assert.Single(ActivityLog.Read(s, day, who: "bob"));
        Assert.Single(ActivityLog.Read(s, day, type: "result"));
        Assert.Single(ActivityLog.Read(s, day, contains: "slab"));
        Assert.Empty(ActivityLog.Read(s, DateTime.UtcNow.AddDays(1)));
    }

    [Fact] public void Secrets_are_scrubbed_and_long_texts_are_cut_in_the_log()
    {
        using var t = new TempDir(); var s = At(t.Path);
        ActivityLog.Write(s, new UserIdentity("A", "E1", "C"), "prompt", "connect with password=Hunter2xyz please " + new string('x', 9000));
        var row = ActivityLog.Read(s, DateTime.UtcNow.AddDays(-1)).Single();
        Assert.DoesNotContain("Hunter2xyz", row.Text); Assert.Contains("<redacted>", row.Text);
        Assert.True(row.Text.Length <= 4001);
    }

    [Theory]
    [InlineData("That is wrong, the tank should be a cylinder", true)]
    [InlineData("this is not accurate", true)]
    [InlineData("it does not match the drawing", true)]
    [InlineData("you missed the nozzles", true)]
    [InlineData("try again", true)]
    [InlineData("Create a pump P-101 here", false)]
    [InlineData("Run the model health check", false)]
    public void Messages_that_say_it_was_wrong_are_recognised(string text, bool expected) => Assert.Equal(expected, Corrections.LooksLikeCorrection(text));

    [Fact] public void A_lesson_carries_who_recorded_it()
    {
        using var t = new TempDir();
        var s = new Settings { SharedRoot = t.Path, UserName = "Anita Rao", EmployeeCode = "E1234", Company = "Petrocon" };
        var prev = Vars.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
            var (l, file, _) = new Pec.Qc.Core.Knowledge.LessonStore(s).Record("gotcha", "slabs need a plane");
            Assert.Equal("E1234", l.EmployeeCode); Assert.Equal("Petrocon", l.Company); Assert.Contains("E1234-", System.IO.Path.GetFileName(file));
        }
        finally { for (var i = 0; i < Vars.Length; i++) Environment.SetEnvironmentVariable(Vars[i], prev[i]); }
    }
}

public class AutonomyArgumentTests
{
    [Fact] public void Careful_is_the_old_behaviour_and_nothing_else_is_allowed()
    {
        var a = ClaudeCli.ChatArguments("m.json", "SYS", null, readableFolder: @"C:\w\attachments");
        Assert.Equal("Read", a[a.IndexOf("--tools") + 1]); Assert.DoesNotContain("Bash", a); Assert.DoesNotContain("--disallowedTools", a);
    }

    [Fact] public void Standard_adds_files_and_the_web_but_no_commands_and_writes_only_in_the_workspace()
    {
        var a = ClaudeCli.ChatArguments("m.json", "SYS", null, autonomy: ClaudeAutonomy.Standard, workspace: @"C:\w");
        var tools = a[a.IndexOf("--tools") + 1];
        Assert.Contains("WebSearch", tools); Assert.Contains("Write", tools); Assert.DoesNotContain("Bash", tools);
        Assert.Contains("Write(//C/w/**)", a); Assert.Contains("Edit(//C/w/**)", a);
    }

    [Fact] public void Full_adds_commands_and_refuses_only_what_wipes_or_shuts_down_the_pc()
    {
        var a = ClaudeCli.ChatArguments("m.json", "SYS", null, autonomy: ClaudeAutonomy.Full, workspace: @"C:\w");
        Assert.Contains("Bash", a[a.IndexOf("--tools") + 1]); Assert.Contains("Bash", a); Assert.Contains("WebFetch", a);
        var i = a.IndexOf("--disallowedTools");
        Assert.True(i > 0);
        var refused = a.Skip(i + 1).TakeWhile(x => !x.StartsWith("--")).ToList();
        Assert.Equal(["Bash(format:*)", "Bash(diskpart:*)", "Bash(mkfs:*)", "Bash(shutdown:*)"], refused);
        Assert.Contains(a, x => x.StartsWith("mcp__"));
    }

    [Fact] public void The_setting_maps_to_the_level_and_full_is_the_default()
    {
        Assert.Equal(ClaudeAutonomy.Full, new Settings().EffectiveAutonomy);
        Assert.Equal(ClaudeAutonomy.Standard, new Settings { Autonomy = "Standard" }.EffectiveAutonomy);
        Assert.Equal(ClaudeAutonomy.Careful, new Settings { Autonomy = "careful" }.EffectiveAutonomy);
        Assert.Equal(ClaudeAutonomy.Full, new Settings { Autonomy = "nonsense" }.EffectiveAutonomy);
    }
}

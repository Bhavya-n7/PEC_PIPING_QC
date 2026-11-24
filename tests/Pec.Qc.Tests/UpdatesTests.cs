using System.Text.RegularExpressions;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Tests;

public class UpdatesTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.10.0", "1.9.9", true)]       // numbers, not text
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    public void Versions_compare_as_numbers(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(candidate, current));

    [Fact]
    public async Task A_newer_release_on_a_server_folder_is_found_with_its_setup_location()
    {
        using var t = new TempDir();
        t.File("share/latest.json", Updates.ReleaseJson("1.2.0", "Faster imports.\nNew quick tasks."));
        var r = await Updates.CheckAsync(System.IO.Path.Combine(t.Path, "share"), "1.0.0");
        Assert.True(r.Available);
        Assert.Equal("1.2.0", r.Latest!.Version);
        Assert.Contains("Faster imports", r.Latest.Notes);
        Assert.EndsWith("PecQcSetup.exe", r.SetupLocation);
        Assert.StartsWith(System.IO.Path.Combine(t.Path, "share"), r.SetupLocation);
    }

    [Fact]
    public async Task No_update_when_current_missing_file_or_bad_file()
    {
        using var t = new TempDir();
        t.File("share/latest.json", Updates.ReleaseJson("1.0.0", ""));
        Assert.False((await Updates.CheckAsync(System.IO.Path.Combine(t.Path, "share"), "1.0.0")).Available);
        Assert.Null((await Updates.CheckAsync("", "1.0.0")).Error);                        // no source configured: silent
        var missing = await Updates.CheckAsync(System.IO.Path.Combine(t.Path, "nothing"), "1.0.0");
        Assert.False(missing.Available);
        t.File("bad/latest.json", "{ not json");
        var bad = await Updates.CheckAsync(System.IO.Path.Combine(t.Path, "bad"), "1.0.0");
        Assert.False(bad.Available);
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void The_guide_covers_setup_updates_and_the_faq()
    {
        var file = TestPaths.FindUp("help/help.md");
        Assert.NotEqual("", file);
        var text = File.ReadAllText(file);
        var headings = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        foreach (var must in new[] { "Setup", "Projects", "Quick tasks", "Approvals", "Knowledge", "Updates", "FAQ", "Troubleshooting" })
            Assert.Contains(headings, h => h.Contains(must, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Sign in with Claude", text);
        Assert.DoesNotContain("API key is needed", text.Replace("No API key is needed", ""));      // we never ask for one
        // the three QC types and the read-only promise are what the product stands for
        foreach (var must in new[] { "isometric", "P&ID", "GA drawing", "read-only", "REVIEW" })
            Assert.Contains(must, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Smart 3D", text);      // nothing left from the product this one was built from
    }

    [Theory]
    [InlineData("src/Pec.Qc.Desktop/MainWindow.xaml.cs")]
    [InlineData("src/Pec.Qc.Desktop/MainWindow.Chats.cs")]
    [InlineData("src/Pec.Qc.Setup/SetupWindow.xaml.cs")]
    public void Background_work_never_reads_or_writes_a_wpf_control(string file)
    {
        // WPF controls belong to the UI thread: inside Task.Run(...) only plain values captured beforehand may be used
        var text = File.ReadAllText(TestPaths.FindUp(file));
        var i = 0;
        while ((i = text.IndexOf("Task.Run(", i, StringComparison.Ordinal)) >= 0)
        {
            var depth = 0; var j = text.IndexOf('(', i);
            for (var k = j; k < text.Length; k++)
            {
                if (text[k] == '(') depth++;
                else if (text[k] == ')' && --depth == 0) { j = k; break; }
            }
            var body = text[i..j];
            Assert.DoesNotMatch(ControlAccess, body.Replace("Dispatcher.Invoke", ""));
            i = j;
        }
    }

    [Fact]
    public void The_desktop_and_setup_projects_take_their_version_from_the_one_place()
    {
        foreach (var proj in new[] { "Desktop", "Setup" })
            Assert.DoesNotContain("<Version>", File.ReadAllText(TestPaths.FindUp($"src/Pec.Qc.{proj}/Pec.Qc.{proj}.csproj")));
    }

    [Fact]
    public void The_release_notes_for_the_current_version_exist_in_the_changelog()
    {
        var v = Regex.Match(File.ReadAllText(TestPaths.FindUp("Directory.Build.props")), @"<Version>([^<]+)</Version>").Groups[1].Value;
        var log = File.ReadAllText(TestPaths.FindUp("CHANGELOG.md"));
        // the same pattern build-installer.ps1 uses to cut the notes for latest.json
        var m = Regex.Match(log, @"(?s)## " + Regex.Escape(v) + @"[^\r\n]*\r?\n(.*?)(\r?\n## |\z)");
        Assert.True(m.Success, $"CHANGELOG.md has no '## {v}' section: the update notes would be empty");
        Assert.True(m.Groups[1].Value.Trim().Length > 20);
    }

    [Fact]
    public void The_release_json_is_what_the_update_check_reads()
    {
        var json = Updates.ReleaseJson("1.2.3", "notes");
        using var t = new TempDir();
        t.File("latest.json", json);
        var check = Updates.CheckAsync(t.Path, "1.2.2").GetAwaiter().GetResult();
        Assert.True(check.Available);
        Assert.Equal("1.2.3", check.Latest!.Version);
        Assert.EndsWith("PecQcSetup.exe", check.SetupLocation);
        Assert.False(Updates.CheckAsync(t.Path, "1.2.3").GetAwaiter().GetResult().Available);
    }

    [Fact]
    public void No_text_in_the_desktop_app_still_talks_about_the_product_it_was_built_from()
    {
        foreach (var proj in new[] { "Desktop", "Setup" })
        {
            var dir = Path.GetDirectoryName(TestPaths.FindUp($"src/Pec.Qc.{proj}/Pec.Qc.{proj}.csproj"))!;
            foreach (var f in Directory.GetFiles(dir, "*.*").Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")))
                Assert.DoesNotContain("Smart 3D", File.ReadAllText(f));
        }
    }
    // Phase 4 adds the same thread guard for SetupWindow. They are added with the projects they guard, not skipped silently here.

    [Fact]
    public void Product_version_comes_from_one_place_and_is_a_real_version()
    {
        var props = File.ReadAllText(TestPaths.FindUp("Directory.Build.props"));
        var v = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", v);
        foreach (var proj in new[] { "Core", "Mcp" })
            Assert.DoesNotContain("<Version>", File.ReadAllText(TestPaths.FindUp($"src/Pec.Qc.{proj}/Pec.Qc.{proj}.csproj")));
    }

    private static readonly string ControlAccess = @"\b(Opt\w+|\w*(Box|Button|Text|Panel|Bar|Status|List|Combo))\.(Text|IsChecked|Content|Visibility|IsEnabled|SelectedItem|SelectedIndex|ItemsSource)\b";

    [Fact]
    public void The_control_access_pattern_catches_the_bug_it_guards_against()
    {
        Assert.Matches(ControlAccess, "if (OptInstallClaude.IsChecked == true && ClaudeCli.Find() is null) inst.InstallClaudeCode(out _);");
        Assert.Matches(ControlAccess, "var f = SkillsBox.Text.Trim();");
        Assert.DoesNotMatch(ControlAccess, "if (installClaude && ClaudeCli.Find() is null) inst.InstallClaudeCode(out _);");
    }

}

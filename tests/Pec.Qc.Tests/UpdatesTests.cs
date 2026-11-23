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

    // Phase 3 adds: the in-app guide covers setup, updates and the FAQ (help/help.md), and the WPF thread guards for MainWindow.
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

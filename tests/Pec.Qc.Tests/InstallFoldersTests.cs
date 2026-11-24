using Pec.Qc.Core;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Tests;

[Collection("EnvVar")]
public class FolderDefaultsTests
{
    [Fact]
    public void With_a_shared_folder_everything_lives_inside_it()
    {
        var f = FolderDefaults.For(@"\\server\share\PIPEING_QC");
        Assert.Equal(@"\\server\share\PIPEING_QC\skills", f.Skills);
        Assert.Equal(@"\\server\share\PIPEING_QC\learning", f.Learning);
        Assert.Equal(@"\\server\share\PIPEING_QC\documents", f.Documents);
        Assert.Equal(@"\\server\share\PIPEING_QC", f.Shared);
    }

    [Fact]
    public void Without_a_shared_folder_the_defaults_are_local()
    {
        var f = FolderDefaults.For("  ");
        Assert.Null(f.Shared);
        Assert.StartsWith(Settings.DataDirectory, f.Skills);
    }

    [Fact]
    public void A_box_follows_the_shared_folder_only_while_the_user_has_not_edited_it()
    {
        Assert.Equal("new", FolderDefaults.Follow("old", "old", "new"));
        Assert.Equal("typed", FolderDefaults.Follow("typed", "old", "new"));
    }
}

[Collection("EnvVar")]
public class SettingsFolderTests
{
    [Fact]
    public void The_QC_folders_sit_under_the_shared_root()
    {
        var s = new Settings { SharedRoot = @"\\server\share\PIPEING_QC" };
        Assert.Equal(@"\\server\share\PIPEING_QC\projects", s.EffectiveProjectsDirectory);
        Assert.Equal(@"\\server\share\PIPEING_QC\models", s.EffectiveModelsDirectory);
        Assert.Equal(@"\\server\share\PIPEING_QC\reports", s.EffectiveReportsDirectory);
        Assert.Equal(@"\\server\share\PIPEING_QC\users", s.EffectiveUsersDirectory);
        Assert.Equal(@"\\server\share\PIPEING_QC\requirements", s.EffectiveRequirementsDirectory);
        Assert.Equal(@"\\server\share\PIPEING_QC\updates", s.EffectiveUpdateSource);
    }

    [Fact]
    public void Without_a_shared_root_there_is_no_projects_folder_and_the_rest_is_local()
    {
        var s = new Settings();
        Assert.Null(s.EffectiveProjectsDirectory);
        Assert.StartsWith(Settings.DataDirectory, s.EffectiveReportsDirectory);
        Assert.Null(s.EffectiveUpdateSource);
    }
}

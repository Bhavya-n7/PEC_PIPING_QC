using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Tests;

public class ProjectCardTests
{
    [Fact]
    public void The_prompt_block_only_contains_filled_fields()
    {
        var p = new ProjectCard { Name = "Plant A piping", Client = "ACME", ModelFile = "model.nwd" };
        var text = p.ToPromptBlock();
        Assert.Contains("Project: Plant A piping", text);
        Assert.Contains("Client: ACME", text);
        Assert.Contains("Navisworks model: model.nwd", text);
        Assert.DoesNotContain("Project inputs folder", text);
        Assert.DoesNotContain("Project no.", text);
    }

    [Fact]
    public void Quick_task_fields_come_from_the_project_and_empty_ones_read_naturally()
    {
        var filled = QuickTasks.Fill("Check {{?Path of the isometric PDF}} for {{project}} ({{client}})", new ProjectCard { Name = "P1", Client = "C1" },
            new Dictionary<string, string> { ["Path of the isometric PDF"] = "x.pdf" });
        Assert.Equal("Check x.pdf for P1 (C1)", filled);
        var empty = QuickTasks.Fill("For {{project}}", null);
        Assert.Equal("For the active project", empty);
    }

    [Fact]
    public void The_default_quick_tasks_cover_the_three_QC_types()
    {
        var cats = QuickTasks.Defaults.Select(t => t.Category).ToHashSet();
        Assert.Contains("Isometric QC", cats);
        Assert.Contains("P&ID QC", cats);
        Assert.Contains("GA QC", cats);
        Assert.Equal(QuickTasks.Defaults.Count, QuickTasks.Defaults.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Every_question_in_a_quick_task_is_askable()
    {
        foreach (var t in QuickTasks.Defaults)
            foreach (var q in QuickTasks.Questions(t.Prompt)) Assert.False(string.IsNullOrWhiteSpace(q), t.Id);
    }
}

public class ProjectStoreTests
{
    [Fact]
    public void A_project_saved_with_a_shared_folder_is_visible_to_another_user_and_active_is_per_user()
    {
        using var t = new TempDir();
        var shared = Path.Combine(t.Path, "projects");
        var ann = new ProjectStore(Path.Combine(t.Path, "ann.json"), shared);
        ann.Save(new ProjectCard { Name = "Blue", Client = "ACME" });
        var bob = new ProjectStore(Path.Combine(t.Path, "bob.json"), shared);
        Assert.Equal("Blue", bob.Active?.Name);                 // sees Ann's card
        Assert.True(File.Exists(Path.Combine(shared, "Blue.json")));
        Assert.False(ann.IsLocalOnly("Blue"));
    }

    [Fact]
    public void Without_a_shared_folder_cards_stay_local_and_can_be_shared_later()
    {
        using var t = new TempDir();
        var local = new ProjectStore(Path.Combine(t.Path, "p.json"), sharedDir: null);
        local.Save(new ProjectCard { Name = "Local one" });
        Assert.False(local.IsShared);

        // later a shared folder appears: the same user file now knows the share
        var shared = Path.Combine(t.Path, "projects");
        var withShare = new ProjectStore(Path.Combine(t.Path, "p.json"), shared);
        Assert.True(withShare.IsLocalOnly("Local one"));
        Assert.Equal(1, withShare.ShareLocalCards());
        Assert.False(withShare.IsLocalOnly("Local one"));
    }

    [Fact]
    public void Project_input_sub_folders_are_not_mistaken_for_cards_and_a_broken_card_is_skipped()
    {
        using var t = new TempDir();
        var shared = Path.Combine(t.Path, "projects");
        t.File("projects/Blue/inputs/line-list.json", "{ \"not\": \"a card\" }");   // inside a sub-folder: ignored
        t.File("projects/Broken.json", "{ not json");
        t.File("projects/Good.json", "{ \"Name\": \"Good\" }");
        var s = new ProjectStore(Path.Combine(t.Path, "u.json"), shared);
        Assert.Equal(["Good"], s.All.Select(p => p.Name).ToArray());
        Assert.EndsWith(Path.Combine("projects", "Blue", "inputs"), s.DefaultInputsFolder("Blue"));
    }

    [Fact]
    public void Deleting_where_the_share_refuses_deletes_is_not_an_error_for_the_caller()
    {
        // the QC share lets users create and change files but not delete them: Delete must not throw when the file cannot go.
        using var t = new TempDir();
        var shared = Path.Combine(t.Path, "projects");
        var s = new ProjectStore(Path.Combine(t.Path, "u.json"), shared);
        s.Save(new ProjectCard { Name = "X" });
        var card = Path.Combine(shared, "X.json");
        using var hold = new FileStream(card, FileMode.Open, FileAccess.Read, FileShare.None);   // an open handle makes the delete fail, like a denied delete
        var ex = Record.Exception(() => s.Delete("X"));
        Assert.Null(ex);
    }
}

public class SharedHubTests
{
    [Fact]
    public void Ensure_creates_the_QC_layout_once_and_never_overwrites_the_readme()
    {
        using var t = new TempDir();
        var root = Path.Combine(t.Path, "PIPEING_QC");
        var st = SharedHub.Ensure(root);
        Assert.True(st.Reachable); Assert.True(st.Writable);
        foreach (var f in new[] { "skills", "learning", "requirements", "projects", "models", "reports", "users", "activity", "audit", "updates" })
            Assert.True(Directory.Exists(Path.Combine(root, f)), f);
        Assert.True(File.Exists(Path.Combine(root, "hub.json")));

        File.WriteAllText(Path.Combine(root, "README.md"), "edited by an admin");
        SharedHub.Ensure(root);
        Assert.Equal("edited by an admin", File.ReadAllText(Path.Combine(root, "README.md")));
    }

    [Fact]
    public void The_write_probe_reuses_one_file_so_a_share_without_delete_stays_clean()
    {
        using var t = new TempDir();
        var root = Path.Combine(t.Path, "hub");
        SharedHub.Ensure(root);
        SharedHub.Status(root); SharedHub.Status(root); SharedHub.Status(root);
        Assert.Single(Directory.GetFiles(root, ".write-test*"));
    }
}

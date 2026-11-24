using System.Text.Json;
using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Tests;

public class UserRegistryTests
{
    private static Settings At(string root) => new() { SharedRoot = root };
    private static UserIdentity Anita => new("Anita Rao", "E1234", "Petrocon");
    private static UserIdentity Ravi => new("Ravi Kumar", "E5678", "Petrocon");

    [Fact]
    public void Roles_parse_naturally_and_unknown_text_is_never_a_higher_role()
    {
        Assert.Equal(QcRole.Admin, QcRoles.Parse("Admin"));
        Assert.Equal(QcRole.Admin, QcRoles.Parse("administrator"));
        Assert.Equal(QcRole.Lead, QcRoles.Parse("QC_Lead"));
        Assert.Equal(QcRole.Lead, QcRoles.Parse("qc lead"));
        Assert.Equal(QcRole.Engineer, QcRoles.Parse("qc-engineer"));
        Assert.Equal(QcRole.Engineer, QcRoles.Parse("superuser"));
        Assert.Equal(QcRole.Engineer, QcRoles.Parse(null));
        Assert.False(QcRoles.TryParseStrict("superuser", out _));
        Assert.True(QcRoles.TryParseStrict("lead", out var r)); Assert.Equal(QcRole.Lead, r);
        Assert.True(QcRole.Admin > QcRole.Lead && QcRole.Lead > QcRole.Engineer);
    }

    [Fact]
    public void A_user_is_registered_with_a_file_named_by_the_employee_code()
    {
        using var t = new TempDir();
        var s = At(t.Path);
        var rec = UserRegistry.Touch(s, Anita);
        var file = Path.Combine(t.Path, "users", "E1234.json");
        Assert.True(File.Exists(file));
        var back = JsonSerializer.Deserialize<UserRecord>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Anita Rao", back.Name); Assert.Equal("Petrocon", back.Company); Assert.Equal(1, back.Sessions);
        Assert.Equal(Environment.MachineName, back.Machine); Assert.NotEmpty(back.Version); Assert.NotEmpty(back.FirstSeen);
        Assert.Equal(rec.Role, back.Role);
    }

    [Fact]
    public void The_first_user_becomes_admin_and_everyone_after_is_an_engineer()
    {
        using var t = new TempDir();
        var s = At(t.Path);
        Assert.Equal(QcRoles.Admin, UserRegistry.Touch(s, Anita).Role);
        Assert.Equal(QcRoles.Engineer, UserRegistry.Touch(s, Ravi).Role);
        Assert.Equal(QcRole.Admin, UserRegistry.RoleOf(s, Anita));
        Assert.Equal(QcRole.Engineer, UserRegistry.RoleOf(s, Ravi));
    }

    [Fact]
    public void Starting_again_keeps_the_role_and_first_seen_and_counts_the_session()
    {
        using var t = new TempDir();
        var s = At(t.Path);
        UserRegistry.Touch(s, Anita);
        UserRegistry.Touch(s, Ravi);
        Assert.True(UserRegistry.SetRole(s, "E5678", QcRole.Lead, out _));
        var first = UserRegistry.Read(Path.Combine(t.Path, "users", "E5678.json"))!;
        var again = UserRegistry.Touch(s, Ravi);
        Assert.Equal(QcRoles.Lead, again.Role);
        Assert.Equal(first.FirstSeen, again.FirstSeen);
        Assert.Equal(first.Sessions + 1, again.Sessions);
    }

    [Fact]
    public void A_role_is_changed_by_code_or_name_and_an_unknown_person_is_reported()
    {
        using var t = new TempDir();
        var s = At(t.Path);
        UserRegistry.Touch(s, Anita); UserRegistry.Touch(s, Ravi);
        Assert.True(UserRegistry.SetRole(s, "ravi kumar", QcRole.Lead, out var c));
        Assert.Equal(QcRoles.Lead, c!.Role);
        Assert.Equal(QcRole.Lead, UserRegistry.RoleOf(s, Ravi));
        Assert.False(UserRegistry.SetRole(s, "E0000", QcRole.Admin, out var none)); Assert.Null(none);
    }

    [Fact]
    public void All_lists_everyone_by_name_and_skips_damaged_files()
    {
        using var t = new TempDir();
        var s = At(t.Path);
        UserRegistry.Touch(s, Ravi); UserRegistry.Touch(s, Anita);
        File.WriteAllText(Path.Combine(t.Path, "users", "broken.json"), "{ not json");
        Assert.Equal(["Anita Rao", "Ravi Kumar"], UserRegistry.All(Path.Combine(t.Path, "users")).Select(r => r.Name).ToArray());
    }

    [Fact]
    public void An_unreachable_share_falls_back_to_a_local_record_and_never_throws()
    {
        using var t = new TempDir();
        var notAFolder = t.File("share-is-a-file", "x");                // a file where the share should be: creating folders under it fails
        var s = At(notAFolder);
        var local = Path.Combine(t.Path, "local-users");
        var rec = UserRegistry.Touch(s, Anita, local);
        Assert.Equal("Anita Rao", rec.Name);
        Assert.True(File.Exists(Path.Combine(local, "E1234.json")));
        Assert.Equal(QcRole.Admin, UserRegistry.RoleOf(s, Anita, local));      // the local record answers while the share is away
        Assert.Equal(QcRole.Engineer, UserRegistry.RoleOf(s, Ravi, local));    // unknown = lowest role
    }

    [Fact]
    public void A_missing_or_unregistered_user_is_an_engineer()
    {
        using var t = new TempDir();
        Assert.Equal(QcRole.Engineer, UserRegistry.RoleOf(At(t.Path), Anita, Path.Combine(t.Path, "none")));
    }

    [Fact]
    public void The_shared_hub_creates_a_users_folder_the_registry_can_use()
    {
        using var t = new TempDir();
        var root = Path.Combine(t.Path, "QC");
        SharedHub.Ensure(root);
        var s = At(root);
        UserRegistry.Touch(s, Anita);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "users"), "*.json"));
        Assert.Equal(1, SharedHub.Status(root).Counts["users"]);
    }
}

public class SeedContentTests
{
    private static string Repo(string rel) => TestPaths.FindUp(rel);

    [Fact]
    public void The_bundled_quick_tasks_file_matches_the_built_in_defaults()
    {
        var f = Repo(Path.Combine("prompts", "quick-tasks.json"));
        Assert.NotEqual("", f);
        var tasks = QuickTasks.Load(f);
        Assert.Equal(QuickTasks.Defaults.Select(x => (x.Id, x.Category, x.Title, x.Prompt)), tasks.Select(x => (x.Id, x.Category, x.Title, x.Prompt)));
    }

    [Fact]
    public void Seeding_copies_the_master_prompt_quick_tasks_and_platform_skills_and_never_overwrites()
    {
        var prompt = Repo(Path.Combine("prompts", "master-prompt.md"));
        Assert.NotEqual("", prompt);
        var bundled = Path.GetDirectoryName(Path.GetDirectoryName(prompt))!;
        using var t = new TempDir();
        var s = new Settings { SharedRoot = Path.Combine(t.Path, "QC") };
        PromptBuilder.SeedDefaults(s, bundled);
        Assert.True(File.Exists(s.EffectiveMasterPromptFile));
        Assert.True(File.Exists(s.EffectiveQuickTasksFile));
        var skills = PromptBuilder.SkillList(s.EffectiveSkillsDirectory).Select(x => x.Name).ToList();
        Assert.Contains("qc-platform", skills);
        Assert.Contains("qc-learning", skills);
        File.WriteAllText(s.EffectiveMasterPromptFile, "edited by the team");
        PromptBuilder.SeedDefaults(s, bundled);
        Assert.Equal("edited by the team", File.ReadAllText(s.EffectiveMasterPromptFile));
    }

    [Fact]
    public void The_seed_text_is_free_of_server_names_and_share_paths()
    {
        var prompt = Repo(Path.Combine("prompts", "master-prompt.md"));
        var bundled = Path.GetDirectoryName(Path.GetDirectoryName(prompt))!;
        foreach (var f in Directory.GetFiles(Path.Combine(bundled, "skills"), "*", SearchOption.AllDirectories).Append(prompt))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(@"\\", text);                                                       // no UNC share paths
            Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);                    // no IP addresses
        }
    }
}

using Pec.Qc.Core.Install;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Tests;

public class KitSyncTests
{
    private sealed class Pc : IDisposable
    {
        public readonly TempDir T = new();
        public string Claude, Shared;
        public KitSync Kit;
        public Pc(string shared, string user, string machine, TempDir? sharedDir = null)
        {
            Claude = System.IO.Path.Combine(T.Path, "claude"); Shared = shared;
            Directory.CreateDirectory(Claude);
            Kit = new KitSync(Claude, Shared, user, machine, System.IO.Path.Combine(T.Path, "manifest.json"));
        }
        public void Skill(string name, string body) => T.File($"claude/skills/{name}/SKILL.md", body);
        public void Dispose() => T.Dispose();
    }

    [Fact] public void A_users_skill_is_published_to_the_review_inbox_not_to_the_shared_skills()
    {
        using var office = new TempDir(); using var a = new Pc(office.Path, "ann", "pc1");
        a.Skill("my-flange-check", "---\nname: my-flange-check\ndescription: check flanges\n---\nStep 1");
        a.Skill("smart3d-qc", "---\nname: smart3d-qc\n---\nproduct skill");          // shipped with the product: never a contribution
        var dry = a.Kit.Publish(apply: false);
        Assert.Equal(["skills/my-flange-check"], dry.Published);
        Assert.False(Directory.Exists(System.IO.Path.Combine(office.Path, "contributions")));
        var r = a.Kit.Publish(apply: true);
        Assert.Single(r.Published);
        Assert.True(File.Exists(System.IO.Path.Combine(office.Path, "contributions", "ann@pc1", "skills", "my-flange-check", "SKILL.md")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(office.Path, "skills", "my-flange-check")));
        Assert.Equal(["skills/my-flange-check"], a.Kit.Publish(true).Unchanged);        // publishing again changes nothing
    }

    [Theory]
    [InlineData("connect to 10.20.30.40 and read it")]
    [InlineData(@"copy it to \\fileserver\projects\x")]
    [InlineData("password = Hunter2xyz")]
    [InlineData("Server=SQLBOX01;Database=x")]
    public void An_item_with_a_server_address_share_or_secret_is_blocked(string text)
    {
        using var office = new TempDir(); using var a = new Pc(office.Path, "ann", "pc1");
        a.Skill("leaky", "---\nname: leaky\n---\n" + text);
        var r = a.Kit.Publish(apply: true);
        Assert.Empty(r.Published); Assert.Single(r.Blocked);
        Assert.False(Directory.Exists(System.IO.Path.Combine(office.Path, "contributions")));
    }

    [Fact] public void Loopback_and_placeholders_are_not_flagged()
    {
        using var office = new TempDir(); using var a = new Pc(office.Path, "ann", "pc1");
        a.Skill("ok", "---\nname: ok\n---\nUse 127.0.0.1 for the bridge. Server is <SQL server>. Ask for the password; never store a password.");
        Assert.Single(a.Kit.Publish(true).Published);
    }

    [Fact] public void Commands_and_agents_are_shared_too_and_an_administrator_approval_reaches_everyone_without_overwriting_edits()
    {
        using var office = new TempDir(); using var ann = new Pc(office.Path, "ann", "pc1"); using var bob = new Pc(office.Path, "bob", "pc2");
        ann.T.File("claude/commands/daily-health.md", "Run qc_model_health and summarise.");
        ann.T.File("claude/agents/spec-reviewer.md", "Review a spec.");
        ann.Skill("my-flange-check", "---\nname: my-flange-check\n---\nv1");
        Assert.Equal(3, ann.Kit.Publish(true).Published.Count);

        Assert.Empty(bob.Kit.Pull().Added);                                                  // nothing approved yet
        var pending = bob.Kit.Pending();
        Assert.Equal(3, pending.Count);
        foreach (var p in pending) bob.Kit.Approve(p.Contributor, p.Kind, p.Name);
        Assert.Empty(bob.Kit.Pending());

        var pulled = bob.Kit.Pull();
        Assert.Equal(3, pulled.Added.Count);
        Assert.True(File.Exists(System.IO.Path.Combine(bob.Claude, "commands", "daily-health.md")));
        Assert.True(File.Exists(System.IO.Path.Combine(bob.Claude, "skills", "my-flange-check", "SKILL.md")));
        Assert.Empty(bob.Kit.Candidates());                                                  // what he pulled is not offered back as his own

        // a new approved version reaches bob while he has not touched it; a local edit is kept
        ann.Skill("my-flange-check", "---\nname: my-flange-check\n---\nv2");
        ann.Kit.Publish(true);
        var c = bob.Kit.Pending().Single(); bob.Kit.Approve(c.Contributor, c.Kind, c.Name);
        Assert.Contains("skills/my-flange-check", bob.Kit.Pull().Updated);
        Assert.Contains("v2", File.ReadAllText(System.IO.Path.Combine(bob.Claude, "skills", "my-flange-check", "SKILL.md")));

        bob.Skill("my-flange-check", "---\nname: my-flange-check\n---\nbob's own edit");
        ann.Skill("my-flange-check", "---\nname: my-flange-check\n---\nv3");
        ann.Kit.Publish(true); c = bob.Kit.Pending().Single(); bob.Kit.Approve(c.Contributor, c.Kind, c.Name);
        var kept = bob.Kit.Pull();
        Assert.Contains("skills/my-flange-check", kept.KeptLocal);
        Assert.Contains("bob's own edit", File.ReadAllText(System.IO.Path.Combine(bob.Claude, "skills", "my-flange-check", "SKILL.md")));
        // the replaced approved version is kept as a backup and never installed as its own skill
        Assert.Contains(Directory.GetDirectories(System.IO.Path.Combine(office.Path, "skills")), d => d.Contains(".bak_"));
        Assert.DoesNotContain(bob.Kit.Pull().Added, a => a.Contains(".bak_"));
    }

    [Fact] public void Reject_removes_an_item_from_the_inbox()
    {
        using var office = new TempDir(); using var a = new Pc(office.Path, "ann", "pc1");
        a.Skill("x", "---\nname: x\n---\nhi"); a.Kit.Publish(true);
        a.Kit.Reject("ann@pc1", KitKind.Skill, "x");
        Assert.Empty(a.Kit.Pending());
        Assert.Throws<FileNotFoundException>(() => a.Kit.Approve("ann@pc1", KitKind.Skill, "x"));
    }
}

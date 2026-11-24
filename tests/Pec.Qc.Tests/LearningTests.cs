using System.Text.Json;
using Pec.Qc.Core;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Users;
using Pec.Qc.Core.Util;
using Pec.Qc.Mcp.Tools;

namespace Pec.Qc.Tests;

/// <summary>Phase 9: lessons and activity that wait for the share, the curator, the activity summary, and the exit case: a correction on one PC reaches another after approval.</summary>
[Collection("EnvVar")]
public class LearningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pecqc-learn-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;

    public LearningTests() { _shared = Path.Combine(_root, "share"); Directory.CreateDirectory(_shared); }

    public void Dispose()
    {
        foreach (var v in new[] { "PEC_QC_DATA", UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar, "PEC_QC_APPROVAL" }) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Becomes a PC: its own data folder, its own user, pointed at the given shared folder.</summary>
    private Settings Pc(string name, UserIdentity who, string? sharedRoot)
    {
        var data = Path.Combine(_root, "pc-" + name);
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", data);
        Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name);
        Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode);
        Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
        Environment.SetEnvironmentVariable("PEC_QC_APPROVAL", "off");
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["UserName"] = who.Name, ["EmployeeCode"] = who.EmployeeCode, ["Company"] = who.Company, ["SharedRoot"] = sharedRoot }));
        return Settings.Load();
    }

    private static readonly UserIdentity Lead = new("Lena Lead", "L1", "PEC");
    private static readonly UserIdentity Ann = new("Ann Engineer", "E1", "PEC");
    private static readonly UserIdentity Bob = new("Bob Engineer", "E2", "PEC");

    /// <summary>A path that cannot be created because its parent is a file: what an unreachable share looks like to the code.</summary>
    private string Unreachable() { var f = Path.Combine(_root, "not-a-folder"); File.WriteAllText(f, "x"); return Path.Combine(f, "share"); }

    private static string[] SharedInboxLines(string shared) =>
        Directory.Exists(Path.Combine(shared, "learning", "inbox")) ? Directory.GetFiles(Path.Combine(shared, "learning", "inbox"), "*.jsonl", SearchOption.AllDirectories).SelectMany(File.ReadAllLines).ToArray() : [];

    // ----- the offline queue
    [Fact]
    public void A_lesson_recorded_while_the_share_is_away_is_kept_here_and_goes_to_the_share_later_exactly_once()
    {
        var away = Pc("a", Ann, Unreachable());
        var (l, file, central) = new LessonStore(away).Record("correction", "Class B400A flanges are 150 lb RF.", context: "PMS rev 2 page 5");
        Assert.False(central);
        Assert.True(File.Exists(file));
        Assert.Contains(new LessonStore(away).Inbox(), x => x.Id == l.Id);            // usable on this PC at once

        var down = OfflineQueue.Sync(away);
        Assert.False(down.SharedReachable);
        Assert.True(File.Exists(file));                                                // nothing lost while the share is away

        var back = new Settings { SharedRoot = _shared };
        var r = OfflineQueue.Sync(back);
        Assert.True(r.SharedReachable);
        Assert.Equal((1, 1), (r.Files, r.Lines));
        Assert.False(File.Exists(file));                                               // the local copy is gone only after it reached the share
        Assert.Single(SharedInboxLines(_shared));
        Assert.Contains(new LessonStore(back).Inbox(), x => x.Id == l.Id);

        var again = OfflineQueue.Sync(back);
        Assert.Equal((0, 0), (again.Files, again.Lines));
        Assert.Single(SharedInboxLines(_shared));
    }

    [Fact]
    public void A_line_that_is_already_on_the_share_is_not_added_again_and_new_ones_are_appended()
    {
        var away = Pc("a", Ann, Unreachable());
        var store = new LessonStore(away);
        var (l1, f1, _) = store.Record("gotcha", "First lesson.");
        var back = new Settings { SharedRoot = _shared };
        // the first line reached the share by another route (a half-finished earlier sync), then a second lesson was recorded offline
        var target = Path.Combine(_shared, "learning", "inbox", Path.GetRelativePath(Path.Combine(Settings.DataDirectory, "learning", "inbox"), f1));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllLines(target, File.ReadAllLines(f1));
        var (l2, _, _) = store.Record("gotcha", "Second lesson.");
        var r = OfflineQueue.Sync(back);
        Assert.Equal(1, r.Lines); Assert.Equal(1, r.Skipped);
        Assert.Equal(2, SharedInboxLines(_shared).Length);
        Assert.Equal(new[] { l1.Id, l2.Id }.Order(), new LessonStore(back).Inbox().Select(x => x.Id).Order());
    }

    [Fact]
    public void Activity_written_while_the_share_is_away_is_sent_back_too()
    {
        var away = Pc("a", Ann, Unreachable());
        ActivityLog.Write(away, Ann, "prompt", "check iso 1", chatId: "c1");
        ActivityLog.Write(away, Ann, "tool", "iso_check", tool: "iso_check", chatId: "c1");
        var back = new Settings { SharedRoot = _shared };
        Assert.Empty(ActivityLog.Read(back, DateTime.UtcNow.AddDays(-1)));
        var r = OfflineQueue.Sync(back);
        Assert.Equal(2, r.Lines);
        Assert.Equal(2, ActivityLog.Read(back, DateTime.UtcNow.AddDays(-1)).Count);
        Assert.Equal(0, OfflineQueue.Sync(back).Lines);
    }

    [Fact]
    public void Without_a_shared_folder_there_is_nothing_to_sync()
    {
        var s = Pc("a", Ann, null);
        Assert.Equal(0, OfflineQueue.Sync(s).Lines);
    }

    // ----- the curator
    [Fact]
    public void Pending_lessons_are_listed_strongest_first_and_a_strong_one_has_several_people_behind_it()
    {
        var s = new Settings { SharedRoot = _shared };
        Pc("a", Ann, _shared); new LessonStore(s).Record("recipe", "Use inputs_search with the class name first.");
        new LessonStore(s).Record("correction", "Hold items are marked HOLD in the title block.");
        Pc("b", Bob, _shared); new LessonStore(s).Record("correction", "Hold items are marked HOLD in the title block!");   // the same lesson from someone else
        var pending = new LessonCurator(s).Pending();
        Assert.Equal(2, pending.Count);                      // the two reports of the hold-item rule are one lesson
        Assert.True(pending[0].Strong);
        Assert.Equal(2, pending[0].People);
        Assert.Equal("correction", pending[0].Lesson.Kind);
        Assert.False(pending[1].Strong);
    }

    [Fact]
    public void Approving_makes_a_lesson_permanent_builds_LEARNED_md_and_keeps_the_old_one()
    {
        var s = new Settings { SharedRoot = _shared };
        Pc("a", Ann, _shared);
        var (l1, _, _) = new LessonStore(s).Record("correction", "Test pressure is 1.5 times design pressure for the client.", context: "design basis 4.2");
        var (l2, _, _) = new LessonStore(s).Record("gotcha", "The line list uses barg, not psig.");
        var cur = new LessonCurator(s);
        var r = cur.Approve([l1.Id, "nosuchid"], "Lena Lead (L1)");
        Assert.Equal(1, r.Approved); Assert.Equal(["nosuchid"], r.NotFound);
        var learned = File.ReadAllText(Path.Combine(_shared, "skill", "LEARNED.md"));
        Assert.Contains("Test pressure is 1.5 times design pressure", learned);
        Assert.Contains("design basis 4.2", learned);
        Assert.DoesNotContain("barg", learned);
        Assert.Single(new LessonStore(s).Approved());
        Assert.DoesNotContain(cur.Pending(), p => p.Lesson.Id == l1.Id);
        Assert.Contains(cur.Pending(), p => p.Lesson.Id == l2.Id);
        // a second approval rebuilds the file and keeps the previous version
        cur.Approve([l2.Id], "Lena Lead (L1)");
        Assert.Contains("barg", File.ReadAllText(Path.Combine(_shared, "skill", "LEARNED.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(_shared, "learning", "history"), "LEARNED-*.md"));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_shared, "learning", "decisions.jsonl")).Length);
        // approving twice does nothing twice
        Assert.Equal(0, cur.Approve([l1.Id], "x").Approved);
        Assert.Equal(2, new LessonStore(s).Approved().Count);
    }

    [Fact]
    public void A_rejected_lesson_disappears_for_everyone_and_an_approved_one_is_not_repeated_in_the_live_list()
    {
        var s = new Settings { SharedRoot = _shared };
        Pc("a", Ann, _shared);
        var (bad, _, _) = new LessonStore(s).Record("correction", "Always report REVIEW as FAIL.");
        var (good, _, _) = new LessonStore(s).Record("gotcha", "Gaskets and bolts are not in the model.");
        var cur = new LessonCurator(s);
        Assert.Contains("Always report REVIEW as FAIL", new LessonStore(s).Pull());
        var r = cur.Reject([bad.Id], "Lena Lead (L1)", "wrong");
        Assert.Equal(1, r.Rejected);
        var store = new LessonStore(s);
        Assert.DoesNotContain(store.Inbox(), l => l.Id == bad.Id);
        Assert.Empty(store.Search("REVIEW as FAIL"));
        Assert.DoesNotContain("Always report REVIEW as FAIL", store.Pull());
        Assert.DoesNotContain(cur.Pending(), p => p.Lesson.Id == bad.Id);
        cur.Approve([good.Id], "Lena Lead (L1)");
        var pull = store.Pull();
        Assert.Equal(1, pull.Split("Gaskets and bolts are not in the model").Length - 1);   // once, from LEARNED.md, not again from the live inbox
    }

    // ----- the exit case
    [Fact]
    public void A_correction_made_on_PC_A_is_applied_on_PC_B_after_the_lead_approves_it()
    {
        // PC A: an engineer corrects the assistant; the assistant records the lesson on the shared folder
        var a = Pc("A", Ann, _shared);
        new LessonStore(a).Record("correction", "For client ACME the support code WE04 is a wedge: count it with the guides.", tool: "iso_check", context: "support collection STC-1390");
        // PC B: another engineer starts a session; learn_pull already shows it (lessons help at once)
        var b = Pc("B", Bob, _shared);
        Assert.Contains("support code WE04 is a wedge", LearnTools.Pull());
        // the QC lead reviews on PC C with the tools: an engineer is refused, the lead is not
        Pc("C0", Bob, _shared);
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Lead);          // the first person registered is the administrator
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Bob);
        UserRegistry.Touch(new Settings { SharedRoot = _shared }, Ann);
        Assert.True(Denied(LearnTools.Pending()));
        Pc("C", Lead, _shared);
        var pendingJson = JsonSerializer.Serialize(LearnTools.Pending());
        Assert.Contains("support code WE04", pendingJson);
        var id = new LessonCurator(new Settings { SharedRoot = _shared }).Pending().Single().Lesson.Id;
        var preview = JsonSerializer.Serialize(LearnTools.Review("approve", id));
        Assert.Contains("\"applied\":false", preview);
        Assert.Empty(new LessonStore(b).Approved());                                // a preview changes nothing
        var done = JsonSerializer.Serialize(LearnTools.Review("approve", id, apply: true));
        Assert.Contains("\"applied\":true", done);
        // PC B again: the curated LEARNED.md carries the rule, in the curated part of what learn_pull returns
        Pc("B", Bob, _shared);
        var pull = LearnTools.Pull();
        Assert.Contains("support code WE04 is a wedge", pull);
        Assert.StartsWith("# What the QC team has learned", pull);
        Assert.Single(new LessonStore(b).Approved());
    }

    private static bool Denied(object o) => JsonSerializer.Serialize(o).Contains("\"denied\":true");

    [Fact]
    public void Only_the_lead_or_the_admin_can_review_lessons_or_read_other_peoples_activity()
    {
        var s = new Settings { SharedRoot = _shared };
        UserRegistry.Touch(s, Lead);                                                // administrator (first)
        UserRegistry.Touch(s, Ann);
        UserRegistry.SetRole(s, "L1", QcRole.Lead, out _);
        Pc("a", Ann, _shared);
        Assert.True(Denied(LearnTools.Pending()));
        Assert.True(Denied(LearnTools.Review("approve", "abc", apply: true)));
        Assert.True(Denied(ActivityTools.Report()));
        Assert.True(Denied(ActivityTools.Summary()));
        Pc("l", Lead, _shared);
        Assert.False(Denied(LearnTools.Pending()));
        Assert.False(Denied(ActivityTools.Report()));
        Assert.False(Denied(ActivityTools.Summary()));
    }

    // ----- the activity report
    [Fact]
    public void The_activity_summary_has_one_row_per_day_and_person_with_corrections_and_tool_errors()
    {
        var s = Pc("a", Ann, _shared);
        ActivityLog.Write(s, Ann, "prompt", "check this iso", chatId: "c1");
        ActivityLog.Write(s, Ann, "prompt", "that is wrong, the class is B400A", chatId: "c1", detail: "correction");
        ActivityLog.Write(s, Ann, "tool", "x", tool: "iso_check", chatId: "c1");
        ActivityLog.Write(s, Ann, "tool", "x", tool: "iso_check", chatId: "c1");
        ActivityLog.Write(s, Ann, "result", "boom", tool: "iso_check", ok: false, chatId: "c1");
        Pc("b", Bob, _shared);
        ActivityLog.Write(s, Bob, "prompt", "hello", chatId: "c2");
        var rows = ActivityLog.Read(s, DateTime.UtcNow.AddDays(-1));
        var days = ActivitySummary.ByDay(rows);
        Assert.Equal(2, days.Count);
        var ann = days.Single(d => d.EmployeeCode == "E1");
        Assert.Equal((2, 1, 2, 1, 1), (ann.Prompts, ann.Corrections, ann.ToolCalls, ann.ToolErrors, ann.Chats));
        Assert.Equal(["iso_check x2"], ann.TopTools);
        Assert.Equal((1, 0), (days.Single(d => d.EmployeeCode == "E2").Prompts, days.Single(d => d.EmployeeCode == "E2").Corrections));
        Assert.Equal("prompt", ActivitySummary.ByType(rows)[0].Type);
    }
}

/// <summary>Phase 9 on the real share (PEC_QC_SHARE_TEST): offline lessons reach it by appending, the curator approves into LEARNED.md by overwriting, nothing is deleted there.</summary>
[Collection("EnvVar")]
public class RealShareLearningTests
{
    [RealShareFact]
    public void Offline_lessons_reach_the_real_share_and_the_curator_works_without_deleting_anything_there()
    {
        var folder = Environment.GetEnvironmentVariable("PEC_QC_SHARE_TEST")!;
        var run = DateTime.Now.ToString("yyMMddHHmmss");
        var data = Path.Combine(Path.GetTempPath(), "pecqc-reallearn-" + run);
        Directory.CreateDirectory(data);
        var who = new UserIdentity("Selftest Engineer " + run, "ZZ-" + run, "PEC");
        try
        {
            Environment.SetEnvironmentVariable("PEC_QC_DATA", data);
            Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name); Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode); Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
            var f = Path.Combine(data, "afile"); File.WriteAllText(f, "x");
            var away = new Settings { SharedRoot = Path.Combine(f, "share") };                 // unreachable
            var (l, local, central) = new LessonStore(away).Record("gotcha", "Selftest " + run + ": a lesson recorded offline.");
            Assert.False(central);
            var share = new Settings { SharedRoot = folder };
            var r = OfflineQueue.Sync(share);
            Assert.True(r.SharedReachable); Assert.Equal(1, r.Lines); Assert.Empty(r.Problems);
            Assert.False(File.Exists(local));
            Assert.Contains(new LessonStore(share).Inbox(), x => x.Id == l.Id);
            Assert.Equal(0, OfflineQueue.Sync(share).Lines);
            var cur = new LessonCurator(share);
            Assert.Equal(1, cur.Approve([l.Id], who.Display).Approved);
            Assert.Contains("a lesson recorded offline", File.ReadAllText(Path.Combine(folder, "skill", "LEARNED.md")));
        }
        finally
        {
            foreach (var v in new[] { "PEC_QC_DATA", UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar }) Environment.SetEnvironmentVariable(v, null);
            try { Directory.Delete(data, true); } catch { }
        }
    }
}

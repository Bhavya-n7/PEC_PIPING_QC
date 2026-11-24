using Pec.Qc.Core;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Users;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Tests;

/// <summary>Runs only when PEC_QC_SHARE_TEST names a folder (for example a clearly named sub-folder of the real QC share): everything the tests write there is permanent, because users cannot delete files on that share.</summary>
public sealed class RealShareFactAttribute : FactAttribute
{
    public RealShareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PEC_QC_SHARE_TEST")))
            Skip = "set PEC_QC_SHARE_TEST to a test folder on the real share to run this";
    }
}

/// <summary>Phase 2 exit check on a real share: two people see each other's lessons and registry entries, logs append, and an unreachable share falls back to this PC.</summary>
[Collection("EnvVar")]
public class RealShareTests
{
    private static string Folder => Environment.GetEnvironmentVariable("PEC_QC_SHARE_TEST")!;

    private static void As(UserIdentity who)
    {
        Environment.SetEnvironmentVariable(UserIdentity.NameVar, who.Name);
        Environment.SetEnvironmentVariable(UserIdentity.CodeVar, who.EmployeeCode);
        Environment.SetEnvironmentVariable(UserIdentity.CompanyVar, who.Company);
    }

    private static void Reset() { foreach (var v in new[] { UserIdentity.NameVar, UserIdentity.CodeVar, UserIdentity.CompanyVar }) Environment.SetEnvironmentVariable(v, null); }

    [RealShareFact]
    public void Two_people_share_registry_lessons_activity_and_audit_on_the_real_share()
    {
        // unique people per run: a run never depends on what an earlier run left behind (files cannot be deleted there)
        var run = DateTime.Now.ToString("yyMMddHHmmss");
        var settings = new Settings { SharedRoot = Folder };
        var local = Path.Combine(Path.GetTempPath(), "pecqc-realshare-" + run);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", local);
        try
        {
            var st = SharedHub.Ensure(Folder);
            Assert.True(st.Reachable); Assert.True(st.Writable);

            var ann = new UserIdentity("Selftest Ann " + run, "ZZ-A" + run, "PEC");
            var bob = new UserIdentity("Selftest Bob " + run, "ZZ-B" + run, "PEC");

            // registry: both appear; roles; a role change sticks and survives the person's next start
            var a = UserRegistry.Touch(settings, ann);
            var b = UserRegistry.Touch(settings, bob);
            var all = UserRegistry.All(settings.EffectiveUsersDirectory);
            Assert.Contains(all, r => r.EmployeeCode == ann.EmployeeCode);
            Assert.Contains(all, r => r.EmployeeCode == bob.EmployeeCode);
            Assert.True(a.RoleValue >= QcRole.Engineer && b.RoleValue >= QcRole.Engineer);
            Assert.True(UserRegistry.SetRole(settings, bob.EmployeeCode, QcRole.Lead, out _));
            var b2 = UserRegistry.Touch(settings, bob);
            Assert.Equal(QcRoles.Lead, b2.Role);
            Assert.Equal(2, b2.Sessions);

            // lessons: Ann records, Bob finds it
            As(ann);
            new LessonStore(settings).Record("gotcha", "Selftest " + run + ": class B400A is stainless 150#");
            As(bob);
            var seenByBob = new LessonStore(settings).Search("Selftest " + run);
            Assert.Contains(seenByBob, l => l.EmployeeCode == ann.EmployeeCode);

            // activity: both write, anyone reads both, a second write appends to the existing file
            ActivityLog.Write(settings, ann, "prompt", "selftest prompt " + run);
            ActivityLog.Write(settings, bob, "prompt", "selftest prompt " + run);
            ActivityLog.Write(settings, ann, "answer", "selftest answer " + run);
            var rows = ActivityLog.Read(settings, DateTime.UtcNow.AddHours(-1), contains: run);
            Assert.Contains(rows, r => r.EmployeeCode == ann.EmployeeCode && r.Type == "prompt");
            Assert.Contains(rows, r => r.EmployeeCode == bob.EmployeeCode && r.Type == "prompt");
            Assert.Contains(rows, r => r.EmployeeCode == ann.EmployeeCode && r.Type == "answer");

            // audit: a line lands in the shared audit folder
            var auditId = AuditLog.Write(settings, "selftest_tool", "selftest-project", "args " + run, "ok");
            Assert.False(string.IsNullOrWhiteSpace(auditId));
            Assert.True(Directory.GetFiles(settings.EffectiveAuditDirectory, "*.jsonl").Length >= 1);
        }
        finally { Reset(); Environment.SetEnvironmentVariable("PEC_QC_DATA", null); }
    }

    [RealShareFact]
    public void An_unreachable_share_falls_back_to_this_PC_without_throwing()
    {
        var run = DateTime.Now.ToString("yyMMddHHmmss");
        var local = Path.Combine(Path.GetTempPath(), "pecqc-offline-" + run);
        Environment.SetEnvironmentVariable("PEC_QC_DATA", local);
        try
        {
            // a share that does not exist (the server part is made up): reads see nothing, writes go to this PC
            var settings = new Settings { SharedRoot = @"\\pecqc-no-such-server-" + run + @"\share" };
            var who = new UserIdentity("Offline Olga " + run, "ZZ-O" + run, "PEC");
            var rec = UserRegistry.Touch(settings, who, Path.Combine(local, "users"));
            Assert.Equal(who.EmployeeCode, rec.EmployeeCode);
            Assert.True(File.Exists(Path.Combine(local, "users", who.FileKey + ".json")));
            ActivityLog.Write(settings, who, "prompt", "offline " + run);
            Assert.True(Directory.GetFiles(Path.Combine(local, "activity"), "*.jsonl", SearchOption.AllDirectories).Length >= 1);
        }
        finally { Environment.SetEnvironmentVariable("PEC_QC_DATA", null); }
    }
}

using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Install;
using Pec.Qc.Core.Users;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Shared lookups for the tools: settings, identity, active project, Navisworks install, approval gate, audit.</summary>
internal static class Resolve
{
    public static Settings Settings() => Core.Settings.Load();

    private static readonly Core.Approvals.ApprovalGate Gate = new();

    /// <summary>For an apply=true action: null when the user has approved it (or approvals are off), else the "waiting" result to return.</summary>
    public static object? NeedApproval(string tool, string canonicalArguments, string summary, string detail, bool destructive = false) =>
        Gate.Check(tool, canonicalArguments, summary, detail, destructive) == Core.Approvals.ApprovalState.Allowed
            ? null
            : new { applied = false, ran = false, waitingForApproval = true, message = Core.Approvals.ApprovalGate.WaitMessage, summary };

    public static UserIdentity Who() => UserIdentity.Current(Settings());

    public static QcRole Role() => UserRegistry.RoleOf(Settings(), Who());

    /// <summary>Null when this user's role is at least <paramref name="minimum"/>; else the refusal to return from the tool (roles are enforced by the tools, not only by the app).</summary>
    public static object? Require(QcRole minimum, string action)
    {
        var have = Role();
        return have >= minimum ? null : new
        {
            applied = false, ran = false, denied = true, yourRole = QcRoles.Name(have), neededRole = QcRoles.Name(minimum),
            message = $"{action} needs the role '{QcRoles.Name(minimum)}'; you are '{QcRoles.Name(have)}'. Ask an administrator (set_user_role) if this should be allowed.",
        };
    }

    public static ProjectCard? ActiveProject() => new ProjectStore().Active;

    public static NavisworksInstall? Navisworks() => NavisworksInstall.Find(Settings().NavisworksHome);

    /// <summary>One audit line for a shared write (who, tool, project, argument hash, outcome); returns the audit id.</summary>
    public static string Audit(string tool, string canonicalArguments, string outcome) =>
        AuditLog.Write(Settings(), tool, ActiveProject()?.Name ?? "(none)", canonicalArguments, outcome);
}

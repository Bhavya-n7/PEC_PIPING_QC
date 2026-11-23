using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Install;
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

    public static ProjectCard? ActiveProject() => new ProjectStore().Active;

    public static NavisworksInstall? Navisworks() => NavisworksInstall.Find(Settings().NavisworksHome);

    /// <summary>One audit line for a shared write (who, tool, project, argument hash, outcome); returns the audit id.</summary>
    public static string Audit(string tool, string canonicalArguments, string outcome) =>
        AuditLog.Write(Settings(), tool, ActiveProject()?.Name ?? "(none)", canonicalArguments, outcome);
}

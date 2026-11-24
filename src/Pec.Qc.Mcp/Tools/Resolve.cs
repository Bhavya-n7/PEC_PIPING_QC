using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Inputs;
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
    public static object? NeedApproval(string tool, string canonicalArguments, string summary, string detail, bool destructive = false)
    {
        var state = Gate.Check(tool, canonicalArguments, summary, detail, destructive, Core.Approvals.ApprovalGate.DefaultWait);
        return state switch
        {
            Core.Approvals.ApprovalState.Allowed => null,
            Core.Approvals.ApprovalState.Denied => new { applied = false, ran = false, denied = true, message = Core.Approvals.ApprovalGate.DeniedMessage, summary },
            _ => new { applied = false, ran = false, waitingForApproval = true, message = Core.Approvals.ApprovalGate.WaitMessage, summary },
        };
    }

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

    /// <summary>
    /// The inputs folder (PMS, line list, P&amp;IDs, vendor data) of the named project, or of the active one. Throws a message for the user when there is no project, no folder
    /// or the folder is missing, so each tool answers the same way.
    /// </summary>
    public static (InputsLibrary Lib, ProjectCard Project) Inputs(string? projectName = null)
    {
        var store = new ProjectStore();
        var card = string.IsNullOrWhiteSpace(projectName) ? store.Active : store.All.FirstOrDefault(p => p.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (card is null) throw new ModelContextProtocol.McpException(string.IsNullOrWhiteSpace(projectName) ? "There is no active QC project. Add one in the app (Projects) first." : $"There is no project named '{projectName}'.");
        var folder = string.IsNullOrWhiteSpace(card.InputsFolder) ? store.DefaultInputsFolder(card.Name) : card.InputsFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new ModelContextProtocol.McpException($"Project '{card.Name}' has no inputs folder. Edit the project and set the folder with the PMS, line list, P&IDs and vendor data.");
        if (!Directory.Exists(folder)) throw new ModelContextProtocol.McpException($"The inputs folder of project '{card.Name}' does not exist: {folder}");
        return (new InputsLibrary(folder), card);
    }

    /// <summary>One audit line for a shared write (who, tool, project, argument hash, outcome); returns the audit id.</summary>
    public static string Audit(string tool, string canonicalArguments, string outcome) =>
        AuditLog.Write(Settings(), tool, ActiveProject()?.Name ?? "(none)", canonicalArguments, outcome);
}

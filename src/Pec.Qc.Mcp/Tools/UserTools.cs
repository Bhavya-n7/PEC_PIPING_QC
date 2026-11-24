using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

/// <summary>The central user registry (<c>users\</c> in the shared folder) and the roles that the tools enforce.</summary>
[McpServerToolType]
public static class UserTools
{
    [McpServerTool(Name = "users_list", ReadOnly = true)]
    [Description("QC LEAD / ADMINISTRATOR: the people registered in the shared folder: name, employee code, company, role (qc-engineer, qc-lead, admin), first and last seen, app version, PC. Roles: qc-engineer runs checks; qc-lead also approves lessons, requirements and reports; admin also manages users, updates and the shared folder.")]
    public static object List()
    {
        if (Resolve.Require(QcRole.Lead, "Listing the users") is { } denied) return denied;
        var s = Resolve.Settings();
        var all = UserRegistry.All(s.EffectiveUsersDirectory);
        return new { count = all.Count, users = all.Select(r => new { r.Name, r.EmployeeCode, r.Company, r.Role, r.FirstSeen, r.LastSeen, r.Version, r.Machine, r.Sessions }), location = s.EffectiveUsersDirectory };
    }

    [McpServerTool(Name = "set_user_role", Destructive = true)]
    [Description("ADMINISTRATOR: change the role of a registered user (qc-engineer, qc-lead or admin), by employee code or exact name. apply=false shows the change; apply=true needs the user's approval in the app and is audited. An administrator cannot remove their own admin role (another admin must do it).")]
    public static object SetRole(string user, string role, bool apply = false)
    {
        if (Resolve.Require(QcRole.Admin, "Changing a role") is { } denied) return denied;
        if (!QcRoles.TryParseStrict(role, out var r)) throw new McpException("role must be qc-engineer, qc-lead or admin.");
        var s = Resolve.Settings();
        var target = UserRegistry.All(s.EffectiveUsersDirectory).FirstOrDefault(x =>
            string.Equals(x.EmployeeCode, user.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, user.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) throw new McpException($"'{user}' is not in the registry (users_list shows who is).");
        var me = Resolve.Who();
        if (r != QcRole.Admin && string.Equals(target.EmployeeCode, me.EmployeeCode, StringComparison.OrdinalIgnoreCase) && target.RoleValue == QcRole.Admin)
            throw new McpException("You cannot remove your own admin role; ask another administrator.");
        var summary = $"{target.Name} ({target.EmployeeCode}): {target.Role} -> {QcRoles.Name(r)}";
        if (!apply) return new { applied = false, change = summary, next = "Call again with apply=true to make the change." };
        if (Resolve.NeedApproval("set_user_role", $"{target.EmployeeCode}|{QcRoles.Name(r)}", "Change a user's role: " + summary, "Roles decide who can approve lessons, requirements and reports, and who can manage users.", destructive: true) is { } wait) return wait;
        if (!UserRegistry.SetRole(s, target.EmployeeCode, r, out var changed)) throw new McpException("The registry could not be written (is the shared folder reachable and writable?).");
        Resolve.Audit("set_user_role", $"{target.EmployeeCode}|{QcRoles.Name(r)}", summary);
        return new { applied = true, change = summary, user = changed };
    }
}

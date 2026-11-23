using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Install;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Mcp.Tools;

/// <summary>Sharing the skills, commands and agents that users create with Claude through the office folder, and the optional Graphify set-up.</summary>
[McpServerToolType]
public static class KitTools
{
    private static KitSync Kit()
    {
        var s = Resolve.Settings();
        if (!s.HasShared) throw new McpException("No shared office folder is set (Settings > Shared office folder), so there is nothing to share with.");
        return KitSync.ForThisPc(s);
    }

    private static KitKind Kind(string k) => k.Trim().ToLowerInvariant() switch
    {
        "skill" or "skills" => KitKind.Skill, "command" or "commands" => KitKind.Command, "agent" or "agents" => KitKind.Agent,
        _ => throw new McpException("kind must be skill, command or agent."),
    };

    [McpServerTool(Name = "kit_status", ReadOnly = true)]
    [Description("What this user has created with Claude (skills, slash commands, agents in their .claude folder) that is not yet shared, what is waiting for administrator review, and what the company has approved. Call it after the user creates or edits a skill, command or agent.")]
    public static object Status()
    {
        var k = Kit();
        return new
        {
            yourNewOrChanged = k.Candidates().Select(i => $"{i.Kind}: {i.Name}"),
            waitingForReview = k.Pending().Select(c => $"{c.Contributor}: {c.Kind} {c.Name}"),
            next = "kit_publish shows what would be shared (apply=false) and shares it for review (apply=true). Use kit_pull to install what the company approved.",
        };
    }

    [McpServerTool(Name = "kit_publish")]
    [Description("Share the user's own skills, slash commands and agents with the office: they are copied to a REVIEW inbox in the shared folder, and an administrator approves them before anyone else gets them. Items that look like they contain a server name, IP address, share path, password or key are blocked and listed. Hooks and settings are never shared. apply=false only reports.")]
    public static object Publish(bool apply = false)
    {
        var r = Kit().Publish(apply);
        if (apply && r.Published.Count > 0) Resolve.Audit("kit_publish", string.Join(",", r.Published), "shared for review");
        return new
        {
            applied = apply, shared = r.Published, alreadyShared = r.Unchanged, blocked = r.Blocked,
            next = r.Blocked.Count > 0 ? "Remove the listed values from the blocked items (use placeholders), then publish again." : apply ? "Waiting for administrator approval (kit_pending / kit_approve)." : "Call again with apply=true to share these for review.",
        };
    }

    [McpServerTool(Name = "kit_pending", ReadOnly = true)]
    [Description("ADMINISTRATOR: the skills, commands and agents users have shared that wait for review, with where to read them.")]
    public static object Pending() => Kit().Pending().Select(c => new { c.Contributor, kind = c.Kind.ToString().ToLowerInvariant(), c.Name, c.Path });

    [McpServerTool(Name = "kit_approve")]
    [Description("ADMINISTRATOR: approve (or reject) one shared item so everyone gets it on their next pull. Read the item first (kit_pending gives its path): skills, commands and agents steer Claude and can run tools, so approve only what you have reviewed. The item it replaces is kept as .bak.")]
    public static object Approve(string contributor, string kind, string name, bool reject = false, bool apply = false)
    {
        var k = Kit(); var kd = Kind(kind);
        if (!apply) return new { applied = false, action = reject ? "reject" : "approve", contributor, kind, name, next = "Read the item, then call again with apply=true." };
        if (Resolve.NeedApproval("kit_approve", $"{contributor}|{kind}|{name}|{reject}", (reject ? "Reject " : "Approve ") + $"{kind} '{name}' from {contributor}",
                "Approved items are installed on every user's PC at their next pull.", destructive: true) is { } wait) return wait;
        try
        {
            if (reject) k.Reject(contributor, kd, name); else k.Approve(contributor, kd, name);
        }
        catch (FileNotFoundException e) { throw new McpException(e.Message); }
        Resolve.Audit("kit_approve", $"{contributor}|{kind}|{name}", reject ? "rejected" : "approved");
        return new { applied = true, action = reject ? "rejected" : "approved", name };
    }

    [McpServerTool(Name = "kit_pull")]
    [Description("Install the skills, commands and agents the company has approved into this user's .claude folder. A local copy the user changed is never overwritten (it is listed). New items are available in the next Claude Code session.")]
    public static object Pull(bool apply = true)
    {
        var r = Kit().Pull(apply);
        return new { applied = apply, added = r.Added, updated = r.Updated, keptBecauseYouChangedThem = r.KeptLocal, note = "Start a new Claude Code session to load new skills, commands and agents." };
    }

}

using System.ComponentModel;
using ModelContextProtocol.Server;
using Pec.Qc.Core;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Setup;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class EnvironmentTools
{
    /// <summary>What the active project's inputs folder holds and what each QC type is still missing; a problem line when there is nothing to check against.</summary>
    private static object? InputsSummary(Pec.Qc.Core.Claude.ProjectCard? project, List<string> problems)
    {
        if (project is null) return null;
        try
        {
            var (lib, _) = Resolve.Inputs();
            var inv = lib.Inventory();
            var missing = Enum.GetValues<Pec.Qc.Core.Findings.QcType>().ToDictionary(t => Pec.Qc.Core.Findings.QcTypes.Code(t), t => lib.Coverage(t, inv).Where(c => !c.Present).Select(c => Pec.Qc.Core.Inputs.InputKinds.Label(c.Kind)).ToList());
            if (inv.Count == 0) problems.Add("The project inputs folder is empty: add the PMS, line list, P&IDs and vendor data (inputs_list says where).");
            return new { folder = lib.Folder, files = inv.Count, byKind = inv.GroupBy(d => d.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()), missingFor = missing };
        }
        catch (ModelContextProtocol.McpException e) { problems.Add(e.Message); return new { problem = e.Message }; }
    }

    [McpServerTool(Name = "qc_status", ReadOnly = true)]
    [Description("The state of the QC assistant on this PC: who is using it, the shared office folder (reachable, writable, what it holds), the active QC project, Navisworks Manage, and whether a newer version is waiting. Call it first; it lists what is ready and what blocks work.")]
    public static async Task<object> Status()
    {
        var s = Resolve.Settings();
        var who = Resolve.Who();
        var me = UserRegistry.Touch(s, who, newSession: false);
        var project = Resolve.ActiveProject();
        var nav = Resolve.Navisworks();

        object shared;
        if (s.HasShared)
        {
            var st = SharedHub.Status(s.SharedRoot!);
            shared = new { configured = true, reachable = st.Reachable, writable = st.Writable, counts = st.Counts, note = st.Reachable ? "Users can create and change files here but not delete them." : "The shared folder cannot be reached: the assistant works from local copies." };
        }
        else shared = new { configured = false, note = "No shared office folder is set: skills, lessons and projects are kept on this PC only." };

        var update = await Updates.CheckAsync(s.EffectiveUpdateSource);
        var problems = new List<string>();
        if (!who.IsComplete) problems.Add("Identity not set: name and employee code are asked once in the app.");
        if (!s.HasShared) problems.Add("No shared office folder configured.");
        if (project is null) problems.Add("No active QC project: set one (client, Navisworks model, project inputs folder).");
        if (nav is null) problems.Add("Navisworks Manage not found: needed to read the model for isometric and GA checks.");

        return new
        {
            version = Updates.CurrentVersion,
            user = new { who.Name, who.EmployeeCode, who.Company, role = me.Role, complete = who.IsComplete },
            sharedFolder = shared,
            activeProject = project is null ? null : new { project.Name, project.Client, project.ProjectNo, project.ModelFile, project.InputsFolder, project.RulePacks },
            inputs = InputsSummary(project, problems),
            navisworks = nav is null ? null : new { nav.Name, nav.Root },
            update = new { available = update.Available, latest = update.Latest?.Version, error = update.Error },
            ready = problems.Count == 0,
            problems,
        };
    }
}

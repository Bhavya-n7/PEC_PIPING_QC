using System.ComponentModel;
using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Mcp.Tools;

/// <summary>The Navisworks model as data: export it once to a shared snapshot, then look lines up and check drawings against it.</summary>
[McpServerToolType]
public static class ModelTools
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ModelSnapshot Snap)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The snapshot of the project's model (the named one, or the newest), loaded once and reused until the file changes.</summary>
    internal static (ModelSnapshot Snap, StoredModel Stored, ProjectCard? Project) Load(string? projectName, string? modelName)
    {
        var card = string.IsNullOrWhiteSpace(projectName) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new McpException($"There is no project named '{projectName}'.");
        var store = new ModelStore(Resolve.Settings());
        var stored = store.Find(card?.Name ?? "", modelName)
            ?? throw new McpException(string.IsNullOrWhiteSpace(modelName)
                ? $"There is no exported model for project '{card?.Name ?? "(none)"}' yet. Export it once with model_export (needs Navisworks on a PC that has it); the team then shares the result."
                : $"There is no exported model '{modelName}' for project '{card?.Name ?? "(none)"}'. model_summary lists what is there.");
        var stamp = File.GetLastWriteTimeUtc(stored.Path);
        if (Cache.TryGetValue(stored.Path, out var c) && c.Stamp == stamp) return (c.Snap, stored, card);
        ModelSnapshot snap;
        try { snap = ModelSnapshot.Load(stored.Path); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { throw new McpException($"The exported model {stored.Name} cannot be read ({e.Message}). Export it again with model_export."); }
        Cache[stored.Path] = (stamp, snap);
        return (snap, stored, card);
    }

    [McpServerTool(Name = "model_export", Destructive = true)]
    [Description("Export the project's Navisworks model (an AVEVA E3D export, .nwd) to a shared snapshot of every pipe, branch and component with its attributes, so drawings can be checked against it without Navisworks. Needs Navisworks Manage on this PC; it runs hidden on a COPY of the model (the open Navisworks and the original file are not touched, nothing is saved) and takes about half a minute to a few minutes. The snapshot goes to the shared models folder; an earlier snapshot of the same model is kept. Do it once per model revision: model_summary shows whether the snapshot is older than the model. apply=false shows the plan; apply=true asks the user to approve and exports.")]
    public static object Export([Description("The .nwd file; empty = the model file of the active project")] string? modelFile = null, [Description("Project name; empty = the active project")] string? project = null, bool apply = false,
        [Description("Minutes to wait for Navisworks, default 15")] int timeoutMinutes = 15)
    {
        var card = string.IsNullOrWhiteSpace(project) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new McpException($"There is no project named '{project}'.");
        var file = (string.IsNullOrWhiteSpace(modelFile) ? card?.ModelFile : modelFile)?.Trim().Trim('"') ?? "";
        if (file.Length == 0) throw new McpException("Say which model to export: give modelFile, or set the Navisworks model on the project card.");
        if (!File.Exists(file)) throw new McpException($"The model file does not exist: {file}");
        var settings = Resolve.Settings();
        var navis = Resolve.Navisworks();
        var store = new ModelStore(settings);
        var target = store.PathFor(card?.Name ?? "", file);
        var existing = File.Exists(target) ? File.GetLastWriteTime(target).ToString("yyyy-MM-dd HH:mm") : null;
        var tools = NavisExportRunner.FindToolsFolder();
        var problems = new List<string>();
        if (navis is null) problems.Add("Navisworks Manage was not found on this PC. Run the export on a PC that has it (or set the Navisworks folder in Settings).");
        if (tools is null) problems.Add("This installation has no Navisworks export tool (the 'navis' folder is missing): install the latest version of the QC Assistant.");
        var sizeMb = new FileInfo(file).Length / 1048576.0;
        if (!apply || problems.Count > 0)
            return new
            {
                exported = false, model = file, sizeMb = Math.Round(sizeMb, 1), wouldWrite = target, earlierSnapshot = existing, problems,
                next = problems.Count > 0 ? "Tell the user what is missing." : "Tell the user it takes about half a minute to a few minutes, then call again with apply=true to export.",
            };
        var canonical = $"{card?.Name}|{Path.GetFullPath(file).ToLowerInvariant()}|{new FileInfo(file).Length}";
        if (Resolve.NeedApproval("model_export", canonical, $"Export Navisworks model {Path.GetFileName(file)} ({sizeMb:0} MB) to the shared snapshot", $"Model: {file}\nWrites: {target}\nNavisworks runs hidden on a copy; nothing in the model is changed or saved.") is { } wait) return wait;

        var tmp = Path.Combine(Path.GetTempPath(), "pecqc_export_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var r = NavisExportRunner.Export(navis!, file, tmp, TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes, 1, 60)));
            if (!r.Ok) throw new McpException("The export failed: " + r.Message);
            ModelSnapshot snap;
            try { snap = ModelSnapshot.Load(tmp); }
            catch (Exception e) { throw new McpException("Navisworks wrote a file that could not be read (" + e.Message + "). Nothing was saved."); }
            if (snap.Lines.Count == 0)
                throw new McpException($"The export found no pipes in the model ({snap.TotalItems} items read). Is this an AVEVA E3D export with PIPE items? Nothing was saved.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var kept = ModelStore.KeepOld(target);
            File.Copy(tmp, target, true);
            var audit = Resolve.Audit("model_export", canonical, $"{snap.Lines.Count} lines, {snap.TotalItems} items");
            Cache.TryRemove(target, out _);
            return new
            {
                exported = true, snapshot = target, earlierKept = kept, lines = snap.Lines.Count, nozzles = snap.Nozzles.Count, items = snap.TotalItems,
                duplicatedLines = snap.Duplicates.Count, seconds = (int)r.Elapsed.TotalSeconds, audit, next = "Tell the user the model is exported and how many lines it holds.",
            };
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [McpServerTool(Name = "model_summary", ReadOnly = true)]
    [Description("What exported Navisworks models the project has and what is in the newest (or the named) one: when it was exported, from which file, how many lines and nozzles, pipes that occur twice in the model tree, and whether the snapshot is older than the model file. Use it before checking drawings against the model.")]
    public static object Summary([Description("Project name; empty = the active project")] string? project = null, [Description("A model name from the list; empty = the newest")] string? model = null)
    {
        var card = string.IsNullOrWhiteSpace(project) ? Resolve.ActiveProject() : new ProjectStore().All.FirstOrDefault(p => p.Name.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new McpException($"There is no project named '{project}'.");
        var store = new ModelStore(Resolve.Settings());
        var all = store.List(card?.Name ?? "");
        if (all.Count == 0)
            return new { exported = false, models = Array.Empty<object>(), projectModelFile = card?.ModelFile ?? "", next = "No model is exported for this project. Use model_export (on a PC with Navisworks), or ask a colleague who has done it." };
        var (snap, stored, _) = Load(project, model);
        string? stale = null;
        var src = card?.ModelFile ?? "";
        if (src.Length > 0 && File.Exists(src) && File.GetLastWriteTime(src) > stored.Written)
            stale = $"The model file {Path.GetFileName(src)} is newer ({File.GetLastWriteTime(src):yyyy-MM-dd HH:mm}) than this snapshot ({stored.Written:yyyy-MM-dd HH:mm}): export it again before relying on it.";
        var dups = snap.Duplicates.Select(d => new { line = d.Key, d.Value.Count, d.Value.Identical, d.Value.SameFile }).Take(25).ToList();
        var types = snap.Lines.Values.SelectMany(l => l.Items).GroupBy(i => i.Type).OrderByDescending(g => g.Count()).Take(15).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            exported = true, models = all.Select(m => new { m.Name, written = m.Written.ToString("yyyy-MM-dd HH:mm"), mb = Math.Round(m.Bytes / 1048576.0, 1) }).ToList(),
            usedModel = stored.Name, snapshot = stored.Path, exportedAt = snap.Exported, sourceModel = snap.Source, lines = snap.Lines.Count, nozzles = snap.Nozzles.Count, itemsInModel = snap.TotalItems,
            componentTypes = types, duplicatedLines = dups, staleWarning = stale,
            next = "Give the user the counts. Mention the duplicated lines and the stale warning if there are any.",
        };
    }

    [McpServerTool(Name = "model_line", ReadOnly = true)]
    [Description("Look up one pipe in the exported model by its line number (any spelling of it): the pipe's attributes (bore, pressure, temperature, class, test pressure, P&ID, insulation...), its branches with head/tail positions and connections, and the components along each branch in order with their types, spec references and positions. Positions are in mm. Use it to answer a question about the model or to see why a check failed.")]
    public static object Line([Description("The line number, e.g. 2\"-CD-60A07900-B400A-N-HS-017")] string line, [Description("Project name; empty = the active project")] string? project = null,
        [Description("A model name; empty = the newest")] string? model = null, [Description("Include every component with its attributes (long); default only counts and the list in order")] bool full = false)
    {
        var (snap, stored, _) = Load(project, model);
        var l = snap.Find(line);
        if (l is null)
        {
            var q = ModelSnapshot.NormLine(line);
            var near = snap.Lines.Keys.Where(k => k.Contains(q, StringComparison.Ordinal) || (q.Length > 6 && q.Contains(k, StringComparison.Ordinal))).Take(10).ToList();
            if (near.Count == 0) { var parts = q.Split('-'); if (parts.Length > 2) near = snap.Lines.Keys.Where(k => k.Contains(parts[2], StringComparison.Ordinal)).Take(10).ToList(); }
            return new { found = false, line, model = stored.Name, similar = near, next = "Say the line is not in the exported model; offer the similar names if there are any. If the model is old, it may need a new export." };
        }
        snap.Duplicates.TryGetValue(l.Name, out var dup);
        return new
        {
            found = true, line = "/" + l.Name, model = stored.Name, treePath = l.Path, attributes = l.Attrs, duplicated = dup is null ? null : new { dup.Count, dup.Identical, dup.SameFile, dup.Paths },
            pipeLengthMm = Math.Round(ModelSnapshot.PipeLengthMm(l), 1),
            branches = l.Branches.Select(b => new
            {
                name = b.ShortName, attributes = b.Attrs.Where(k => k.Key is "HPOS" or "TPOS" or "HDIR" or "TDIR" or "HREF" or "TREF" or "HBOR" or "TBOR" or "HCON" or "TCON" or "PSPE" or "TEMP" or "PRES" or "TPRESS").ToDictionary(k => k.Key, k => k.Value),
                componentCounts = b.Items.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.Count()),
                components = b.Items.Select(i => full ? (object)new { i.Type, i.Name, i.Attrs } : new { i.Type, name = i.Name.Split(" of ")[0], spec = i.Spec, pos = i.Attr("POS") }).ToList(),
            }).ToList(),
        };
    }
}

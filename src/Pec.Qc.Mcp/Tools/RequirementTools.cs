using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Requirements;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class RequirementTools
{
    private static QcType Type(string? t) => QcTypes.Parse(t) ?? throw new McpException("qcType must be iso, pid or ga.");

    [McpServerTool(Name = "client_requirements", ReadOnly = true)]
    [Description("The client's requirement packs: with no client, the list of packs (client, rules, candidates, whether it is only an example); with a client, its rules, each with the document and section or page it comes from. Rules marked 'candidate' are probably right but not confirmed: a broken candidate is a REVIEW, never a FAIL. A 'pattern' rule can be tested with requirements_check; a 'manual' rule is for you to apply by judgement. Quote the rule's source when you use a rule.")]
    public static object Get([Description("Client name; empty = list the packs")] string? client = null, [Description("Only rules for this QC type: iso, pid or ga (rules for 'all' are always included)")] string? qcType = null)
    {
        var store = RequirementStore.From(Resolve.Settings());
        if (string.IsNullOrWhiteSpace(client))
        {
            var list = store.List();
            return new { folder = store.Directory_, packs = list, note = list.Count == 0 ? "No requirement pack has been saved yet. A QC lead saves one with save_requirements, from a client document, with the source of every rule." : "Name a client to read its rules." };
        }
        var pack = store.Load(client) ?? throw new McpException($"There is no requirement pack for '{client}'. Known: {string.Join(", ", store.List().Select(s => s.Client))}.");
        var t = QcTypes.Parse(qcType);
        var rules = pack.AllRules.Where(r => t is null || r.QcType.Equals("all", StringComparison.OrdinalIgnoreCase) || r.QcType.Equals(QcTypes.Code(t.Value), StringComparison.OrdinalIgnoreCase)).ToList();
        return new
        {
            pack.Client, pack.Version, pack.Example, pack.Notes, rules = rules.Count,
            list = rules.Select(r => new { r.Id, r.QcType, r.Check, r.Kind, r.Field, r.Pattern, r.Severity, r.Status, rule = r.Statement, source = r.Source?.ToString() }),
            note = pack.Example ? "This is only an EXAMPLE pack: its rules are not a client's. Do not apply them." : null,
        };
    }

    [McpServerTool(Name = "save_requirements", Destructive = true)]
    [Description("Save a client's requirement pack (a QC lead only). The pack is JSON: {\"client\":\"...\",\"version\":\"...\",\"notes\":\"...\",\"rules\":[{\"id\":\"ISO-001\",\"qcType\":\"iso|pid|ga|all\",\"check\":\"identity\",\"kind\":\"pattern|manual\",\"field\":\"line number\",\"pattern\":\"regex\",\"statement\":\"what the rule says\",\"severity\":\"FAIL|REVIEW\",\"status\":\"confirmed|candidate\",\"source\":{\"document\":\"...\",\"section\":\"4.2\",\"page\":\"17\"}}]}. EVERY rule needs a source: the document and the section or page. Never save a rule that is only a guess; use status candidate for one that is probably right but not confirmed. apply=false shows the validation and the change; apply=true asks the user to approve, keeps a backup of the file it replaces and records the save.")]
    public static object Save([Description("The pack as JSON")] string packJson, bool apply = false)
    {
        if (Resolve.Require(QcRole.Lead, "Saving a client requirement pack") is { } denied) return denied;
        var pack = RequirementStore.Parse(packJson) ?? throw new McpException("The pack is not valid JSON of the shape described.");
        var errors = RequirementValidation.Validate(pack);
        if (errors.Count > 0) throw new McpException("The pack cannot be saved yet:\n- " + string.Join("\n- ", errors));
        var store = RequirementStore.From(Resolve.Settings());
        var preview = store.Save(pack, apply: false);
        if (!apply)
            return new { saved = false, client = pack.Client, rules = pack.AllRules.Count, candidates = pack.AllRules.Count(r => r.Status == "candidate"), file = preview.Path, replacesExisting = File.Exists(preview.Path), changedLines = preview.LinesChanged, diff = preview.Diff, next = "Show the user the rules and their sources, then call again with apply=true." };
        var canonical = JsonSerializer.Serialize(JsonNode.Parse(packJson));
        if (Resolve.NeedApproval("save_requirements", canonical, $"Save client requirements: {pack.Client} ({pack.AllRules.Count} rules)", $"File: {preview.Path}\n\n{preview.Diff}") is { } wait) return wait;
        var done = store.Save(pack, apply: true);
        var audit = Resolve.Audit("save_requirements", canonical, $"saved {pack.Client}: {pack.AllRules.Count} rules");
        return new { saved = true, client = pack.Client, rules = pack.AllRules.Count, file = done.Path, backup = done.BackupPath, audit };
    }

    [McpServerTool(Name = "requirements_check", ReadOnly = true)]
    [Description("Test values against the client's PATTERN rules for a QC type and one field, for example every line number found on an isometric against the client's line number format. Returns findings in the standard form (OK, or FAIL / REVIEW with the rule's own source) that can go straight into qc_report_write, plus the MANUAL rules for that QC type that you must apply by judgement. 'values' is a JSON array of strings, or of {\"subject\":\"sheet 2\",\"value\":\"...\"}.")]
    public static object Check([Description("Client name")] string client, [Description("iso, pid or ga")] string qcType, [Description("The field the values belong to, as the rule names it, e.g. line number")] string field, [Description("JSON array of values")] string values)
    {
        var t = Type(qcType);
        var pack = RequirementStore.From(Resolve.Settings()).Load(client) ?? throw new McpException($"There is no requirement pack for '{client}'.");
        if (pack.Example) throw new McpException("That pack is only an example: its rules are not a client's and are not applied.");
        List<(string, string)> list;
        try
        {
            var arr = JsonNode.Parse(values) as JsonArray ?? throw new McpException("'values' must be a JSON array.");
            list = arr.Select((n, i) => n is JsonObject o ? ((string?)o["subject"] ?? $"value {i + 1}", (string?)o["value"] ?? "") : ($"value {i + 1}", n?.ToString() ?? "")).ToList();
        }
        catch (JsonException e) { throw new McpException("'values' is not valid JSON: " + e.Message); }
        var findings = RequirementChecks.Evaluate(pack, t, field, list);
        var manual = RequirementChecks.ManualRules(pack, t);
        return new
        {
            client = pack.Client, qcType = QcTypes.Code(t), field, tested = list.Count, rulesApplied = findings.Select(f => f.RuleId).Distinct().Count(),
            counts = QcReport.Counts(findings), findings = JsonNode.Parse(FindingJson.ToJson(findings)),
            manualRules = manual.Select(r => new { r.Id, r.Check, rule = r.Statement, source = r.Source?.ToString(), r.Status }),
            note = findings.Count == 0 ? $"No pattern rule in this pack covers the field '{field}' for {QcTypes.Label(t)}. That is not a pass: nothing was tested." : null,
        };
    }

    [McpServerTool(Name = "qc_checklist", ReadOnly = true)]
    [Description("What a kind of QC check covers: the checklist for the isometric, P&ID or GA check (each item: id, what to verify, what to verify it against), the project inputs it needs and which of them the active project has, and the client's manual rules when a client is named. Use it to plan a check and to say, at the end, which items were done and which were not.")]
    public static object Checklist([Description("iso, pid or ga")] string qcType, [Description("Client name, to add that client's manual rules")] string? client = null, [Description("Project name; empty = the active project")] string? project = null)
    {
        var t = Type(qcType);
        var s = Resolve.Settings();
        var items = QcChecklists.For(t, s.EffectiveRequirementsDirectory, out var source);
        object? coverage = null;
        try { var (lib, card) = Resolve.Inputs(project); coverage = new { project = card.Name, inputs = lib.Coverage(t).Select(c => new { kind = c.Kind.ToString(), c.Present, c.Count, neededFor = c.Why }) }; }
        catch (McpException e) { coverage = new { problem = e.Message }; }
        var manual = string.IsNullOrWhiteSpace(client) ? null : RequirementStore.From(s).Load(client) is { } p ? RequirementChecks.ManualRules(p, t).Select(r => new { r.Id, rule = r.Statement, source = r.Source?.ToString(), r.Status }).ToList() : null;
        return new { qcType = QcTypes.Code(t), title = QcTypes.Label(t), source, items = items.Select(i => new { i.Id, i.Check, verify = i.Text, against = i.Against }), projectInputs = coverage, clientManualRules = manual };
    }
}

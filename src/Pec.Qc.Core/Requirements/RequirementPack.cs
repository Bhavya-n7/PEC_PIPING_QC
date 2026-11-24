using System.Text.Json;
using System.Text.RegularExpressions;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Core.Requirements;

/// <summary>Where a client rule comes from: the document and the section or page in it. A rule without one is not accepted.</summary>
public sealed record RuleSource(string Document = "", string Section = "", string Page = "")
{
    public override string ToString() => string.Join(", ", new[] { Document, Section.Length > 0 ? "section " + Section : "", Page.Length > 0 ? "page " + Page : "" }.Where(x => x.Length > 0));
}

/// <summary>
/// One client rule. <c>pattern</c> rules can be checked by the tools (a regular expression the value of a field must match, for example the line number format);
/// <c>manual</c> rules are for the person or for Claude's judgement and are listed as review items. A <c>candidate</c> is a rule that is probably right but not confirmed:
/// when it is broken the result is REVIEW, never FAIL.
/// </summary>
public sealed record RequirementRule(string Id, string QcType, string Check, string Statement, string Kind = "manual", string Field = "", string Pattern = "",
    string Severity = "FAIL", string Status = "confirmed", RuleSource? Source = null);

public sealed record RequirementPack(string Client, string Version = "", bool Example = false, string Notes = "", IReadOnlyList<RequirementRule>? Rules = null)
{
    public IReadOnlyList<RequirementRule> AllRules => Rules ?? [];
}

public sealed record RequirementSummary(string Client, string File, int Rules, int Candidates, bool Example, string Version);

public static class RequirementValidation
{
    public static readonly string[] Kinds = ["pattern", "manual"], Severities = ["FAIL", "REVIEW"], Statuses = ["confirmed", "candidate"], QcCodes = ["iso", "pid", "ga", "all"];

    /// <summary>Everything wrong with a pack, one line each. Empty when it can be saved.</summary>
    public static List<string> Validate(RequirementPack pack)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(pack.Client)) errors.Add("The pack needs a client name.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in pack.AllRules)
        {
            var n = string.IsNullOrWhiteSpace(r.Id) ? "(rule without an id)" : r.Id;
            if (string.IsNullOrWhiteSpace(r.Id)) errors.Add("A rule has no id.");
            else if (!ids.Add(r.Id)) errors.Add($"{n}: the id is used twice.");
            if (string.IsNullOrWhiteSpace(r.Statement)) errors.Add($"{n}: the statement (what the rule says) is empty.");
            if (!QcCodes.Contains((r.QcType ?? "").ToLowerInvariant())) errors.Add($"{n}: qcType must be one of {string.Join(", ", QcCodes)}.");
            if (!Kinds.Contains((r.Kind ?? "").ToLowerInvariant())) errors.Add($"{n}: kind must be pattern or manual.");
            if (!Severities.Contains((r.Severity ?? "").ToUpperInvariant())) errors.Add($"{n}: severity must be FAIL or REVIEW.");
            if (!Statuses.Contains((r.Status ?? "").ToLowerInvariant())) errors.Add($"{n}: status must be confirmed or candidate.");
            if (r.Source is null || string.IsNullOrWhiteSpace(r.Source.Document) || (string.IsNullOrWhiteSpace(r.Source.Section) && string.IsNullOrWhiteSpace(r.Source.Page)))
                errors.Add($"{n}: a source is required: the document and the section or page the rule comes from. Never save a rule that is only a guess.");
            if (string.Equals(r.Kind, "pattern", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(r.Field)) errors.Add($"{n}: a pattern rule needs the field it checks (for example 'line number').");
                try { _ = new Regex(r.Pattern ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)); if (string.IsNullOrEmpty(r.Pattern)) errors.Add($"{n}: a pattern rule needs a pattern."); }
                catch (ArgumentException e) { errors.Add($"{n}: the pattern is not a valid regular expression ({e.Message})."); }
            }
        }
        return errors;
    }
}

/// <summary>The client requirement files of the shared folder (<c>requirements\&lt;client&gt;.json</c>).</summary>
public sealed class RequirementStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string Directory_ { get; } = directory;

    public static RequirementStore From(Settings s) => new(s.EffectiveRequirementsDirectory);

    public string FileFor(string client) => Path.Combine(Directory_, ProjectStore.SafeName(client) + ".json");

    public static RequirementPack? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<RequirementPack>(json, Json); }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<RequirementSummary> List()
    {
        var list = new List<RequirementSummary>();
        if (!System.IO.Directory.Exists(Directory_)) return list;
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory_, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(f).Equals("qc-checklists.json", StringComparison.OrdinalIgnoreCase)) continue;      // the checklists are not a client pack
            try
            {
                var p = Parse(File.ReadAllText(f));
                if (p is not null && p.Client.Length > 0) list.Add(new RequirementSummary(p.Client, Path.GetFileName(f), p.AllRules.Count, p.AllRules.Count(r => r.Status == "candidate"), p.Example, p.Version));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* an unreadable file is skipped */ }
        }
        return list;
    }

    public RequirementPack? Load(string client)
    {
        var direct = FileFor(client);
        try
        {
            if (File.Exists(direct)) return Parse(File.ReadAllText(direct));
            var match = List().FirstOrDefault(s => s.Client.Equals(client.Trim(), StringComparison.OrdinalIgnoreCase));
            return match is null ? null : Parse(File.ReadAllText(Path.Combine(Directory_, match.File)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string ToText(RequirementPack p) => JsonSerializer.Serialize(p, Json);

    /// <summary>Preview (apply=false) or save with a diff and a timestamped backup of the file it replaces. The pack must be valid.</summary>
    public FileChange Save(RequirementPack pack, bool apply)
    {
        var errors = RequirementValidation.Validate(pack);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        System.IO.Directory.CreateDirectory(Directory_);
        return SafeWrite.WriteText(FileFor(pack.Client), ToText(pack), apply);
    }
}

public static class RequirementChecks
{
    private static bool Applies(RequirementRule r, QcType t) => r.QcType.Equals("all", StringComparison.OrdinalIgnoreCase) || r.QcType.Equals(QcTypes.Code(t), StringComparison.OrdinalIgnoreCase);

    private static string Key(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Checks values of one field (for example every line number found on the drawings) against the pattern rules of the pack that apply to the QC type.
    /// A value that matches is OK; one that does not is FAIL or REVIEW as the rule says (always REVIEW for a candidate rule), with the rule's own source.
    /// </summary>
    public static List<Finding> Evaluate(RequirementPack pack, QcType type, string field, IEnumerable<(string Subject, string Value)> values)
    {
        var rules = pack.AllRules.Where(r => r.Kind.Equals("pattern", StringComparison.OrdinalIgnoreCase) && Applies(r, type) && Key(r.Field) == Key(field)).ToList();
        var result = new List<Finding>();
        var list = values.ToList();
        foreach (var r in rules)
        {
            var rx = new Regex(r.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            var src = new SourceRef(r.Source?.Document ?? "", string.Join(", ", new[] { r.Source?.Section is { Length: > 0 } s ? "section " + s : "", r.Source?.Page is { Length: > 0 } p ? "page " + p : "" }.Where(x => x.Length > 0)), r.Id);
            foreach (var (subject, value) in list)
            {
                bool ok;
                try { ok = rx.IsMatch(value ?? ""); } catch (RegexMatchTimeoutException) { ok = false; }
                var broken = r.Severity.Equals("REVIEW", StringComparison.OrdinalIgnoreCase) || r.Status.Equals("candidate", StringComparison.OrdinalIgnoreCase) ? FindingStatus.Review : FindingStatus.Fail;
                result.Add(new Finding(r.Check, ok ? FindingStatus.Ok : broken, subject, ok ? r.Statement : r.Statement + (r.Status.Equals("candidate", StringComparison.OrdinalIgnoreCase) ? " (candidate rule: not confirmed)" : ""),
                    value ?? "", "must match " + r.Pattern, src, "", r.Id));
            }
        }
        return result;
    }

    /// <summary>The rules for a person (or Claude's judgement) to apply: they cannot be tested by a pattern.</summary>
    public static IReadOnlyList<RequirementRule> ManualRules(RequirementPack pack, QcType type) =>
        pack.AllRules.Where(r => r.Kind.Equals("manual", StringComparison.OrdinalIgnoreCase) && Applies(r, type)).ToList();
}

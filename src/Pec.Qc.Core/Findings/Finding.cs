using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pec.Qc.Core.Findings;

public enum FindingStatus { Ok = 0, Info = 1, Review = 2, Fail = 3 }

public static class FindingStatuses
{
    public static string Code(FindingStatus s) => s switch { FindingStatus.Fail => "FAIL", FindingStatus.Review => "REVIEW", FindingStatus.Info => "INFO", _ => "OK" };

    public static FindingStatus? Parse(string? s) => (s ?? "").Trim().ToUpperInvariant() switch
    {
        "FAIL" or "FAILED" or "ERROR" => FindingStatus.Fail,
        "REVIEW" or "CHECK" or "UNDECIDED" => FindingStatus.Review,
        "INFO" or "NOTE" => FindingStatus.Info,
        "OK" or "PASS" or "PASSED" => FindingStatus.Ok,
        _ => null,
    };
}

/// <summary>Where the source value came from: the document, the page / sheet / section in it, and the item (a row, a tag, a clause).</summary>
public sealed record SourceRef(string Document, string Page = "", string Item = "")
{
    public override string ToString() => string.Join(", ", new[] { Document, Page, Item }.Where(x => x.Length > 0));
}

/// <summary>
/// One result of a check, the same for isometrics, P&amp;IDs and GA drawings. FAIL = the drawing and the source disagree (both values shown, with the source's document and
/// page); REVIEW = it cannot be decided (a value is missing or unclear: a person looks); OK = they agree; INFO = something that cannot be verified. Nothing here
/// asserts a difference without the evidence for it.
/// </summary>
public sealed record Finding(string Check, FindingStatus Status, string Subject, string Message, string DrawingValue = "", string SourceValue = "",
    SourceRef? Source = null, string Location = "", string RuleId = "");

public static class FindingJson
{
    /// <summary>
    /// Reads findings from JSON (an array, or an object with a "findings" array). Property names are not case sensitive. Every problem is listed with the finding number,
    /// so the caller can fix and resend: a FAIL needs the subject, both values and the source document; a REVIEW needs the subject and the reason.
    /// </summary>
    public static List<Finding> Parse(string json, out List<string> errors)
    {
        errors = [];
        var list = new List<Finding>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException e) { errors.Add("The findings are not valid JSON: " + e.Message); return list; }
        var arr = root as JsonArray ?? (root as JsonObject)?["findings"] as JsonArray ?? (root as JsonObject)?["Findings"] as JsonArray;
        if (arr is null) { errors.Add("Give a JSON array of findings (or an object with a \"findings\" array)."); return list; }
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject o) { errors.Add($"Finding {i + 1}: not an object."); continue; }
            string Get(params string[] names)
            {
                foreach (var n in names)
                    foreach (var kv in o) if (string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase) && kv.Value is not null) return kv.Value.ToString().Trim();
                return "";
            }
            var status = FindingStatuses.Parse(Get("status"));
            var n1 = $"Finding {i + 1}";
            if (status is null) { errors.Add($"{n1}: status must be OK, REVIEW, FAIL or INFO."); continue; }
            var f = new Finding(Get("check", "category"), status.Value, Get("subject", "item", "tag", "line"), Get("message", "text", "description"),
                Get("drawing", "drawingValue", "drawing_value"), Get("source", "sourceValue", "source_value", "expected"),
                new SourceRef(Get("sourceDoc", "sourceDocument", "source_document", "document"), Get("sourcePage", "source_page", "page"), Get("sourceItem", "source_item")),
                Get("location", "where"), Get("ruleId", "rule_id", "rule"));
            if (f.Message.Length == 0) errors.Add($"{n1}: a message is needed.");
            if (f.Status is FindingStatus.Fail or FindingStatus.Review && f.Subject.Length == 0) errors.Add($"{n1}: a {FindingStatuses.Code(f.Status)} needs the subject (the line, tag or item it is about).");
            if (f.Status == FindingStatus.Fail)
            {
                if (f.DrawingValue.Length == 0) errors.Add($"{n1}: a FAIL needs the value on the drawing.");
                if (f.SourceValue.Length == 0) errors.Add($"{n1}: a FAIL needs the value in the source. If the source does not say, it is a REVIEW.");
                if (f.Source is null || f.Source.Document.Length == 0) errors.Add($"{n1}: a FAIL needs the source document (and the page or item) the source value came from.");
            }
            list.Add(f);
        }
        return list;
    }

    public static string ToJson(IEnumerable<Finding> findings) => JsonSerializer.Serialize(findings.Select(f => new
    {
        check = f.Check, status = FindingStatuses.Code(f.Status), subject = f.Subject, message = f.Message, drawing = f.DrawingValue, source = f.SourceValue,
        sourceDoc = f.Source?.Document ?? "", sourcePage = f.Source?.Page ?? "", sourceItem = f.Source?.Item ?? "", location = f.Location, ruleId = f.RuleId,
    }), new JsonSerializerOptions { WriteIndented = true });
}

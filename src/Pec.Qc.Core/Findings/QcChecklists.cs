using System.Text.Json;

namespace Pec.Qc.Core.Findings;

/// <summary>One thing to verify in a kind of QC check.</summary>
public sealed record ChecklistItem(string Id, string Check, string Text, string Against);

/// <summary>
/// What each kind of QC check covers: the default list written from QCTYPES.MD and the QC procedure. It lists WHAT to verify, never an engineering limit, so it carries no
/// client data. The QC lead can replace it by putting <c>qc-checklists.json</c> in the shared requirements folder (same shape: {"iso":[...],"pid":[...],"ga":[...]}).
/// </summary>
public static class QcChecklists
{
    public const string FileName = "qc-checklists.json";
    public const string DefaultSource = "PEC QC checklist (default list from QCTYPES.MD; the QC lead can replace it)";

    public static IReadOnlyDictionary<QcType, IReadOnlyList<ChecklistItem>> Defaults { get; } = new Dictionary<QcType, IReadOnlyList<ChecklistItem>>
    {
        [QcType.Isometric] =
        [
            new("ISO-01", "identity", "Line number on every sheet equals the model's line number and the line list entry", "model, line list"),
            new("ISO-02", "identity", "Piping class, size and fluid code match the line list and the PMS", "line list, PMS"),
            new("ISO-03", "title-block", "Design, operating and test pressure and temperature, test medium, insulation, paint and PWHT match the line list", "line list"),
            new("ISO-04", "title-block", "The P&ID number on the isometric matches the line list and the P&ID shows this line", "line list, P&ID"),
            new("ISO-05", "bom", "Quantities and sizes of every component type (pipe, fittings, flanges, valves, supports) match the model, summed over all sheets", "model"),
            new("ISO-06", "pms", "Component descriptions, material, rating, schedule and end connections match the PMS for the class and size", "PMS"),
            new("ISO-07", "pms", "Flange, gasket and bolt items match the PMS (rating, facing, stud length)", "PMS"),
            new("ISO-08", "coordinates", "Start, end and continuation coordinates and the continuation line numbers match the model", "model"),
            new("ISO-09", "lengths", "Cut pipe lengths add up to the pipe quantity and the total agrees with the model", "model"),
            new("ISO-10", "supports", "Support tags and positions match the model and the support standard", "model"),
            new("ISO-11", "vendor", "Valves and special items match the vendor data (type, rating, face to face, weight)", "vendor data"),
            new("ISO-12", "drawing", "Sheet numbering, revision, date and the other title-block fields are complete and consistent", "drawing"),
            new("ISO-13", "drawing", "Notes, hold items and general notes are present and correct", "drawing"),
        ],
        [QcType.Pid] =
        [
            new("PID-01", "lines", "Every line number on the original P&ID is on the redrawn one with the same size, class and fluid", "original P&ID"),
            new("PID-02", "equipment", "Every equipment tag, name and rating on the original is on the redrawn one", "original P&ID"),
            new("PID-03", "instruments", "Every instrument tag, type and loop on the original is on the redrawn one", "original P&ID"),
            new("PID-04", "valves", "Every valve (tag, type, size, fail position) and every line break on the original is on the redrawn one", "original P&ID"),
            new("PID-05", "notes", "Notes, hold items, legends and the revision block are copied accurately", "original P&ID"),
            new("PID-06", "extra", "Nothing appears on the redrawn P&ID that is not on the original", "original P&ID"),
            new("PID-07", "connectivity", "Line directions, connections and off-page connectors agree with the original", "original P&ID"),
        ],
        [QcType.Ga] =
        [
            new("GA-01", "equipment", "Equipment tags and positions match the model", "model"),
            new("GA-02", "dimensions", "Dimensions, elevations and coordinates match the model", "model"),
            new("GA-03", "vendor", "Equipment sizes and nozzle positions and orientations match the vendor data", "vendor data"),
            new("GA-04", "tags", "Tags and services agree with the P&IDs", "P&ID"),
            new("GA-05", "drawing", "North arrow, scale, grid and the title block are complete", "drawing"),
            new("GA-06", "drawing", "Notes, revision and issue status are present and correct", "drawing"),
            new("GA-07", "clearances", "Access and clearances are adequate (a judgement for the engineer)", "drawing, model"),
        ],
    };

    /// <summary>The checklist for a QC type: the shared file when it exists and has that type, else the default.</summary>
    public static IReadOnlyList<ChecklistItem> For(QcType type, string? requirementsDirectory, out string source)
    {
        source = DefaultSource;
        try
        {
            var file = requirementsDirectory is null ? null : Path.Combine(requirementsDirectory, FileName);
            if (file is not null && File.Exists(file))
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, List<ChecklistItem>>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc is not null && doc.TryGetValue(QcTypes.Code(type), out var items) && items.Count > 0) { source = "shared requirements/" + FileName; return items; }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* a broken file falls back to the default */ }
        return Defaults[type];
    }
}

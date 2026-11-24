using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Iso;

/// <summary>
/// Tolerances and the mappings the isometric check needs. The defaults are what the first project used. The support map (which model attachment is which iso support
/// code) is NOT guessed: it is empty until the QC lead adds it from the client's support collection, and a support that is not in it is reported for a person to look at.
/// The QC lead overrides any value with <c>iso-rules.json</c> in the shared requirements folder.
/// </summary>
public sealed record IsoRules
{
    public const string FileName = "iso-rules.json";

    public double CoordTolMm { get; init; } = 2.0;
    public double PressureTolPsi { get; init; } = 0.5;
    public double TempTolF { get; init; } = 1.0;
    public double CutSumTolIn { get; init; } = 0.0625;
    public double PipeLengthTolIn { get; init; } = 1.0;
    /// <summary>A coordinate this close to the line's own start / end but outside the tolerance is reported as wrong, not as unknown.</summary>
    public double EndpointNearMm { get; init; } = 50.0;
    /// <summary>Model attachment spec reference to the iso support code (e.g. "/SUPP/ATTA-119": "WE04").</summary>
    public IReadOnlyDictionary<string, string> SupportMap { get; init; } = new Dictionary<string, string>();
    /// <summary>Regular expressions (matched from the start) of attachment spec references that are not BOM supports: flow arrows, dummy legs, points.</summary>
    public IReadOnlyList<string> IgnoreAttaSpecs { get; init; } = [@"^/[A-Z]\d{3}[A-Z]/ATTA-\d+$"];

    public bool IsIgnoredAtta(string spec) => IgnoreAttaSpecs.Any(p => Regex.IsMatch(spec, p));

    /// <summary>The rules: defaults, with the values of iso-rules.json (when it exists in the folder) over them. <paramref name="source"/> says which.</summary>
    public static IsoRules Load(string? requirementsDirectory, out string source)
    {
        source = "built-in defaults";
        var file = requirementsDirectory is null ? null : Path.Combine(requirementsDirectory, FileName);
        if (file is null || !File.Exists(file)) return new IsoRules();
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
            if (o is null) { source = $"built-in defaults ({FileName} is not a JSON object)"; return new IsoRules(); }
            JsonNode? Get(params string[] names) => o.FirstOrDefault(kv => names.Any(n => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase))).Value;
            double D(double dflt, params string[] names) => Get(names) is JsonValue v && v.TryGetValue<double>(out var d) ? d : dflt;
            var def = new IsoRules();
            var map = new Dictionary<string, string>();
            if (Get("supportMap", "support_map") is JsonObject sm) foreach (var kv in sm) if (kv.Value is not null) map[kv.Key] = kv.Value.ToString();
            var ign = Get("ignoreAttaSpecs", "ignore_atta_specs") is JsonArray ia ? ia.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : def.IgnoreAttaSpecs.ToList();
            source = file;
            return new IsoRules
            {
                CoordTolMm = D(def.CoordTolMm, "coordTolMm", "coord_tol_mm"), PressureTolPsi = D(def.PressureTolPsi, "pressureTolPsi", "pressure_tol_psi"),
                TempTolF = D(def.TempTolF, "tempTolF", "temp_tol_f"), CutSumTolIn = D(def.CutSumTolIn, "cutSumTolIn", "cut_sum_tol_in"),
                PipeLengthTolIn = D(def.PipeLengthTolIn, "pipeLengthTolIn", "pipe_length_tol_in"), EndpointNearMm = D(def.EndpointNearMm, "endpointNearMm", "endpoint_near_mm"),
                SupportMap = map, IgnoreAttaSpecs = ign,
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            source = $"built-in defaults ({FileName} could not be read: {e.Message})";
            return new IsoRules();
        }
    }
}

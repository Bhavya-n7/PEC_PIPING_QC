using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Tools;

/// <summary>A value the user gives when running the tool. <c>{{name}}</c> in a step's arguments is replaced by it.</summary>
public sealed record CustomToolParam(string Name, string Description, string Type = "string", bool Required = true, string? Default = null);

/// <summary>One call of an existing tool. Argument values may contain <c>{{parameter}}</c>.</summary>
public sealed record CustomToolStep(string Tool, JsonObject Args, string? Note = null);

/// <summary>
/// A custom tool is a named sequence of the assistant's own tools with parameters (a recipe). It contains no code, so a tool made on
/// one PC is safe to run on every PC, and each step still goes through the same checks and approvals as when Claude calls it.
/// </summary>
public sealed record CustomToolDef(
    string Name, string Description, IReadOnlyList<CustomToolParam> Parameters, IReadOnlyList<CustomToolStep> Steps,
    string Author, string Machine, DateTime Created, int Revision = 1);

public sealed record SyncReport(int ToShared, int FromShared, bool SharedReachable);

public sealed class ToolRegistry
{
    public const int MaxSteps = 12;
    /// <summary>Tools a custom tool may not call: they would create tools, or loop.</summary>
    public static readonly string[] Forbidden = ["run_custom_tool", "create_custom_tool", "save_skill", "forget", "remember"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _shared;
    private readonly string _local;

    public ToolRegistry(string sharedDir, string localDir) { _shared = sharedDir; _local = localDir; }

    public static ToolRegistry From(Settings s) => new(s.EffectiveToolsDirectory, s.LocalToolsDirectory);

    // ---- storage: the user's own copy always; the shared copy when the folder is reachable

    public IReadOnlyList<CustomToolDef> List()
    {
        var byName = new Dictionary<string, (CustomToolDef Def, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { _local, _shared })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                var def = Read(f);
                if (def is null) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (!byName.TryGetValue(def.Name, out var have) || t > have.Time) byName[def.Name] = (def, t);
            }
        }
        return byName.Values.Select(v => v.Def).OrderBy(d => d.Name).ToList();
    }

    public CustomToolDef? Get(string name) => List().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves to the user's copy, and to the shared folder when it can be reached. Returns where it was written.</summary>
    public (bool Local, bool Shared) Save(CustomToolDef def)
    {
        var text = JsonSerializer.Serialize(def, Json);
        Directory.CreateDirectory(_local);
        File.WriteAllText(Path.Combine(_local, def.Name + ".json"), text);
        var shared = false;
        try { Directory.CreateDirectory(_shared); File.WriteAllText(Path.Combine(_shared, def.Name + ".json"), text); shared = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (true, shared);
    }

    /// <summary>Copies the newer file in both directions, so tools made offline reach the shared folder and new shared tools reach this PC.</summary>
    public SyncReport Sync()
    {
        int up = 0, down = 0;
        try { Directory.CreateDirectory(_shared); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new SyncReport(0, 0, false); }
        Directory.CreateDirectory(_local);
        var names = Directory.GetFiles(_local, "*.json").Concat(Directory.GetFiles(_shared, "*.json")).Select(Path.GetFileName).Distinct().OfType<string>();
        foreach (var n in names)
        {
            var l = Path.Combine(_local, n); var s = Path.Combine(_shared, n);
            try
            {
                if (!File.Exists(s)) { File.Copy(l, s); up++; }
                else if (!File.Exists(l)) { File.Copy(s, l); down++; }
                else
                {
                    var lt = File.GetLastWriteTimeUtc(l); var st = File.GetLastWriteTimeUtc(s);
                    if (lt > st.AddSeconds(1)) { File.Copy(l, s, true); up++; }
                    else if (st > lt.AddSeconds(1)) { File.Copy(s, l, true); down++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new SyncReport(up, down, true);
    }

    private static CustomToolDef? Read(string file)
    {
        try { return JsonSerializer.Deserialize<CustomToolDef>(File.ReadAllText(file), Json); }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    // ---- checking a definition

    /// <param name="knownTools">Names of the assistant's built-in tools.</param>
    public static List<string> Validate(CustomToolDef d, IReadOnlySet<string> knownTools)
    {
        var errors = new List<string>();
        if (!Regex.IsMatch(d.Name, @"^[a-z][a-z0-9_]{2,40}$")) errors.Add("Name: lowercase letters, digits and _, 3 to 41 characters, starting with a letter.");
        if (knownTools.Contains(d.Name)) errors.Add($"'{d.Name}' is the name of a built-in tool. Choose another name.");
        if (string.IsNullOrWhiteSpace(d.Description)) errors.Add("A description is needed (when to use the tool).");
        if (d.Steps.Count == 0) errors.Add("At least one step is needed.");
        if (d.Steps.Count > MaxSteps) errors.Add($"At most {MaxSteps} steps.");
        var names = d.Parameters.Select(p => p.Name).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) errors.Add("Parameter names must be different.");
        foreach (var p in d.Parameters)
        {
            if (!Regex.IsMatch(p.Name, @"^[A-Za-z][A-Za-z0-9_]{0,30}$")) errors.Add($"Parameter '{p.Name}': letters, digits and _ only.");
            if (p.Type is not ("string" or "number" or "boolean" or "list")) errors.Add($"Parameter '{p.Name}': type must be string, number, boolean or list.");
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < d.Steps.Count; i++)
        {
            var st = d.Steps[i];
            if (!knownTools.Contains(st.Tool)) errors.Add($"Step {i + 1}: there is no tool '{st.Tool}'.");
            if (Forbidden.Contains(st.Tool)) errors.Add($"Step {i + 1}: '{st.Tool}' cannot be used inside a custom tool.");
            foreach (Match m in Regex.Matches(st.Args.ToJsonString(), @"\{\{([A-Za-z0-9_]+)\}\}")) used.Add(m.Groups[1].Value);
        }
        foreach (var u in used.Where(u => !names.Contains(u, StringComparer.OrdinalIgnoreCase))) errors.Add($"{{{{{u}}}}} is used in a step but is not a parameter.");
        foreach (var p in d.Parameters.Where(p => !used.Contains(p.Name))) errors.Add($"Parameter '{p.Name}' is not used by any step.");
        return errors;
    }

    // ---- running: fill in the arguments of each step

    /// <summary>The steps with every <c>{{parameter}}</c> replaced. A value that is only a placeholder keeps the parameter's type (number, true/false, list).</summary>
    public static List<(string Tool, JsonObject Args, string? Note)> Resolve(CustomToolDef d, IReadOnlyDictionary<string, string> values)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in d.Parameters)
        {
            if (values.TryGetValue(p.Name, out var v) && !string.IsNullOrWhiteSpace(v)) map[p.Name] = v.Trim();
            else if (p.Default is not null) map[p.Name] = p.Default;
            else if (p.Required) throw new ArgumentException($"The parameter '{p.Name}' is needed: {p.Description}");
        }
        var types = d.Parameters.ToDictionary(p => p.Name, p => p.Type, StringComparer.OrdinalIgnoreCase);
        return d.Steps.Select(s => (s.Tool, (JsonObject)Fill(JsonNode.Parse(s.Args.ToJsonString())!, map, types)!, s.Note)).ToList();
    }

    private static JsonNode? Fill(JsonNode? n, Dictionary<string, string> map, Dictionary<string, string> types)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList())
                {
                    var child = o[k];
                    var filled = Fill(child, map, types);
                    if (filled is null && child is JsonValue) { o.Remove(k); continue; }     // optional value left empty: leave the argument out
                    if (!ReferenceEquals(filled, child)) o[k] = filled;
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) { var child = a[i]; var filled = Fill(child, map, types); if (!ReferenceEquals(filled, child)) a[i] = filled; }
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                var whole = Regex.Match(s, @"^\{\{([A-Za-z0-9_]+)\}\}$");
                if (whole.Success)
                {
                    if (!map.TryGetValue(whole.Groups[1].Value, out var val)) return null;
                    var type = types.GetValueOrDefault(whole.Groups[1].Value, "string");
                    return type switch
                    {
                        "number" when double.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
                        "boolean" => JsonValue.Create(val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1" || val.Equals("yes", StringComparison.OrdinalIgnoreCase)),
                        "list" => new JsonArray(val.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                        _ => JsonValue.Create(val),
                    };
                }
                return JsonValue.Create(Regex.Replace(s, @"\{\{([A-Za-z0-9_]+)\}\}", m => map.GetValueOrDefault(m.Groups[1].Value, "")));
            default: return n;
        }
    }
}

using System.Text;
using System.Text.Json;

namespace Pec.Qc.Core.Claude;

/// <summary>
/// The QC project the user works on, so the assistant knows the client, the Navisworks model and where the project inputs are without being told each time.
/// One JSON card per project in <c>&lt;shared&gt;\projects</c>; which project is active is per user.
/// </summary>
public sealed record ProjectCard
{
    public string Name { get; init; } = "";          // label in the app, e.g. "Blue Point - piping"
    public string Client { get; init; } = "";
    public string ProjectNo { get; init; } = "";
    public string ModelFile { get; init; } = "";     // the Navisworks .nwd the isometrics and GA drawings are checked against
    public string InputsFolder { get; init; } = "";  // PMS, line list, P&IDs, vendor data (default: <shared>\projects\<name>\inputs)
    public string RulePacks { get; init; } = "";     // requirement packs that apply, comma separated (file names in <shared>\requirements)
    public string Details { get; init; } = "";       // free text: scope, units, naming rules, contacts

    /// <summary>The block added to the prompt. Only filled fields are written; nothing is invented.</summary>
    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Active QC project (set by the user in the app)");
        void L(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"- {k}: {v.Trim()}"); }
        L("Project", Name); L("Client", Client); L("Project no.", ProjectNo); L("Navisworks model", ModelFile);
        L("Project inputs folder", InputsFolder); L("Requirement packs", RulePacks);
        if (!string.IsNullOrWhiteSpace(Details)) sb.AppendLine().AppendLine("Project details:").AppendLine(Details.Trim());
        sb.AppendLine().AppendLine("Use these values unless the user names others. If a needed value is empty, ask for it; do not guess.");
        return sb.ToString();
    }
}

public sealed class ProjectStore
{
    // Local file: which project this user has active (always per user) and, when there is no shared folder, the cards themselves.
    private sealed class FileModel { public string Active { get; set; } = ""; public List<ProjectCard> Projects { get; set; } = []; }

    private readonly string _file;
    private readonly string? _sharedDir;
    private FileModel _m = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <param name="file">The user's own projects.json (default: the app data folder).</param>
    /// <param name="sharedDir">Shared <c>projects</c> folder with one JSON card per project. Default: from the settings (shared office folder), none when there is no shared folder.</param>
    public ProjectStore(string? file = null, string? sharedDir = null)
    {
        _file = file ?? Path.Combine(Settings.DataDirectory, "projects.json");
        _sharedDir = sharedDir ?? (file is null ? Settings.Load().EffectiveProjectsDirectory : null);
        try { if (File.Exists(_file)) _m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_file), Json) ?? new(); }
        catch (JsonException) { _m = new(); }
    }

    public bool IsShared => !string.IsNullOrWhiteSpace(_sharedDir);
    public string? SharedDirectory => _sharedDir;

    private List<ProjectCard> SharedCards()
    {
        var list = new List<ProjectCard>();
        if (!IsShared || !Directory.Exists(_sharedDir)) return list;
        foreach (var f in Directory.EnumerateFiles(_sharedDir!, "*.json").Order(StringComparer.OrdinalIgnoreCase))   // top level only: sub-folders hold the project inputs
        {
            try { var c = JsonSerializer.Deserialize<ProjectCard>(File.ReadAllText(f), Json); if (c is { Name.Length: > 0 }) list.Add(c); }
            catch (Exception e) when (e is JsonException or IOException) { /* a broken card is skipped, never blocks the others */ }
        }
        return list;
    }

    /// <summary>Shared cards first, then this PC's own cards that are not in the shared folder.</summary>
    public IReadOnlyList<ProjectCard> All
    {
        get
        {
            var shared = SharedCards();
            return shared.Concat(_m.Projects.Where(l => shared.All(s => s.Name != l.Name))).ToList();
        }
    }

    /// <summary>True when the card is only on this PC (not in the shared folder).</summary>
    public bool IsLocalOnly(string name) => IsShared && SharedCards().All(s => s.Name != name);

    public ProjectCard? Active => All.FirstOrDefault(p => p.Name == _m.Active) ?? All.FirstOrDefault();

    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private string CardFile(string name) => Path.Combine(_sharedDir!, SafeName(name) + ".json");

    /// <summary>The default inputs folder of a project on the share: <c>projects\&lt;name&gt;\inputs</c>.</summary>
    public string? DefaultInputsFolder(string name) => IsShared ? Path.Combine(_sharedDir!, SafeName(name), "inputs") : null;

    public void Save(ProjectCard p, bool makeActive = true)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("A project needs a name.");
        if (IsShared)
        {
            Directory.CreateDirectory(_sharedDir!);
            File.WriteAllText(CardFile(p.Name), JsonSerializer.Serialize(p, Json));
            _m.Projects.RemoveAll(x => x.Name == p.Name);           // the shared card replaces a local copy
        }
        else
        {
            var i = _m.Projects.FindIndex(x => x.Name == p.Name);
            if (i >= 0) _m.Projects[i] = p; else _m.Projects.Add(p);
        }
        if (makeActive) _m.Active = p.Name;
        Write();
    }

    /// <summary>Copy this PC's own cards into the shared folder (only those not there yet). Returns how many.</summary>
    public int ShareLocalCards()
    {
        if (!IsShared) return 0;
        var n = 0;
        foreach (var l in _m.Projects.ToList()) if (IsLocalOnly(l.Name)) { Save(l, makeActive: false); n++; }
        return n;
    }

    public void SetActive(string name) { if (All.Any(p => p.Name == name)) { _m.Active = name; Write(); } }

    /// <summary>Removes a card. On a share where users cannot delete files this removes only this PC's copy and says so through the return value (false = nothing removed).</summary>
    public bool Delete(string name)
    {
        var n = _m.Projects.RemoveAll(p => p.Name == name);
        if (IsShared && File.Exists(CardFile(name)))
        {
            try { File.Delete(CardFile(name)); n++; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { /* the share allows create but not delete (or the file is in use): an administrator removes the card */ }
        }
        if (_m.Active == name) _m.Active = All.FirstOrDefault()?.Name ?? "";
        if (n > 0) Write();
        return n > 0;
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(_m, Json));
    }
}

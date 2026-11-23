using System.Text.Json;

namespace Pec.Qc.Core;

/// <summary>
/// Office-wide defaults shipped NEXT TO the installer and inside the app (office-defaults.json): the shared library folder and the update (Download) folder of this
/// company's server. The release build writes the file from the release manager's private config, so server names never live in the source repository.
/// A user's own settings always win; the defaults only fill what is empty.
/// </summary>
public sealed record OfficeDefaults(string? SharedRoot, string? UpdateSource)
{
    public const string FileName = "office-defaults.json";
    public static readonly OfficeDefaults None = new(null, null);

    public static OfficeDefaults Load(params string[] extraFolders)
    {
        var folders = new List<string>(extraFolders) { AppContext.BaseDirectory };
        try { var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/'))?.FullName; if (parent is not null) folders.Add(parent); } catch { }
        foreach (var f in folders)
        {
            try
            {
                var file = Path.Combine(f, FileName);
                if (!File.Exists(file)) continue;
                var d = JsonSerializer.Deserialize<OfficeDefaults>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (d is not null) return d;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return None;
    }

    /// <summary>Fills the empty SharedRoot / UpdateSource of a settings object from these defaults.</summary>
    public void ApplyTo(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.SharedRoot) && !string.IsNullOrWhiteSpace(SharedRoot)) s.SharedRoot = SharedRoot;
        if (string.IsNullOrWhiteSpace(s.UpdateSource) && !string.IsNullOrWhiteSpace(UpdateSource)) s.UpdateSource = UpdateSource;
    }
}

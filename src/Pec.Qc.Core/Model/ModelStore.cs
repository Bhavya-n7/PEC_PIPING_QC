namespace Pec.Qc.Core.Model;

public sealed record StoredModel(string Name, string Path, DateTime Written, long Bytes);

/// <summary>
/// The exported model snapshots, one folder per project: models\&lt;project&gt;\&lt;model name&gt;.model.json on the shared folder (or the local data folder with none), so the
/// team exports a model once and everyone checks against it. An export never overwrites an earlier snapshot of the same model: the old one is kept with a time stamp.
/// </summary>
public sealed class ModelStore(Settings settings)
{
    public string FolderFor(string project) => System.IO.Path.Combine(settings.EffectiveModelsDirectory, Safe(project.Length == 0 ? "(no project)" : project));

    public static string Safe(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Where the snapshot of this model file goes.</summary>
    public string PathFor(string project, string modelFile) =>
        System.IO.Path.Combine(FolderFor(project), Safe(System.IO.Path.GetFileNameWithoutExtension(modelFile)) + ".model.json");

    public List<StoredModel> List(string project)
    {
        var dir = FolderFor(project);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.model.json").Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new StoredModel(f.Name[..^".model.json".Length], f.FullName, f.LastWriteTime, f.Length)).ToList();
    }

    /// <summary>The snapshot to check against: the one named, or the newest of the project.</summary>
    public StoredModel? Find(string project, string? name = null)
    {
        var all = List(project);
        if (string.IsNullOrWhiteSpace(name)) return all.FirstOrDefault();
        var n = name.Trim();
        if (n.EndsWith(".model.json", StringComparison.OrdinalIgnoreCase)) n = n[..^".model.json".Length];
        n = System.IO.Path.GetFileNameWithoutExtension(n.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase) ? n : n + ".x");
        return all.FirstOrDefault(m => m.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(m => m.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Moves an earlier snapshot of the same model aside (stamped), so a new export never overwrites it. Returns the new name of the old file, or null if there was none.</summary>
    public static string? KeepOld(string target)
    {
        if (!File.Exists(target)) return null;
        var stamped = System.IO.Path.ChangeExtension(target, null) + "." + File.GetLastWriteTime(target).ToString("yyyyMMdd-HHmmss") + ".old.json";
        if (File.Exists(stamped)) return null;
        File.Copy(target, stamped);
        return stamped;
    }
}

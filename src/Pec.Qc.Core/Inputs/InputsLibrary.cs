using System.Text.RegularExpressions;
using Pec.Qc.Core.Findings;

namespace Pec.Qc.Core.Inputs;

/// <summary>What a project input is: the client's documents the QC checks against (QCTYPES.MD).</summary>
public enum InputKind { Pms, LineList, Pid, Vendor, Other }

public static class InputKinds
{
    /// <summary>The standard sub-folders of a project's inputs folder.</summary>
    public static string FolderName(InputKind k) => k switch { InputKind.Pms => "pms", InputKind.LineList => "line-list", InputKind.Pid => "pid", InputKind.Vendor => "vendor", _ => "other" };

    public static string Label(InputKind k) => k switch
    {
        InputKind.Pms => "Piping Material Specification (PMS)", InputKind.LineList => "Line list", InputKind.Pid => "P&IDs", InputKind.Vendor => "Vendor data", _ => "Other project documents",
    };

    public static InputKind? Parse(string? text) => (text ?? "").Trim().ToLowerInvariant().Replace("&", "").Replace("-", "").Replace("_", "").Replace(" ", "") switch
    {
        "pms" or "spec" or "specs" => InputKind.Pms,
        "linelist" or "lines" => InputKind.LineList,
        "pid" or "pids" or "pandid" => InputKind.Pid,
        "vendor" or "vendordata" or "datasheet" or "datasheets" => InputKind.Vendor,
        "other" => InputKind.Other,
        _ => null,
    };

    /// <summary>By the first folder of the relative path when it is one of the standard names, else by words in the file name.</summary>
    public static InputKind Classify(string relPath)
    {
        var parts = relPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && Parse(parts[0]) is { } byFolder) return byFolder;
        var name = Path.GetFileNameWithoutExtension(parts.LastOrDefault() ?? "");
        if (Regex.IsMatch(name, @"(^|[\s._-])pms([\s._-]|$)|piping[\s._-]*material|material[\s._-]*spec", RegexOptions.IgnoreCase)) return InputKind.Pms;
        if (Regex.IsMatch(name, @"line[\s._-]*list|linelist", RegexOptions.IgnoreCase)) return InputKind.LineList;
        if (Regex.IsMatch(name, @"p\s*&\s*id|(^|[\s._-])p[\s._-]?id([\s._-]|$)|piping[\s._-]*and[\s._-]*instrument", RegexOptions.IgnoreCase)) return InputKind.Pid;
        if (Regex.IsMatch(name, @"vendor|data[\s._-]*sheet|catalog", RegexOptions.IgnoreCase)) return InputKind.Vendor;
        return InputKind.Other;
    }
}

public sealed record InputDocument(string RelPath, InputKind Kind, string Ext, long Size, DateTime Modified, bool Indexable);

public sealed record InputCoverageItem(InputKind Kind, int Count, bool Present, string Why);

/// <summary>The project's inputs folder (PMS, line list, P&amp;IDs, vendor data): what is in it and what each QC type still needs.</summary>
public sealed class InputsLibrary(string folder)
{
    public string Folder { get; } = folder;
    public bool Exists => Directory.Exists(Folder);

    public static readonly string[] IndexableExtensions = [".pdf", ".xlsx", ".xlsm", ".csv", ".tsv", ".docx", ".pptx", ".txt", ".md"];

    public static bool IsIndexable(string path) => IndexableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool IsNoise(string name) =>
        name.StartsWith("~$") || name.StartsWith('.') || name.Contains(".bak_", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every file under the folder (sub-folders included), newest layout first by kind. Backup and temporary files are left out.</summary>
    public IReadOnlyList<InputDocument> Inventory()
    {
        if (!Exists) return [];
        var list = new List<InputDocument>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (IsNoise(name)) continue;
                var info = new FileInfo(f);
                var rel = Path.GetRelativePath(Folder, f);
                list.Add(new InputDocument(rel, InputKinds.Classify(rel), info.Extension.ToLowerInvariant(), info.Length, info.LastWriteTime, IsIndexable(f)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a share that drops out mid-scan: what was read so far is returned */ }
        return list.OrderBy(d => d.Kind).ThenBy(d => d.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The kinds of input each QC type is checked against (QCTYPES.MD).</summary>
    public static IReadOnlyList<(InputKind Kind, string Why)> Needs(QcType t) => t switch
    {
        QcType.Isometric => [(InputKind.Pms, "class, rating, material and component descriptions"), (InputKind.LineList, "size, fluid, class, pressures, temperatures, insulation, P&ID number"),
                             (InputKind.Pid, "the line exists and where it runs"), (InputKind.Vendor, "valves and special items")],
        QcType.Pid => [(InputKind.Pid, "the client's original P&ID the redrawn one is compared with")],
        _ => [(InputKind.Pid, "tags and services"), (InputKind.Vendor, "equipment dimensions and nozzles")],
    };

    public IReadOnlyList<InputCoverageItem> Coverage(QcType t, IReadOnlyList<InputDocument>? inventory = null)
    {
        inventory ??= Inventory();
        return Needs(t).Select(n => { var c = inventory.Count(d => d.Kind == n.Kind); return new InputCoverageItem(n.Kind, c, c > 0, n.Why); }).ToList();
    }

    /// <summary>Creates the standard sub-folders (pms, line-list, pid, vendor, other). Only called for a folder the assistant made itself.</summary>
    public static void EnsureLayout(string folder)
    {
        foreach (InputKind k in Enum.GetValues<InputKind>()) Directory.CreateDirectory(Path.Combine(folder, InputKinds.FolderName(k)));
    }

    /// <summary>The full path of a file inside the folder; null when the name would leave it (a path outside is never read).</summary>
    public string? Resolve(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var root = Path.GetFullPath(Folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.IsPathRooted(relPath) ? relPath : Path.Combine(Folder, relPath));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }
}

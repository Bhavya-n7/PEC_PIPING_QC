namespace Pec.Qc.Core.Install;

/// <summary>
/// Finds Navisworks Manage on this PC (optional: only the cross-check against a source NWD needs it). Which licence it runs under
/// (education or commercial) is the user's own business: the product works with either and never checks or changes it.
/// </summary>
public sealed record NavisworksInstall(string Name, string Root)
{
    public string RoamerExe => Path.Combine(Root, "Roamer.exe");
    public string ApiDll => Path.Combine(Root, "Autodesk.Navisworks.Api.dll");
    public string AutomationDll => Path.Combine(Root, "Autodesk.Navisworks.Automation.dll");

    public static NavisworksInstall? Find(string? configuredRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot) && File.Exists(Path.Combine(configuredRoot, "Autodesk.Navisworks.Automation.dll")))
            return new(Path.GetFileName(configuredRoot.TrimEnd('\\', '/')), configuredRoot);
        var autodesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");
        if (!Directory.Exists(autodesk)) return null;
        return Directory.EnumerateDirectories(autodesk, "Navisworks Manage*")
            .Where(d => File.Exists(Path.Combine(d, "Autodesk.Navisworks.Automation.dll")))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new NavisworksInstall(Path.GetFileName(d), d))
            .FirstOrDefault();
    }
}

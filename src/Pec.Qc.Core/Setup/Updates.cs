using System.Reflection;
using System.Text.Json;

namespace Pec.Qc.Core.Setup;

/// <summary>The published latest release: <c>latest.json</c> next to the installer, on a server share or a web address.</summary>
public sealed record ReleaseInfo(string Version, string Notes, string Setup, DateTime? Released);

public sealed record UpdateCheck(bool Available, string Current, ReleaseInfo? Latest, string? Error)
{
    /// <summary>Where Setup can be started from (a file path or a URL), when an update is available.</summary>
    public string? SetupLocation { get; init; }
}

/// <summary>
/// Rolling out a new version = copy <c>PecQcSetup.exe</c>, <c>payload.zip</c> and <c>latest.json</c> (written by
/// <c>scripts\build-installer.ps1</c>) to the update folder. Every app checks that folder (Settings: Update source) at start-up.
/// </summary>
public static class Updates
{
    public const string FileName = "latest.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public static async Task<UpdateCheck> CheckAsync(string? source, string? current = null, CancellationToken ct = default)
    {
        current ??= CurrentVersion;
        if (string.IsNullOrWhiteSpace(source)) return new UpdateCheck(false, current, null, null);
        try
        {
            string json; string baseLocation;
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseLocation = source.TrimEnd('/') + "/";
                json = await Http.GetStringAsync(baseLocation + FileName, ct);
            }
            else
            {
                var file = Directory.Exists(source) ? Path.Combine(source, FileName) : source;
                if (!File.Exists(file)) return new UpdateCheck(false, current, null, $"No {FileName} in {source}");
                baseLocation = Path.GetDirectoryName(Path.GetFullPath(file))! + Path.DirectorySeparatorChar;
                json = await File.ReadAllTextAsync(file, ct);
            }
            var latest = JsonSerializer.Deserialize<ReleaseInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (latest is null || string.IsNullOrWhiteSpace(latest.Version)) return new UpdateCheck(false, current, null, "The release file is empty or not valid.");
            var newer = IsNewer(latest.Version, current);
            return new UpdateCheck(newer, current, latest, null) { SetupLocation = newer ? baseLocation + (string.IsNullOrWhiteSpace(latest.Setup) ? "PecQcSetup.exe" : latest.Setup) : null };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or TaskCanceledException or UnauthorizedAccessException)
        {
            return new UpdateCheck(false, current, null, "Could not check for updates: " + e.Message);
        }
    }

    /// <summary>"1.2.0" is newer than "1.1.9"; missing parts count as 0; text after '-' (pre-release) is ignored.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.Split('-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
        var a = Parts(candidate); var b = Parts(current);
        for (var i = 0; i < 4; i++) if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    /// <summary>Make the release file (used by the build script and tests).</summary>
    public static string ReleaseJson(string version, string notes, string setupFile = "PecQcSetup.exe") =>
        JsonSerializer.Serialize(new ReleaseInfo(version, notes, setupFile, DateTime.Now), new JsonSerializerOptions { WriteIndented = true });
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pec.Qc.Core.Util;

/// <summary>One JSON line per write to something shared (skills, lessons, requirement packs, tools, reports): who, which project, which tool, a hash of the arguments, the outcome. Never contains secrets or arguments themselves.</summary>
public static class AuditLog
{
    public static string ArgsHash(string canonicalArguments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArguments)))[..16].ToLowerInvariant();

    public static string Write(Settings settings, string tool, string project, string canonicalArguments, string outcome)
    {
        var dir = settings.EffectiveAuditDirectory;
        var file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM") + ".jsonl");
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow.ToString("s") + "Z", user = UserIdentity.Current(settings).Name, employeeCode = UserIdentity.Current(settings).EmployeeCode,
            company = UserIdentity.Current(settings).Company, windowsUser = Environment.UserName, machine = Environment.MachineName,
            tool, project, argsHash = ArgsHash(canonicalArguments), outcome = outcome.Length > 400 ? outcome[..400] : outcome,
        });
        try { Directory.CreateDirectory(dir); File.AppendAllText(file, line + "\n"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* an unreachable share must not block the work; the local copy below is kept */ Fallback(line); }
        return file;
    }

    private static void Fallback(string line)
    {
        var dir = Path.Combine(Settings.DataDirectory, "audit");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM") + ".jsonl"), line + "\n");
    }
}

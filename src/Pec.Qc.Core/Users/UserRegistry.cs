using System.Text.Json;
using System.Text.Json.Serialization;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Core.Users;

/// <summary>The roles of the QC team. Each role includes the one before it.</summary>
public enum QcRole { Engineer = 0, Lead = 1, Admin = 2 }

public static class QcRoles
{
    public const string Engineer = "qc-engineer", Lead = "qc-lead", Admin = "admin";

    public static string Name(QcRole r) => r switch { QcRole.Admin => Admin, QcRole.Lead => Lead, _ => Engineer };

    /// <summary>Accepts the registry names and a few natural spellings; anything unknown is the lowest role, never a higher one.</summary>
    public static QcRole Parse(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-') switch
    {
        "admin" or "administrator" => QcRole.Admin,
        "qc-lead" or "lead" => QcRole.Lead,
        _ => QcRole.Engineer,
    };

    public static bool TryParseStrict(string? s, out QcRole role)
    {
        role = Parse(s);
        var k = (s ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return k is "admin" or "administrator" or "qc-lead" or "lead" or "qc-engineer" or "engineer";
    }
}

/// <summary>One person in the central registry (<c>users\&lt;employee code&gt;.json</c> in the shared folder).</summary>
public sealed record UserRecord
{
    public string Name { get; init; } = "";
    public string EmployeeCode { get; init; } = "";
    public string Company { get; init; } = "";
    public string Role { get; init; } = QcRoles.Engineer;
    public string FirstSeen { get; init; } = "";
    public string LastSeen { get; init; } = "";
    public string Version { get; init; } = "";
    public string Machine { get; init; } = "";
    public string WindowsUser { get; init; } = "";
    public int Sessions { get; init; }
    [JsonIgnore] public QcRole RoleValue => QcRoles.Parse(Role);
}

/// <summary>
/// The central user registry. Every start of the assistant refreshes the user's own file (name, code, version, machine, last seen) and keeps the role,
/// which only an administrator changes. The share lets users create and overwrite files but not delete them, so nothing here deletes.
/// If the share cannot be reached, the record is written to the local data folder and is the answer for that session.
/// Honest limit: the role lives in a text file that anyone with write access to the share could edit; the tools enforce it, the share's folder permissions
/// are what really protect it (give <c>users\</c> modify rights to administrators only, and have the app write through <c>Touch</c> only for a user's own file).
/// </summary>
public static class UserRegistry
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string FileFor(string usersDirectory, UserIdentity who) => Path.Combine(usersDirectory, who.FileKey + ".json");

    public static UserRecord? Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<UserRecord>(File.ReadAllText(file), Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyList<UserRecord> All(string usersDirectory)
    {
        try
        {
            if (!Directory.Exists(usersDirectory)) return [];
            return Directory.GetFiles(usersDirectory, "*.json").Select(Read).Where(r => r is not null).Cast<UserRecord>()
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Records that this user is here. A new user is a qc-engineer, except that when the registry has no administrator yet the first person to register becomes one,
    /// so someone can manage the others (change it afterwards by editing the file or with set_user_role). An existing record keeps its role and first-seen date.
    /// </summary>
    public static UserRecord Touch(Settings settings, UserIdentity who, string? localDirectory = null, bool newSession = true)
    {
        var dir = settings.EffectiveUsersDirectory;
        var existing = Read(FileFor(dir, who));
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var role = existing?.Role;
        if (string.IsNullOrWhiteSpace(role)) role = All(dir).Any(r => r.RoleValue == QcRole.Admin) ? QcRoles.Engineer : QcRoles.Admin;
        var rec = new UserRecord
        {
            Name = who.Name, EmployeeCode = who.EmployeeCode, Company = who.Company, Role = role,
            FirstSeen = existing?.FirstSeen is { Length: > 0 } f ? f : now, LastSeen = now,
            Version = Updates.CurrentVersion, Machine = Environment.MachineName, WindowsUser = Environment.UserName,
            Sessions = existing is null ? 1 : existing.Sessions + (newSession ? 1 : 0),   // the app counts a session at start; the tools only refresh last-seen
        };
        Save(settings, who, rec, localDirectory);
        return rec;
    }

    /// <summary>The role of this user without writing anything (engineer when unknown).</summary>
    public static QcRole RoleOf(Settings settings, UserIdentity who, string? localDirectory = null) =>
        (Read(FileFor(settings.EffectiveUsersDirectory, who)) ?? Read(FileFor(localDirectory ?? LocalDirectory, who)))?.RoleValue ?? QcRole.Engineer;

    /// <summary>Changes the role of one person (the caller has already checked that the user is an administrator). False when that person is not registered.</summary>
    public static bool SetRole(Settings settings, string codeOrName, QcRole role, out UserRecord? changed)
    {
        changed = null;
        var match = All(settings.EffectiveUsersDirectory).FirstOrDefault(r =>
            string.Equals(r.EmployeeCode, codeOrName.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, codeOrName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        var who = new UserIdentity(match.Name, match.EmployeeCode, match.Company);
        changed = match with { Role = QcRoles.Name(role) };
        return Save(settings, who, changed);
    }

    private static string LocalDirectory => Path.Combine(Settings.DataDirectory, "users");

    private static bool Save(Settings settings, UserIdentity who, UserRecord rec, string? localDirectory = null)
    {
        var text = JsonSerializer.Serialize(rec, Json);
        try
        {
            Directory.CreateDirectory(settings.EffectiveUsersDirectory);
            File.WriteAllText(FileFor(settings.EffectiveUsersDirectory, who), text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var local = localDirectory ?? LocalDirectory;
            try { Directory.CreateDirectory(local); File.WriteAllText(FileFor(local, who), text); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}

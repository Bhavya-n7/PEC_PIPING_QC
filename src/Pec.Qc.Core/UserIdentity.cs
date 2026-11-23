namespace Pec.Qc.Core;

/// <summary>
/// Who is using the assistant: name, employee code and company, asked once when the app first starts. Every lesson, audit entry and activity-log line carries it,
/// so the team can tell who asked what, support them, and learn from the right person. The desktop app passes it to the tools it starts (environment variables);
/// when the tools are started by another client they read it from the same settings file.
/// </summary>
public sealed record UserIdentity(string Name, string EmployeeCode, string Company)
{
    public const string NameVar = "PEC_QC_USER", CodeVar = "PEC_QC_EMPCODE", CompanyVar = "PEC_QC_COMPANY";

    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(EmployeeCode);

    /// <summary>"Anita Rao (E1234)", or just the name when there is no code yet.</summary>
    public string Display => EmployeeCode.Length > 0 ? $"{Name} ({EmployeeCode})" : Name;

    /// <summary>What a file name can carry: the employee code (or the Windows user) and nothing odd.</summary>
    public string FileKey
    {
        get
        {
            var k = EmployeeCode.Length > 0 ? EmployeeCode : Environment.UserName;
            var safe = new string(k.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            return safe.Length > 0 ? safe : "user";
        }
    }

    public static UserIdentity FromSettings(Settings s) => new((s.UserName ?? "").Trim(), (s.EmployeeCode ?? "").Trim(), (s.Company ?? "").Trim());

    /// <summary>The environment (set by the desktop app) first, then the settings file, then the Windows user name.</summary>
    public static UserIdentity Current(Settings s)
    {
        var fromSettings = FromSettings(s);
        string Pick(string var, string fallback) { var v = Environment.GetEnvironmentVariable(var); return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim(); }
        var name = Pick(NameVar, fromSettings.Name);
        return new UserIdentity(name.Length > 0 ? name : Environment.UserName, Pick(CodeVar, fromSettings.EmployeeCode), Pick(CompanyVar, fromSettings.Company));
    }

    public IDictionary<string, string> ToEnvironment() => new Dictionary<string, string> { [NameVar] = Name, [CodeVar] = EmployeeCode, [CompanyVar] = Company };
}

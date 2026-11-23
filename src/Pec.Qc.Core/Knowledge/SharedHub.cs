using System.Text.Json;

namespace Pec.Qc.Core.Knowledge;

public sealed record HubStatus(string Root, bool Reachable, bool Writable, IReadOnlyDictionary<string, int> Counts);

/// <summary>The shared office folder: created with its sub-folders and a README, so everyone finds the same layout.</summary>
public static class SharedHub
{
    public static readonly string[] Folders =
        ["skills", "learning", "context", "documents", "requirements", "projects", "models", "reports", "users", "activity", "audit", "prompts", "tools", "commands", "agents", "contributions", "updates"];

    /// <summary>The name of the probe file: one fixed name that is overwritten, so a share that lets users create and change files but not delete them stays clean.</summary>

    public static HubStatus Ensure(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            foreach (var f in Folders) Directory.CreateDirectory(Path.Combine(root, f));
            var readme = Path.Combine(root, "README.md");
            if (!File.Exists(readme)) File.WriteAllText(readme, Readme);
            var info = Path.Combine(root, "hub.json");
            if (!File.Exists(info)) File.WriteAllText(info, JsonSerializer.Serialize(new { layout = 1, created = DateTime.Now.ToString("yyyy-MM-dd"), folders = Folders }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new HubStatus(root, false, false, new Dictionary<string, int>()); }
        return Status(root);
    }

    public static HubStatus Status(string root)
    {
        if (!Directory.Exists(root)) return new HubStatus(root, false, false, new Dictionary<string, int>());
        var counts = Folders.ToDictionary(f => f, f => Directory.Exists(Path.Combine(root, f)) ? Directory.GetFiles(Path.Combine(root, f), "*", SearchOption.AllDirectories).Length : 0);
        return new HubStatus(root, true, CanWrite(root), counts);
    }

    public const string ProbeFileName = ".write-test";

    /// <summary>
    /// True when files can be created and changed. Deleting is NOT required (and never relied on): the QC share lets users create, overwrite and append
    /// but not delete files, so the probe overwrites one fixed file instead of creating and removing a new one each time.
    /// </summary>
    private static bool CanWrite(string root)
    {
        try
        {
            File.WriteAllText(Path.Combine(root, ProbeFileName), DateTime.UtcNow.ToString("s") + "Z");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private const string Readme = """
        # Shared folder for the PEC QC Assistant

        Every PC that uses the assistant points at this folder (Settings > Shared folder). What is here is shared by everyone.

        | Folder | What it holds | Who changes it |
        |---|---|---|
        | `skills\` | How-to guides, one folder per skill with a `SKILL.md` | Administrators; Claude can save a skill after the user approves |
        | `learning\` | Lessons, decisions, mistakes, client rules and corrections (Markdown, each with a source) | Claude (user approves each entry) |
        | `context\` | Context notes per project: what is set up, what is pending, key decisions | Claude (user approves) |
        | `documents\` | Client procedures, specifications, training (PDF, Markdown, text) and the converted `pack\` | Administrators |
        | `requirements\` | Client rule packs and QC-type checklists (JSON, every rule with its source: document, section, page) | QC leads |
        | `projects\` | One JSON card per QC project (client, project no., Navisworks model, inputs folder, rule packs); `projects\<name>\inputs\` holds that project's PMS, line list, P&IDs and vendor data | QC leads; users through the app |
        | `models\` | Navisworks model snapshots (JSON) so the team exports a model once | Any user (through the app) |
        | `reports\` | Saved QC reports (Excel and Markdown), one folder per project | The assistant |
        | `users\` | The user registry: one file per person (name, employee code, role, first and last seen, version) | The app (automatic); roles by administrators |
        | `activity\` | What each user asked and what the assistant did, per month, person and PC | The app (automatic) |
        | `audit\` | The write-tool audit log, one JSON line per shared write (who, what, outcome) | The assistant (automatic) |
        | `prompts\` | `master-prompt.md` (the rules) and `quick-tasks.json` (the one-click tasks) | Administrators |
        | `tools\` | Custom tools: named sequences of the assistant's own tools. A tool made on one PC is copied here and then works on every PC | Claude (user approves) |
        | `commands\`, `agents\`, `contributions\` | The shared kit: commands and agents users propose, an administrator approves | Users, administrators |
        | `updates\` | `latest.json`, `PecQcSetup.exe`, `payload.zip` of the newest release | Release manager |

        Notes
        - Users can create, change and append to files here but **cannot delete** files. Logs are append-only; to remove something, ask an administrator. Backups are kept as `<file>.bak_<date-time>`.
        - Files are plain text, so they can be reviewed, versioned or restored from backup.
        - Client documents and learning may be confidential: give access only to the people who use the assistant.
        - Nothing here contains passwords or keys, and nothing should.
        """;
}

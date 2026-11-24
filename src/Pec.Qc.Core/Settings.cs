using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pec.Qc.Core;

/// <summary>
/// Local settings of the assistant, kept in <c>%APPDATA%\PEC\QcAssistant\settings.json</c>
/// (folder can be moved with the PEC_QC_DATA environment variable).
/// <para>
/// <see cref="SharedRoot"/> is the office folder everyone points at. When it is set, skills, learning, context, documents, prompts,
/// custom tools and updates all live inside it (<c>skills\</c>, <c>learning\</c>, <c>context\</c>, <c>documents\</c>, <c>prompts\</c>,
/// <c>tools\</c>, <c>updates\</c>). Each one can still be pointed somewhere else on its own.
/// </para>
/// </summary>
public sealed class Settings
{
    /// <summary>Navisworks Manage install folder when it cannot be found automatically (the user's own licence, education or commercial).</summary>
    public string? NavisworksHome { get; set; }

    /// <summary>Claude model for the assistant. Default Sonnet 5.5; the user can change it in the app.</summary>
    public string Model { get; set; } = ClaudeChoices.DefaultModel;

    /// <summary>Claude effort level (low, medium, high, xhigh, max). Default medium; the user can change it in the app.</summary>
    public string Effort { get; set; } = ClaudeChoices.DefaultEffort;

    /// <summary>The shared office folder (local or a server share) that holds everything that is shared between users.</summary>
    public string? SharedRoot { get; set; }

    /// <summary>Where the knowledge base lives (local folder or a shared drive). Empty = shared folder\learning, else &lt;data folder&gt;\knowledge.</summary>
    public string? KnowledgeDirectory { get; set; }

    /// <summary>Project context notes (what is set up, what is pending). Empty = shared folder\context, else &lt;data folder&gt;\context.</summary>
    public string? ContextDirectory { get; set; }

    /// <summary>Where the skills (SKILL.md folders) are read from.</summary>
    public string? SkillsDirectory { get; set; }

    /// <summary>Where client documents and extra manuals (PDF, Markdown, text) are read from for search.</summary>
    public string? DocumentsDirectory { get; set; }

    /// <summary>Where shared custom tools (recipes made from the existing tools) are kept.</summary>
    public string? ToolsDirectory { get; set; }

    /// <summary>The master prompt file that is sent with every request.</summary>
    public string? MasterPromptFile { get; set; }

    /// <summary>Folder (server share) or web address that holds latest.json, PecQcSetup.exe and payload.zip of the newest release.</summary>
    public string? UpdateSource { get; set; }

    /// <summary>The company's release PUBLIC key (XML). When set, an update is only accepted if its release file is signed with the matching private key. Comes from office-defaults.json.</summary>
    public string? UpdatePublicKey { get; set; }

    /// <summary>"full" (default): the activity log keeps what people asked and what the assistant answered. "metadata": only who, when, which tool and whether it worked; the texts are not recorded. For clients whose drawings and data must not be copied into a log.</summary>
    public string? ActivityDetail { get; set; }
    [JsonIgnore] public bool ActivityMetadataOnly => string.Equals(ActivityDetail, "metadata", StringComparison.OrdinalIgnoreCase);

    /// <summary>How often the user is asked before Claude saves something shared (skills, lessons, requirement packs, tools): task (once per task, the default) | strict (every write) | off (never ask).</summary>
    public string ApprovalMode { get; set; } = "task";

    /// <summary>What Claude may do on its own in the app: full (files, web, commands: it builds what is missing and tests it; the default) | standard (files in its workspace and the web) | careful (the QC tools only).</summary>
    public string Autonomy { get; set; } = "full";

    /// <summary>Who uses this PC's assistant (asked once at first start): shown on every lesson, audit entry and activity-log line.</summary>
    public string? UserName { get; set; }
    public string? EmployeeCode { get; set; }
    public string? Company { get; set; }

    /// <summary>After a task that had errors, built something or was corrected, the assistant reflects once: records lessons and saves tools it built. Default on.</summary>
    public bool SelfReflect { get; set; } = true;

    /// <summary>Install a newer version found at start-up by itself (once per version, before any chat is running). Default on.</summary>
    public bool AutoUpdate { get; set; } = true;
    public string? AutoUpdateTried { get; set; }

    [JsonIgnore] public Claude.ClaudeAutonomy EffectiveAutonomy => (Autonomy ?? "").Trim().ToLowerInvariant() switch
    {
        "careful" => Claude.ClaudeAutonomy.Careful, "standard" => Claude.ClaudeAutonomy.Standard, _ => Claude.ClaudeAutonomy.Full,
    };

    [JsonIgnore] public Approvals.ApprovalMode EffectiveApprovalMode => Approvals.ApprovalGate.ParseMode(string.IsNullOrWhiteSpace(ApprovalMode) ? "task" : ApprovalMode);

    [JsonIgnore] public string FilePath { get; private set; } = "";

    public static string DataDirectory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PEC_QC_DATA");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PEC", "QcAssistant");
        }
    }

    [JsonIgnore] public bool HasShared => !string.IsNullOrWhiteSpace(SharedRoot);

    private string Pick(string? explicitPath, string sub, string localSub) =>
        !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath
        : HasShared ? Path.Combine(SharedRoot!, sub)
        : Path.Combine(DataDirectory, localSub);

    [JsonIgnore] public string EffectiveKnowledgeDirectory => Pick(KnowledgeDirectory, "learning", "knowledge");
    [JsonIgnore] public string EffectiveContextDirectory => Pick(ContextDirectory, "context", "context");
    [JsonIgnore] public string EffectiveSkillsDirectory => Pick(SkillsDirectory, "skills", "skills");
    [JsonIgnore] public string EffectiveDocumentsDirectory => Pick(DocumentsDirectory, "documents", "documents");
    [JsonIgnore] public string EffectiveToolsDirectory => Pick(ToolsDirectory, "tools", "tools-shared");
    /// <summary>Client requirement files (rules with their sources) as JSON; they come from confidential client documents, so they live here, not in git.</summary>
    [JsonIgnore] public string EffectiveRequirementsDirectory => Pick(null, "requirements", "requirements");

    /// <summary>Project cards (name, client, project no., Navisworks model, inputs folder, rule packs) shared by everyone; null when there is no shared folder.
    /// Sub-folders hold each project's inputs (<c>projects\&lt;name&gt;\inputs</c>: PMS, line list, P&amp;IDs, vendor data).</summary>
    [JsonIgnore] public string? EffectiveProjectsDirectory => HasShared ? Path.Combine(SharedRoot!, "projects") : null;

    /// <summary>Navisworks model snapshots (JSON) so the team exports a model once and everyone checks against it.</summary>
    [JsonIgnore] public string EffectiveModelsDirectory => Pick(null, "models", "models");

    /// <summary>Saved QC reports (Excel + Markdown), one folder per project.</summary>
    [JsonIgnore] public string EffectiveReportsDirectory => Pick(null, "reports", "reports");

    /// <summary>The central user registry: one JSON file per user (identity, role, first/last seen, version).</summary>
    [JsonIgnore] public string EffectiveUsersDirectory => Pick(null, "users", "users");

    /// <summary>Write-tool audit log (one JSON line per write), on the shared folder when there is one.</summary>
    [JsonIgnore] public string EffectiveAuditDirectory => Pick(null, "audit", "audit");

    /// <summary>The user's own copy of custom tools: used when the shared folder cannot be reached, and synced with it.</summary>
    [JsonIgnore] public string LocalToolsDirectory => Path.Combine(DataDirectory, "tools");

    [JsonIgnore] public string EffectiveMasterPromptFile =>
        !string.IsNullOrWhiteSpace(MasterPromptFile) ? MasterPromptFile
        : HasShared ? Path.Combine(SharedRoot!, "prompts", "master-prompt.md")
        : Path.Combine(DataDirectory, "master-prompt.md");

    [JsonIgnore] public string EffectiveQuickTasksFile =>
        HasShared ? Path.Combine(SharedRoot!, "prompts", "quick-tasks.json") : Path.Combine(DataDirectory, "quick-tasks.json");

    [JsonIgnore] public string? EffectiveUpdateSource =>
        !string.IsNullOrWhiteSpace(UpdateSource) ? UpdateSource : HasShared ? Path.Combine(SharedRoot!, "updates") : null;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException) { s = new(); }
        s.FilePath = path;
        if (string.Equals(path, Path.Combine(DataDirectory, "settings.json"), StringComparison.OrdinalIgnoreCase))
            OfficeDefaults.Load().ApplyTo(s);   // fills only what is empty; only for the real settings file (tests use their own path)
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>The models and effort levels the app offers (full model names, so the choice does not move when an alias is updated).</summary>
public static class ClaudeChoices
{
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string DefaultEffort = "medium";

    public static readonly (string Id, string Label)[] Models =
    [
        ("claude-sonnet-5-5", "Sonnet 5.5"),
        ("claude-opus-5-5", "Opus 5.5"),
        ("claude-fable-5-2", "Fable 5.2"),
        ("claude-haiku-5-5", "Haiku 5.5"),
    ];

    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    public static string ModelOrDefault(string? m) => string.IsNullOrWhiteSpace(m) ? DefaultModel : m;
    public static string EffortOrDefault(string? e) => Efforts.Contains(e ?? "") ? e! : DefaultEffort;
}

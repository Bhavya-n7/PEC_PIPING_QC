using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pec.Qc.Core.Approvals;

public sealed record ApprovalRequest(string Id, string Tool, string Summary, string Detail, DateTime Requested, bool Destructive = false);

public enum ApprovalState { Allowed, Pending }

/// <summary>How often the user is asked before Claude changes something.</summary>
public enum ApprovalMode
{
    /// <summary>Never ask. Writes are still limited to sites that allow them.</summary>
    Off,
    /// <summary>Ask ONCE; the approval covers everything Claude does for that task until it has been idle for <see cref="ApprovalGate.TaskIdle"/>.
    /// Deleting, enabling writes on a site and approving colleagues' shared items always ask for themselves.</summary>
    Task,
    /// <summary>Ask for every single change (one run of one exact action, expires after 10 minutes).</summary>
    Strict,
}

/// <summary>
/// "Ask the user before changing something", enforced in the MCP server and not left to Claude: a write tool calls <see cref="Check"/>; unless the user has approved,
/// it gets Pending, the request is written to the approvals folder, the desktop app shows it, and the tool does nothing until it is called again.
/// <para>
/// In <see cref="ApprovalMode.Task"/> (the default) one approval opens a task window: later non-destructive changes pass without asking while Claude keeps working
/// (the window slides, and closes after <see cref="TaskIdle"/> of inactivity). Destructive actions are always exact and single-use.
/// </para>
/// <para>
/// The mode comes from PEC_QC_APPROVAL (set by the desktop app from its settings): off | task (or on) | strict. When the variable is absent the server
/// was started by another client (Claude Code or Claude Desktop in a terminal), which asks for permission itself before it calls a tool: no second dialog.
/// </para>
/// </summary>
public sealed class ApprovalGate
{
    public const string ModeVariable = "PEC_QC_APPROVAL";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TaskIdle = TimeSpan.FromMinutes(20);

    private readonly string _dir;
    private readonly Func<ApprovalMode> _mode;

    public ApprovalGate(string? directory = null, Func<ApprovalMode>? mode = null)
    {
        _dir = directory ?? Path.Combine(Settings.DataDirectory, "approvals");
        _mode = mode ?? (() => ParseMode(Environment.GetEnvironmentVariable(ModeVariable)));
    }

    /// <summary>Kept for callers that only know on/off: on means ask for every change.</summary>
    public ApprovalGate(string? directory, Func<bool> enabled) : this(directory, () => enabled() ? ApprovalMode.Strict : ApprovalMode.Off) { }

    public static ApprovalMode ParseMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "task" or "on" or "once" => ApprovalMode.Task,
        "strict" or "every" or "always" => ApprovalMode.Strict,
        _ => ApprovalMode.Off,        // "off", empty or absent
    };

    public static string ModeToVariable(ApprovalMode m) => m switch { ApprovalMode.Task => "task", ApprovalMode.Strict => "strict", _ => "off" };

    public ApprovalMode Mode => _mode();
    public string Directory_ => _dir;
    private string GrantFile => Path.Combine(_dir, "task.grant");

    /// <summary>Same action → same id (tool name + canonical arguments).</summary>
    public static string IdFor(string tool, string canonicalArguments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + canonicalArguments)))[..16].ToLowerInvariant();

    /// <param name="tool">MCP tool name.</param>
    /// <param name="canonicalArguments">The arguments that define the action (everything except apply).</param>
    /// <param name="summary">One line for the dialog.</param>
    /// <param name="detail">Diff or command line shown in the dialog.</param>
    /// <param name="destructive">Deleting, enabling writes, approving others' items: always asks for this exact action, even inside a task.</param>
    public ApprovalState Check(string tool, string canonicalArguments, string summary, string detail, bool destructive = false)
    {
        var mode = _mode();
        if (mode == ApprovalMode.Off) return ApprovalState.Allowed;
        System.IO.Directory.CreateDirectory(_dir);

        if (mode == ApprovalMode.Task && !destructive && TaskGrantActive(touch: true)) return ApprovalState.Allowed;

        var id = IdFor(tool, canonicalArguments);
        var granted = Path.Combine(_dir, id + ".approved");
        if (File.Exists(granted))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(granted);
            File.Delete(granted);                                   // single use
            DeletePending(id);
            if (age <= Lifetime) return ApprovalState.Allowed;
        }
        File.WriteAllText(Path.Combine(_dir, id + ".pending"), JsonSerializer.Serialize(new ApprovalRequest(id, tool, summary, detail, DateTime.Now, destructive)));
        return ApprovalState.Pending;
    }

    private bool TaskGrantActive(bool touch)
    {
        try
        {
            if (!File.Exists(GrantFile)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(GrantFile) > TaskIdle) { File.Delete(GrantFile); return false; }
            if (touch) File.SetLastWriteTimeUtc(GrantFile, DateTime.UtcNow);       // the window slides while Claude keeps working
            return true;
        }
        catch (IOException) { return false; }
    }

    public const string WaitMessage =
        "Waiting for the user's approval. A request was sent to the PEC QC Assistant window. " +
        "Tell the user in one short line what you are about to do, then call this tool again with exactly the same arguments. Do not ask again in the chat.";

    // ---- the desktop app's side

    public IReadOnlyList<ApprovalRequest> Pending() =>
        !System.IO.Directory.Exists(_dir) ? [] :
        System.IO.Directory.GetFiles(_dir, "*.pending")
            .Select(f => { try { return JsonSerializer.Deserialize<ApprovalRequest>(File.ReadAllText(f)); } catch (Exception e) when (e is IOException or JsonException) { return null; } })
            .OfType<ApprovalRequest>().OrderBy(r => r.Requested).ToList();

    /// <summary>The user approved. In task mode a non-destructive approval also opens the task window, so Claude can finish without asking again.</summary>
    public void Approve(string id, bool openTaskWindow = false)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var req = Pending().FirstOrDefault(r => r.Id == id);
        File.WriteAllText(Path.Combine(_dir, id + ".approved"), DateTime.UtcNow.ToString("o"));
        if (openTaskWindow && req is { Destructive: false }) File.WriteAllText(GrantFile, DateTime.UtcNow.ToString("o"));
        DeletePending(id);       // answered: the app's timer must not find the same request again and ask a second time
    }

    public void Deny(string id) { DeletePending(id); try { File.Delete(GrantFile); } catch (IOException) { } }

    /// <summary>Ends the task window (a new chat starts a new task).</summary>
    public void EndTask() { try { File.Delete(GrantFile); } catch (IOException) { } }

    public bool TaskWindowOpen => TaskGrantActive(touch: false);

    private void DeletePending(string id) { var p = Path.Combine(_dir, id + ".pending"); if (File.Exists(p)) File.Delete(p); }
}

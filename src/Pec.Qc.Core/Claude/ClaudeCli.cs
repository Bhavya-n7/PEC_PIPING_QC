using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pec.Qc.Core.Claude;

/// <summary>Tokens of one request: new input, output, input read from the prompt cache, input written to it.</summary>
public sealed record TokenUsage(int Input, int Output, int CacheRead, int CacheWrite)
{
    public int TotalInput => Input + CacheRead + CacheWrite;
    /// <summary>Share of the input that came from the cache (cheap and fast).</summary>
    public double CachedShare => TotalInput == 0 ? 0 : (double)CacheRead / TotalInput;
}

public sealed record ClaudeAuth(bool LoggedIn, string? Method, string? Email, string? Organization, DateTime? ExpiresAt, string? Error);

/// <summary>One thing that happened in a chat turn, in the order Claude Code reported it.</summary>
public abstract record ChatEvent
{
    public sealed record Started(string SessionId, IReadOnlyList<string> McpServers) : ChatEvent;
    public sealed record Text(string Content) : ChatEvent;
    public sealed record ToolUse(string Name, string Input) : ChatEvent;
    public sealed record ToolResult(string Content, bool IsError) : ChatEvent;
    public sealed record Finished(bool Ok, string Result, string? SessionId, double? CostUsd, int? Turns, TokenUsage? Usage = null) : ChatEvent;
}

/// <summary>
/// Signs in with the user's own Claude account through Claude Code (<c>claude auth login</c>) and chats through it
/// in headless mode, with only the PEC QC MCP server attached. No API key is used or stored.
/// </summary>
/// <summary>How much Claude may do on its own in the desktop app (see <see cref="ClaudeCli.ChatArguments"/>).</summary>
public enum ClaudeAutonomy { Careful, Standard, Full }

public sealed class ClaudeCli
{
    public const string McpName = "pec-qc";
    private readonly string _claude;

    public ClaudeCli(string claudePath) => _claude = claudePath;

    public static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), name));
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---- sign-in

    public async Task<ClaudeAuth> StatusAsync(CancellationToken ct = default)
    {
        var (code, outp, err) = await RunAsync(["auth", "status"], null, TimeSpan.FromSeconds(30), ct);
        return ParseStatus(outp, code == 0 ? null : (err.Length > 0 ? err : $"claude exited with {code}"));
    }

    public static ClaudeAuth ParseStatus(string json, string? error = null)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var exp = S("loginExpiresAt");
            return new ClaudeAuth(r.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True,
                S("authMethod"), S("email"), S("orgName") ?? S("organization"),
                DateTime.TryParse(exp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var e) ? e : null, error);
        }
        catch (JsonException) { return new ClaudeAuth(false, null, null, null, null, error ?? "Could not read the sign-in status."); }
    }

    /// <summary>Opens the browser sign-in of Claude Code and waits for it to finish (up to 5 minutes).</summary>
    public async Task<ClaudeAuth> LoginAsync(CancellationToken ct = default)
    {
        await RunAsync(["auth", "login", "--claudeai"], null, TimeSpan.FromMinutes(5), ct);
        return await StatusAsync(ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default) =>
        await RunAsync(["auth", "logout"], null, TimeSpan.FromSeconds(30), ct);

    // ---- chat

    /// <param name="mcpConfigFile">JSON file from <see cref="WriteMcpConfig"/>.</param>
    /// <param name="replaceDefaultPrompt">true: our prompt replaces the coding-oriented default prompt of Claude Code (far fewer tokens per request); false: ours is appended to it.</param>
    /// <summary>The only commands refused even at full autonomy: the ones that wipe a disk or switch the PC off. Everything else the user can do, Claude can do.</summary>
    public static readonly string[] AlwaysRefused = ["Bash(format:*)", "Bash(diskpart:*)", "Bash(mkfs:*)", "Bash(shutdown:*)"];

    public static List<string> ChatArguments(string mcpConfigFile, string systemPrompt, string? sessionId, string? model = null, bool replaceDefaultPrompt = true, string? effort = null,
        string? readableFolder = null, ClaudeAutonomy autonomy = ClaudeAutonomy.Careful, string? workspace = null, IEnumerable<string>? extraServers = null)
    {
        // What Claude may use besides the QC tools:
        //  Careful  - nothing, except Read limited by a path rule to the chat attachments folder
        //  Standard - read files, write and edit inside its own workspace, search the web, keep a to-do list
        //  Full     - also write anywhere the user can and run commands, so it can build what is missing and test it (only disk-wiping and shutdown commands stay refused)
        var allowed = new List<string> { $"mcp__{McpName}__*" };
        // the MCP servers installed from Settings (AutoCAD, Navisworks, Revit ...): the chat may use them
        if (extraServers is not null) allowed.AddRange(extraServers.Where(n => n.Length > 0 && n != McpName).Select(n => $"mcp__{n}__*"));
        string tools;
        switch (autonomy)
        {
            case ClaudeAutonomy.Full:
                tools = "Read,Write,Edit,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite";
                allowed.AddRange(["Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch", "TodoWrite"]);
                break;
            case ClaudeAutonomy.Standard:
                tools = "Read,Write,Edit,Glob,Grep,WebFetch,WebSearch,TodoWrite";
                allowed.AddRange(["Read", "Glob", "Grep", "WebFetch", "WebSearch", "TodoWrite"]);
                if (workspace is not null) { allowed.Add(Claude.ChatAttachments.PathRule("Write", workspace)); allowed.Add(Claude.ChatAttachments.PathRule("Edit", workspace)); }
                break;
            default:
                tools = readableFolder is null ? "" : "Read";
                if (readableFolder is not null) allowed.Add(Claude.ChatAttachments.ReadRule(readableFolder));
                break;
        }
        var a = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--mcp-config", mcpConfigFile, "--strict-mcp-config", "--tools", tools, "--allowedTools" };
        a.AddRange(allowed);
        if (autonomy == ClaudeAutonomy.Full) { a.Add("--disallowedTools"); a.AddRange(AlwaysRefused); }
        a.AddRange(["--disable-slash-commands", replaceDefaultPrompt ? "--system-prompt" : "--append-system-prompt", systemPrompt]);
        if (!string.IsNullOrEmpty(sessionId)) { a.Add("--resume"); a.Add(sessionId); }
        if (!string.IsNullOrEmpty(model)) { a.Add("--model"); a.Add(model); }
        if (!string.IsNullOrEmpty(effort)) { a.Add("--effort"); a.Add(effort); }
        return a;
    }

    public static string WriteMcpConfig(string directory, string mcpExe, IDictionary<string, string>? env = null, IReadOnlyDictionary<string, McpInstall.McpServerEntry>? extra = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mcp.json");
        var server = new Dictionary<string, object> { ["command"] = mcpExe, ["args"] = Array.Empty<string>() };
        if (env is { Count: > 0 }) server["env"] = env;
        var all = new Dictionary<string, object>();
        if (extra is not null)
            foreach (var (name, e) in extra.Where(x => x.Key != McpName && x.Value.Command.Length > 0))
            {
                var d = new Dictionary<string, object> { ["command"] = e.Command, ["args"] = e.Args ?? [] };
                if (e.Env is { Count: > 0 }) d["env"] = e.Env;
                all[name] = d;
            }
        all[McpName] = server;
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = all }));
        return path;
    }

    /// <summary>Send one prompt (follow-ups pass the session id of the first turn). Events are reported as they arrive.</summary>
    public async Task<ChatEvent.Finished> ChatAsync(string prompt, string mcpConfigFile, string systemPrompt, string? sessionId,
        Action<ChatEvent> onEvent, string? workingDirectory = null, IDictionary<string, string>? env = null, CancellationToken ct = default,
        string? model = null, string? effort = null, ClaudeAutonomy autonomy = ClaudeAutonomy.Careful)
    {
        var psi = NewStartInfo(ChatArguments(mcpConfigFile, systemPrompt, sessionId, model, true, effort,
            workingDirectory is null ? null : Path.Combine(workingDirectory, "attachments"), autonomy, workingDirectory, McpInstall.McpServerRegistry.ForDataDirectory().Load().Keys), workingDirectory);
        psi.RedirectStandardInput = true;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Claude Code did not start.");
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();

        using var reg = ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        ChatEvent.Finished? finished = null;
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            foreach (var ev in ChatStream.Parse(line))
            {
                onEvent(ev);
                if (ev is ChatEvent.Finished f) finished = f;
            }
        await p.WaitForExitAsync(ct);
        return finished ?? new ChatEvent.Finished(false,
            stderr.Length > 0 ? stderr.ToString().Trim() : "Claude Code ended without an answer.", sessionId, null, null);
    }

    // ---- process helpers

    private ProcessStartInfo NewStartInfo(IEnumerable<string> args, string? workingDirectory)
    {
        var psi = _claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", _claude } }
            : new ProcessStartInfo(_claude);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        return psi;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(IEnumerable<string> args, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var psi = NewStartInfo(args, null);
        psi.RedirectStandardInput = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Claude Code did not start.");
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } if (ct.IsCancellationRequested) throw; }
        return (p.HasExited ? p.ExitCode : -1, await o, await e);
    }
}

/// <summary>Turns one line of <c>--output-format stream-json</c> into chat events.</summary>
public static class ChatStream
{
    public static IEnumerable<ChatEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { yield break; }
        using (doc)
        {
            var r = doc.RootElement;
            var type = Str(r, "type");
            if (type == "system" && Str(r, "subtype") == "init")
            {
                var servers = r.TryGetProperty("mcp_servers", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(s => $"{Str(s, "name")}:{Str(s, "status")}").ToList() : [];
                yield return new ChatEvent.Started(Str(r, "session_id") ?? "", servers);
            }
            else if (type is "assistant" or "user" && r.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in content.EnumerateArray())
                {
                    switch (Str(c, "type"))
                    {
                        case "text" when type == "assistant" && Str(c, "text") is { Length: > 0 } t:
                            yield return new ChatEvent.Text(t); break;
                        case "tool_use":
                            var name = (Str(c, "name") ?? "").Replace($"mcp__{ClaudeCli.McpName}__", "");
                            if (name is "ToolSearch") break;                               // internal lookup, not interesting to the user
                            yield return new ChatEvent.ToolUse(name, c.TryGetProperty("input", out var i) ? i.GetRawText() : "{}"); break;
                        case "tool_result":
                            var text = c.TryGetProperty("content", out var cc) ? ResultText(cc) : "";
                            if (text.Length > 0 && !text.StartsWith("[{\"type\":\"tool_reference", StringComparison.Ordinal))
                                yield return new ChatEvent.ToolResult(text, c.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True);
                            break;
                    }
                }
            }
            else if (type == "result")
            {
                yield return new ChatEvent.Finished(
                    Str(r, "subtype") == "success" && !(r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True),
                    Str(r, "result") ?? "", Str(r, "session_id"),
                    r.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDouble(out var cd) ? cd : null,
                    r.TryGetProperty("num_turns", out var nt) && nt.TryGetInt32(out var ntv) ? ntv : null,
                    r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? new TokenUsage(Int(u, "input_tokens"), Int(u, "output_tokens"), Int(u, "cache_read_input_tokens"), Int(u, "cache_creation_input_tokens")) : null);
            }
        }
    }

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string ResultText(JsonElement c) =>
        c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" :
        c.ValueKind == JsonValueKind.Array
            ? string.Join("\n", c.EnumerateArray().Select(x => Str(x, "text") ?? x.GetRawText()))
            : c.GetRawText();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

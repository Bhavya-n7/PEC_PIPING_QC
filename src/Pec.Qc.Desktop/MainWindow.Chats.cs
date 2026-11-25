using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pec.Qc.Core;
using Pec.Qc.Core.Approvals;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Desktop;

/// <summary>Chats: history, several chats at once, sending, attachments and approvals.</summary>
public partial class MainWindow
{
    private readonly ChatHistory _history = new();
    private readonly Dictionary<string, ChatSession> _open = new();
    private ChatSession _cur = new();

    // the chat on screen owns the busy flag, the attached files and the batch name
    private bool _busy => _cur.Busy;
    private List<Attachment> _files => _cur.Files;
    private string _batch { get => _cur.Batch; set => _cur.Batch = value; }

    private string? _mcpExe;
    private UserIdentity Who => UserIdentity.Current(_settings);

    /// <summary>First start: ask who is using the assistant (name, employee code, company). Everything is logged against it.</summary>
    private void EnsureIdentity()
    {
        if (UserIdentity.FromSettings(_settings).IsComplete) return;
        var who = IdentityWindow.Ask(this, UserIdentity.FromSettings(_settings));
        if (who is null) return;
        _settings.UserName = who.Name; _settings.EmployeeCode = who.EmployeeCode; _settings.Company = who.Company;
        try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The tools Claude starts get the confirmation mode and who the user is; written again whenever the settings change.</summary>
    private void WriteMcpConfigFile()
    {
        if (_mcpExe is null) return;
        var env = new Dictionary<string, string> { ["PEC_QC_APPROVAL"] = ApprovalGate.ModeToVariable(_settings.EffectiveApprovalMode), ["PEC_QC_DATA"] = Settings.DataDirectory };
        foreach (var kv in UserIdentity.FromSettings(_settings).ToEnvironment()) env[kv.Key] = kv.Value;
        _mcpConfig = ClaudeCli.WriteMcpConfig(Path.Combine(Settings.DataDirectory, "claude"), _mcpExe, env, Pec.Qc.Core.McpInstall.McpServerRegistry.ForDataDirectory().Load());
    }

    /// <summary>One line in the team's activity log: who asked what, what the assistant did, how it ended.</summary>
    private void Log(ChatSession s, string type, string text, string? tool = null, bool? ok = null, string? detail = null)
    {
        try { ActivityLog.Write(_settings, Who, type, text, tool, ok, s.Data.Id, _profiles.Active?.Name, detail); }
        catch (Exception) { /* a log problem must never stop the work */ }
    }

    private const string ReflectionPrompt =
        "[Internal step, not from the user] Save what you learned, or say nothing new. " +
        "If something went wrong or took several tries: learn_record it (kind correction, gotcha, recipe or failure; plain words, no secrets). " +
        "If you built a procedure that can be reused: save it with create_custom_tool and a short how-to-use. Do not repeat your answer; reply with ONE short line of what you saved, or 'nothing new'.";

    private void InitChats()
    {
        try { _history.Prune(); } catch (IOException) { }
        Closing += (_, _) => { foreach (var s in _open.Values) s.Cts?.Cancel(); _cur.Cts?.Cancel(); };
        ShowSession(_cur);
    }

    // ------------------------------------------------------------------ sessions

    private ChatSession NewSession() => new(new StoredChat { Project = _profiles.Active?.Name });

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (!_cur.Started && !_cur.Busy && _cur.Files.Count == 0 && Prompt.Text.Length == 0) { GoAssistant(); return; }   // already on an empty chat
        _gate.EndTask();                 // a new chat is a new task: ask again for the first change
        ShowSession(NewSession());
    }

    /// <summary>Starts a fresh chat and shows a notice on its welcome page (used when the project or the folders change).</summary>
    private void NewConversation(string? message)
    {
        _gate.EndTask();
        var s = NewSession();
        ShowSession(s);
        if (message is not null) Say(s, Role.System, message);
    }

    private void ShowSession(ChatSession s)
    {
        if (_cur != s) _cur.Draft = Prompt.Text;
        _cur = s;
        s.Unread = false;
        Notices.Children.Clear();
        if (s.Started)
        {
            WelcomeScroll.Visibility = Visibility.Collapsed;
            ChatScroll.Content = s.Panel; ChatScroll.Visibility = Visibility.Visible;
            ChatScroll.ScrollToEnd();
        }
        else
        {
            ChatScroll.Content = null; ChatScroll.Visibility = Visibility.Collapsed;
            WelcomeScroll.Visibility = Visibility.Visible;
            GreetingText.Text = Greeting();
        }
        Prompt.Text = s.Draft;
        RenderChips();
        UpdateInput();
        RefreshChatList();
        GoAssistant();
        Prompt.Focus();
    }

    /// <summary>Opens a chat from the history (or switches to it when it is already open, even while Claude is working in it).</summary>
    private void OpenChat(string id)
    {
        if (_open.TryGetValue(id, out var s)) { ShowSession(s); return; }
        var data = _history.Load(id);
        if (data is null) { RefreshChatList(); return; }
        s = new ChatSession(data); _open[id] = s;
        if (data.Messages.Count > 0)
        {
            ShowChat(s);                       // it is not the chat on screen yet: this only marks it started
            foreach (var m in data.Messages)
                Say(s, m.Role switch { "user" => Role.User, "assistant" => Role.Assistant, _ => Role.System }, m.Text, persist: false);
        }
        ShowSession(s);
    }

    private void Persist(ChatSession s, Role role, string text)
    {
        var d = s.Data;
        d.Messages.Add(new StoredMessage { Role = role.ToString().ToLowerInvariant(), Text = text, Time = DateTime.Now });
        d.Updated = DateTime.Now;
        if (role == Role.User && !d.TitleChosenByUser && d.Messages.Count(m => m.Role == "user") == 1) d.Title = ChatHistory.TitleFrom(text);
        SaveChat(s);
    }

    private void SaveChat(ChatSession s)
    {
        if (s.Data.Messages.Count == 0) return;
        try { _history.Save(s.Data); _open[s.Data.Id] = s; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the chat still works; it just is not saved this time */ }
        RefreshChatList();
    }

    // ------------------------------------------------------------------ history list

    private void ChatSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ChatSearchHint is not null) ChatSearchHint.Visibility = ChatSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshChatList();
    }

    private void RefreshChatList()
    {
        if (ChatList is null) return;
        var q = ChatSearch?.Text.Trim() ?? "";
        var items = (q.Length == 0 ? _history.List() : _history.Search(q)).ToList();
        ChatList.Children.Clear();
        if (items.Count == 0)
        {
            ChatList.Children.Add(new TextBlock { Text = q.Length == 0 ? "Your chats will appear here." : "No chats match.", Foreground = Res("SidebarMuted"), FontSize = 12, Margin = new Thickness(12, 6, 12, 6), TextWrapping = TextWrapping.Wrap });
            return;
        }
        string? last = null;
        foreach (var it in items)
        {
            var g = ChatHistory.Group(it.Updated, DateTime.Now);
            if (g != last)
            {
                ChatList.Children.Add(new TextBlock { Text = g.ToUpperInvariant(), Style = (Style)FindResource("Eyebrow"), Foreground = Res("SidebarMuted"), Margin = new Thickness(12, last is null ? 2 : 12, 0, 4) });
                last = g;
            }
            ChatList.Children.Add(ChatRow(it));
        }
    }

    private Button ChatRow(ChatSummary it)
    {
        var selected = it.Id == _cur.Data.Id;
        _open.TryGetValue(it.Id, out var live);
        FrameworkElement? mark = null;
        if (live is { Busy: true })
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Res("Accent"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Claude is working in this chat" };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            mark = dot;
        }
        else if (live is { Unread: true })
            mark = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Res("Good"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "New answer" };

        var row = new DockPanel { LastChildFill = true };
        if (mark is not null) { DockPanel.SetDock(mark, Dock.Right); mark.Margin = new Thickness(8, 0, 4, 0); row.Children.Add(mark); }
        row.Children.Add(new TextBlock { Text = it.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res("SidebarInk"), FontSize = 13 });

        var btn = new Button
        {
            Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 7, 8, 7), Margin = new Thickness(0, 1, 0, 1),
            Background = selected ? Res("SidebarActive") : Brushes.Transparent, Content = row, Tag = it.Id,
            ToolTip = $"{it.Title}\n{it.Updated:ddd d MMM, HH:mm} · {it.Messages} messages",
        };
        btn.Click += (_, _) => OpenChat(it.Id);
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename" }; rename.Click += (_, _) => RenameChat(it.Id, it.Title);
        var delete = new MenuItem { Header = "Delete" }; delete.Click += (_, _) => DeleteChat(it.Id, it.Title);
        menu.Items.Add(rename); menu.Items.Add(delete);
        btn.ContextMenu = menu;
        return btn;
    }

    private void RenameChat(string id, string current)
    {
        var name = TextPrompt.Ask(this, "Rename chat", "Chat name", current);
        if (name is null) return;
        _history.Rename(id, name);
        if (_open.TryGetValue(id, out var s)) { s.Data.Title = name; s.Data.TitleChosenByUser = true; }
        RefreshChatList();
    }

    private void DeleteChat(string id, string title)
    {
        if (MessageBox.Show(this, $"Delete the chat \"{title}\"? This cannot be undone.", "Delete chat", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var wasCurrent = _cur.Data.Id == id;
        if (_open.TryGetValue(id, out var s)) { s.Cts?.Cancel(); _open.Remove(id); }
        _history.Delete(id);
        if (wasCurrent) ShowSession(NewSession()); else RefreshChatList();
    }

    // ------------------------------------------------------------------ chat

    private void Prompt_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; Send_Click(sender, e); }
        else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && PasteAttachment()) e.Handled = true;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = Prompt.Text.Trim();
        if (text.Length == 0 && _files.Count == 0) return;
        if (text.Length == 0) text = "Please open the attached file" + (_files.Count == 1 ? "" : "s") + " and tell me what " + (_files.Count == 1 ? "it contains" : "they contain") + ".";
        Prompt.Clear();
        var files = _files.ToList(); _files.Clear(); RenderChips();
        await SendAsync(text, files);
    }

    /// <summary>Sends in the chat on screen; when Claude is still busy there (a quick task, a button) the message opens a new chat instead of waiting.</summary>
    private async Task SendAsync(string text, IReadOnlyList<Attachment>? files = null)
    {
        var s = _cur;
        if (s.Busy) { _gate.EndTask(); s = NewSession(); ShowSession(s); }
        await SendAsync(s, text, files);
    }

    private async Task SendAsync(ChatSession s, string text, IReadOnlyList<Attachment>? files = null)
    {
        if (s.Busy || _claude is null || _mcpConfig is null) return;
        WriteMcpConfigFile();                      // servers installed from Settings since the last message are included
        files ??= [];
        Say(s, Role.User, files.Count == 0 ? text : text + "\n\nAttached: " + string.Join(", ", files.Select(f => f.Name)));
        s.TurnTools = 0; s.TurnErrors = 0; s.TurnBuilt = false; s.TurnLearned = false; s.TurnSavedTool = false; s.Reflecting = false;
        s.TurnCorrected = Corrections.LooksLikeCorrection(text);
        Log(s, "prompt", files.Count == 0 ? text : text + "\n[attached: " + string.Join(", ", files.Select(f => f.Name)) + "]", detail: s.TurnCorrected ? "looks like a correction" : null);
        s.Batch = ChatAttachments.Batch();
        s.Busy = true; if (s == _cur) UpdateInput();
        RefreshChatList();
        s.Cts = new CancellationTokenSource();
        var thinking = Thinking(s); s.Thinking = thinking;
        try
        {
            // short, stable system prompt: the rules and skills are read on demand with the guidance tool
            var system = PromptBuilder.Build(_settings, _profiles.Active);
            var prompt = text + ChatAttachments.PromptNote(files);
            var sid = s.Data.ClaudeSessionId;
            var fin = await RunClaude(s, prompt, system, sid, thinking);
            if (!fin.Ok && sid is not null && Regex.IsMatch(fin.Result ?? "", @"(?i)no conversation found|could not (resume|find)|session .{0,40}not found"))
            {
                // an old chat whose Claude session is gone: carry on from the saved text instead
                Say(s, Role.System, "The earlier Claude session is no longer available, so this chat continues from its saved text.");
                s.Data.ClaudeSessionId = null;
                var before = new StoredChat { Messages = s.Data.Messages.Take(Math.Max(0, s.Data.Messages.Count - 2)).ToList() };
                fin = await RunClaude(s, ChatHistory.ContextFor(before) + prompt, system, null, thinking);
            }
            s.Data.ClaudeSessionId = fin.SessionId ?? s.Data.ClaudeSessionId;
            if (fin.Usage is { } u) TokenStatus.Text = $"{u.TotalInput:N0} in · {u.CachedShare:P0} cached · {u.Output:N0} out";
            Log(s, "done", fin.Ok ? "finished" : "failed: " + fin.Result, ok: fin.Ok, detail: $"tools {s.TurnTools}, errors {s.TurnErrors}" + (fin.Usage is { } uu ? $", tokens in {uu.TotalInput} out {uu.Output}" : ""));
            if (!fin.Ok) Say(s, Role.System, "Something went wrong: " + fin.Result);
            else await ReflectAsync(s, system, thinking);
        }
        catch (OperationCanceledException) { Say(s, Role.System, "Stopped."); Log(s, "stopped", "stopped by the user"); }
        catch (Exception ex) { Say(s, Role.System, "Could not run Claude Code: " + ex.Message); Log(s, "error", ex.Message, ok: false); }
        finally
        {
            s.Panel.Children.Remove(thinking);
            s.Step = null; s.Thinking = null;
            s.Busy = false; s.Cts = null;
            if (s == _cur) { UpdateInput(); Prompt.Focus(); } else s.Unread = true;
            SaveChat(s);
            RefreshShared();                // a tool made in this chat is copied to the shared folder
            await RefreshAuthAsync();      // a session that expired shows up here
        }
    }

    /// <summary>After a task that had errors, built something, was corrected or was long: one more short turn in the same session where the assistant records what it
    /// learned and saves any tool it made, so the next task (and every colleague) starts smarter. Skipped when it already did that during the task.</summary>
    private async Task ReflectAsync(ChatSession s, string system, FrameworkElement thinking)
    {
        if (!_settings.SelfReflect || s.Cts is null || s.Cts.IsCancellationRequested) return;
        // only for work that was long or hit errors, and only when nothing was saved during it; short requests are skipped
        var long_ = s.TurnTools >= 4 || (s.TurnErrors > 0 && s.TurnTools >= 2) || s.TurnCorrected || s.TurnBuilt;
        var saved = s.TurnLearned || s.TurnSavedTool;
        Pec.Qc.Core.Util.PathDiagnostics.Note("save-pass", $"tools {s.TurnTools}, errors {s.TurnErrors}, corrected {s.TurnCorrected}, built {s.TurnBuilt}, saved during task {saved} -> " + (long_ && !saved ? "asking" : "skipped"));
        if (!long_ || saved) return;
        s.Reflecting = true;
        try
        {
            var fin = await RunClaude(s, ReflectionPrompt, system, s.Data.ClaudeSessionId, thinking);
            s.Data.ClaudeSessionId = fin.SessionId ?? s.Data.ClaudeSessionId;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(s, "error", "reflection failed: " + ex.Message, ok: false); }
        finally { s.Reflecting = false; }
    }

    private Task<ChatEvent.Finished> RunClaude(ChatSession s, string prompt, string system, string? sessionId, FrameworkElement thinking) =>
        _claude!.ChatAsync(prompt, _mcpConfig!, system, sessionId, ev => Dispatcher.Invoke(() => OnEvent(s, ev, thinking)), _work, null, s.Cts!.Token,
            ClaudeChoices.ModelOrDefault(_settings.Model), ClaudeChoices.EffortOrDefault(_settings.Effort), _settings.EffectiveAutonomy);

    private void Stop_Click(object sender, RoutedEventArgs e) => _cur.Cts?.Cancel();

    private void OnEvent(ChatSession s, ChatEvent ev, FrameworkElement thinking)
    {
        switch (ev)
        {
            case ChatEvent.Started st when st.McpServers.Any(m => !m.EndsWith(":connected")):
                Say(s, Role.System, "The QC tools did not start: " + string.Join(", ", st.McpServers)); break;
            case ChatEvent.Text t:
                Log(s, s.Reflecting ? "learned" : "answer", t.Content);
                s.Panel.Children.Remove(thinking);
                if (s.Reflecting) { if (!t.Content.Trim().StartsWith("nothing new", StringComparison.OrdinalIgnoreCase)) Say(s, Role.System, "Learned: " + Trim(t.Content.Trim(), 240), persist: false); }
                else Say(s, Role.Assistant, t.Content);
                s.Panel.Children.Add(thinking); break;
            case ChatEvent.ToolUse u:
                s.TurnTools++; s.LastTool = u.Name;
                if (u.Name.EndsWith("create_custom_tool") || u.Name.EndsWith("save_skill")) s.TurnBuilt = true;
                if (u.Name.EndsWith("create_custom_tool")) s.TurnSavedTool = true;
                if (u.Name.EndsWith("learn_record")) s.TurnLearned = true;
                Log(s, "tool", u.Name, u.Name, detail: Trim(u.Input, 500));
                if (!s.Reflecting) BeginStep(s, u.Name, Pretty(u.Input));
                s.Panel.Children.Remove(thinking); s.Panel.Children.Add(thinking); break;
            case ChatEvent.ToolResult r:
                if (r.IsError) s.TurnErrors++;
                Log(s, "result", Trim(r.Content, 600), s.LastTool, !r.IsError);
                if (!s.Reflecting) EndStep(s, r.IsError, Trim(r.Content, 6000));
                break;
        }
        if (s == _cur) ChatScroll.ScrollToEnd();
    }

    // ------------------------------------------------------------------ approvals

    private void PollApprovals()
    {
        foreach (var req in _gate.Pending())
        {
            if (!_asked.Add(req.Id)) continue;
            // the request comes from whichever chat Claude is working in; tell the user there
            var target = _open.Values.FirstOrDefault(x => x.Busy) ?? _cur;
            var mode = _settings.EffectiveApprovalMode;
            if (mode == ApprovalMode.Off) { _gate.Approve(req.Id); continue; }              // asked before the setting was changed: just let it through
            var dlg = new ApprovalWindow(req, mode == ApprovalMode.Task && !req.Destructive) { Owner = this };
            var ok = dlg.ShowDialog() == true;
            if (ok) { _gate.Approve(req.Id, openTaskWindow: mode == ApprovalMode.Task); Say(target, Role.System, $"Approved: {req.Summary}"); }
            else { _gate.Deny(req.Id); Say(target, Role.System, $"Denied: {req.Summary}."); }
            Log(target, "approval", req.Summary, req.Tool, ok);
            _asked.Remove(req.Id);      // the same action may be requested again later
        }
    }

    // ------------------------------------------------------------------ attachments

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files", Multiselect = true, CheckFileExists = true,
            Filter = "Documents and pictures|*.pdf;*.docx;*.xlsx;*.xlsm;*.pptx;*.csv;*.tsv;*.txt;*.md;*.png;*.jpg;*.jpeg;*.gif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Composer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
        e.Handled = true;
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var p in paths)
        {
            if (_files.Count >= ChatAttachments.MaxFiles) { problems.Add($"Only {ChatAttachments.MaxFiles} files can be attached to one message."); break; }
            if (Directory.Exists(p)) { problems.Add($"'{Path.GetFileName(p)}' is a folder: attach files, or name the folder in your message."); continue; }
            try { _files.Add(ChatAttachments.Stage(p, _work, _batch)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { problems.Add(ex.Message); }
        }
        RenderChips();
        if (problems.Count > 0) MessageBox.Show(this, string.Join("\n", problems.Distinct()), "Attach files", MessageBoxButton.OK, MessageBoxImage.Information);
        Prompt.Focus();
    }

    /// <summary>Ctrl+V with a picture (a screenshot) or copied files on the clipboard attaches them instead of pasting text.</summary>
    private bool PasteAttachment()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) { AddFiles(Clipboard.GetFileDropList().Cast<string>().ToList()); return true; }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                using var ms = new MemoryStream(); enc.Save(ms);
                _files.Add(ChatAttachments.StageBytes(ms.ToArray(), $"pasted-image-{DateTime.Now:HHmmss}.png", _work, _batch));
                RenderChips(); return true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidDataException) { /* clipboard busy: fall back to normal paste */ }
        return false;
    }

    private void RenderChips()
    {
        AttachBar.Children.Clear();
        foreach (var f in _files.ToList())
        {
            var file = f;
            var remove = new Button { Content = "✕", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 0, 6, 0), FontSize = 11, ToolTip = "Remove", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { _files.Remove(file); RenderChips(); };
            var label = new TextBlock { Text = f.Name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Name };
            var size = new TextBlock { Text = ChatAttachments.Size(f.Size), Style = (Style)FindResource("Muted"), FontSize = 11, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            AttachBar.Children.Add(new Border
            {
                Background = Res("AccentSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 8, 6),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, size, remove } },
            });
        }
        AttachBar.Visibility = _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

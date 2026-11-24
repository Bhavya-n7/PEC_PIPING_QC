using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pec.Qc.Core;
using Pec.Qc.Core.Approvals;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Install;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Setup;
using Pec.Qc.Core.Tools;

namespace Pec.Qc.Desktop;

public partial class MainWindow : Window
{
    private ClaudeCli? _claude;
    private string? _mcpConfig;
    private bool _signedIn;
    private bool _loading;
    private bool _helpBuilt;
    private ProjectStore _profiles = new();
    private Settings _settings = Settings.Load();
    private readonly ApprovalGate _gate = new(mode: () => ApprovalMode.Task);   // replaced from the settings at start-up
    private readonly HashSet<string> _asked = [];
    private readonly DispatcherTimer _approvalTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _work = Path.Combine(Settings.DataDirectory, "workspace");
    private UpdateCheck? _update;
    private List<(string Title, Block Target)> _helpToc = [];

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => FitAllCards();
        Loaded += (_, _) => FitAllCards();
        Loaded += async (_, _) => await StartAsync();
        Loaded += (_, _) => _ = Task.Run(PullSharedKit);
        _approvalTimer.Tick += (_, _) => PollApprovals();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    // ------------------------------------------------------------------ start up

    private async Task StartAsync()
    {
        Directory.CreateDirectory(_work);
        try { Pec.Qc.Core.Claude.ChatAttachments.Cleanup(_work); } catch (Exception) { /* old attachments are only housekeeping */ }
        VersionText.Text = "Version " + Updates.CurrentVersion;
        Title = "PEC QC Assistant " + Updates.CurrentVersion;
        _settings = Settings.Load();
        EnsureIdentity();
        var firstAdmin = RegisterUser();
        Pec.Qc.Core.Util.PathDiagnostics.Snapshot(_settings);     // where this PC reads and writes lessons, tools, activity
        RefreshModelPickers();
        _ = CheckUpdateAsync();
        try { PromptBuilder.SeedDefaults(_settings, AppContext.BaseDirectory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say(Role.System, "Could not prepare the rules and skills folders: " + e.Message); }

        RefreshNavis();
        RefreshInputs();
        RefreshShared();
        RefreshProfiles();
        RefreshTasks();
        GreetingText.Text = Greeting();
        InitChats();

        _mcpExe = AssistantSetup.FindMcpExe(AppContext.BaseDirectory);
        if (_mcpExe is null) Say(Role.System, "The QC tools (mcp\\Pec.Qc.Mcp.exe) were not found next to the app. Run Setup again to repair the installation.");
        else WriteMcpConfigFile();

        var path = ClaudeCli.Find();
        if (path is null)
        {
            SetStatus(ClaudeDot, ClaudeText, Light.Bad, "Claude Code missing", "Run Setup with 'Install Claude Code' ticked, or install it from https://claude.com/claude-code");
            Say(Role.System, "Claude Code is not installed on this PC. Run Setup again with \"Install Claude Code\" ticked, or install it from https://claude.com/claude-code, then restart this app.");
            SetSignedIn(false, false);
            return;
        }
        _claude = new ClaudeCli(path);
        await RefreshAuthAsync();
        _approvalTimer.Start();
        if (firstAdmin && _settings.HasShared) Say(Role.System, "You are the first person to register on this shared folder, so you are its administrator: you can approve shared items and change other people's roles. Everyone who comes after you starts as a QC engineer.");
        if (_signedIn && _profiles.Active is null)
            Say(Role.System, "Start by adding your project: open Projects and press Add project. Then pick a quick task or type a request.");
        Prompt.Focus();
    }

    /// <summary>Lists this user in the shared registry (name, code, role, version, last seen). True when this person just became the administrator because nobody was yet.</summary>
    private bool RegisterUser()
    {
        try
        {
            var who = Who;
            if (!who.IsComplete) return false;
            var rec = Pec.Qc.Core.Users.UserRegistry.Touch(_settings, who);
            return rec.Sessions == 1 && rec.RoleValue == Pec.Qc.Core.Users.QcRole.Admin;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }   // the registry is a convenience: never stop the app for it
    }

    private static string Greeting() => DateTime.Now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    private async Task RefreshAuthAsync()
    {
        if (_claude is null) return;
        var a = await _claude.StatusAsync();
        SetSignedIn(a.LoggedIn, true, a);
    }

    private void RefreshNavis()
    {
        var nw = NavisworksInstall.Find(_settings.NavisworksHome);
        SetStatus(NavisDot, NavisText, nw is null ? Light.Warn : Light.Good, nw is null ? "Navisworks not found" : nw.Name,
            nw is null ? "Needed to read the 3D model for isometric and GA checks. Set its folder in Settings if it is installed in a different place." : nw.Root);
    }

    /// <summary>The active project's inputs folder (PMS, line list, P&IDs, vendor data): present and how many files it holds.</summary>
    private void RefreshInputs()
    {
        var p = _profiles.Active;
        if (p is null) { SetStatus(InputsDot, InputsText, Light.Off, "No project", "Add a QC project (Projects)."); return; }
        if (string.IsNullOrWhiteSpace(p.InputsFolder)) { SetStatus(InputsDot, InputsText, Light.Warn, "No inputs folder", "Edit the project and set the folder with the PMS, line list, P&IDs and vendor data."); return; }
        try
        {
            if (!Directory.Exists(p.InputsFolder)) { SetStatus(InputsDot, InputsText, Light.Bad, "Inputs folder missing", p.InputsFolder); return; }
            var n = Directory.GetFiles(p.InputsFolder, "*", SearchOption.AllDirectories).Length;
            SetStatus(InputsDot, InputsText, n > 0 ? Light.Good : Light.Warn, n > 0 ? $"Project inputs: {n} files" : "Project inputs: empty", p.InputsFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetStatus(InputsDot, InputsText, Light.Bad, "Inputs folder unreachable", p.InputsFolder); }
    }

    private enum Light { Off, Good, Warn, Bad }

    private void SetStatus(System.Windows.Shapes.Ellipse dot, TextBlock text, Light kind, string label, string? tip = null)
    {
        dot.Fill = kind switch { Light.Good => Res("Good"), Light.Warn => Res("Warn"), Light.Bad => Res("Bad"), _ => new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0xB0)) };
        text.Text = label;
        text.ToolTip = tip;
    }

    private void SetSignedIn(bool signedIn, bool cliFound, ClaudeAuth? a = null)
    {
        _signedIn = signedIn;
        if (cliFound) SetStatus(ClaudeDot, ClaudeText, signedIn ? Light.Good : Light.Warn, signedIn ? "Claude signed in" : "Claude: sign in", a?.Email);
        SignInButton.Visibility = cliFound && !signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutButton.Visibility = cliFound && signedIn ? Visibility.Visible : Visibility.Collapsed;
        UpdateInput();
    }

    private void UpdateInput()
    {
        Prompt.IsEnabled = _signedIn && !_busy && _mcpConfig is not null;
        SendButton.IsEnabled = Prompt.IsEnabled;
        SendButton.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        void Enable(Panel? p) { if (p is null) return; foreach (var b in AllButtons(p)) if (b.Tag is QuickTask) b.IsEnabled = Prompt.IsEnabled; }
        Enable(WelcomeTasks); Enable(TasksPanel);
    }

    private static IEnumerable<Button> AllButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button b) yield return b;
            foreach (var d in AllButtons(child)) yield return d;
        }
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (AssistantView is null) return;           // fired while the window is still being built
        var which = sender == NavProjects ? "projects" : sender == NavTasks ? "tasks" : sender == NavKnowledge ? "knowledge" : sender == NavHelp ? "help" : "assistant";
        FitAllCards();
        AssistantView.Visibility = which == "assistant" ? Visibility.Visible : Visibility.Collapsed;
        ProjectsView.Visibility = which == "projects" ? Visibility.Visible : Visibility.Collapsed;
        TasksView.Visibility = which == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        KnowledgeView.Visibility = which == "knowledge" ? Visibility.Visible : Visibility.Collapsed;
        HelpView.Visibility = which == "help" ? Visibility.Visible : Visibility.Collapsed;
        if (which == "projects") BuildProjects();
        if (which == "knowledge") BuildKnowledge();
        if (which == "help") BuildHelp();
        if (which == "assistant") Prompt?.Focus();
    }

    private void GoAssistant() => NavAssistant.IsChecked = true;
    private void ManageProjects_Click(object sender, RoutedEventArgs e) => NavProjects.IsChecked = true;

    // ------------------------------------------------------------------ project profiles

    private void RefreshProfiles()
    {
        _loading = true;
        _profiles = new ProjectStore();
        var names = _profiles.All.Select(p => p.Name).ToList();
        ProfileBox.ItemsSource = names.Count > 0 ? names : ["No project yet: add one in Projects"];
        ProfileBox.SelectedIndex = names.Count > 0 ? Math.Max(0, names.IndexOf(_profiles.Active?.Name ?? "")) : 0;
        ProfileBox.IsEnabled = names.Count > 0;
        _loading = false;
        var p = _profiles.Active;
        var line = p is null ? "" : string.Join("   Â·   ", new[] { p.Client, p.ProjectNo }.Where(x => x.Length > 0));
        ProfileLine.Text = line;
        WelcomeProjectName.Text = p?.Name ?? "No project yet";
        WelcomeProjectLine.Text = p is null ? "Add your project so the assistant knows the client, the Navisworks model and the project inputs."
            : string.Join("   Â·   ", new[] { p.Client, p.ProjectNo.Length > 0 ? "No. " + p.ProjectNo : "", p.ModelFile.Length > 0 ? Path.GetFileName(p.ModelFile) : "no model set" }.Where(x => x.Length > 0));
        RefreshInputs();
        if (ProjectsView.Visibility == Visibility.Visible) BuildProjects();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name || name == _profiles.Active?.Name) return;
        _profiles.SetActive(name);
        RefreshProfiles();
        NewConversation($"Project changed to {name}. A new chat was started so the assistant works on that project.");
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e) => EditProfile(null);

    private void EditProfile(ProjectCard? existing)
    {
        var w = new ProjectWindow(existing, _profiles.DefaultInputsFolder) { Owner = this };
        if (w.ShowDialog() != true || w.Result is null) return;
        _profiles.Save(w.Result);
        RefreshProfiles();
        NewConversation($"Project saved: {w.Result.Name}. A new chat was started so the assistant uses the new details.");
    }

    // ------------------------------------------------------------------ model and effort
    private void RefreshModelPickers()
    {
        _loading = true;
        ModelBox.Items.Clear();
        foreach (var (id, label) in ClaudeChoices.Models) ModelBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        var model = ClaudeChoices.ModelOrDefault(_settings.Model);
        var mi = ModelBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == model);
        if (mi < 0) { ModelBox.Items.Add(new ComboBoxItem { Content = model, Tag = model }); mi = ModelBox.Items.Count - 1; }   // a model typed into the settings file
        ModelBox.SelectedIndex = mi;
        EffortBox.Items.Clear();
        foreach (var e in ClaudeChoices.Efforts) EffortBox.Items.Add(new ComboBoxItem { Content = char.ToUpper(e[0]) + e[1..], Tag = e });
        EffortBox.SelectedIndex = Array.IndexOf(ClaudeChoices.Efforts, ClaudeChoices.EffortOrDefault(_settings.Effort));
        _loading = false;
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelBox.SelectedItem is not ComboBoxItem i) return;
        _settings.Model = (string)i.Tag; _settings.Save();
    }

    private void Effort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EffortBox.SelectedItem is not ComboBoxItem i) return;
        _settings.Effort = (string)i.Tag; _settings.Save();
    }

    private void BuildProjects()
    {
        ProjectsPanel.Children.Clear();
        foreach (var p in _profiles.All)
        {
            var active = _profiles.Active?.Name == p.Name;
            var card = new Border { Style = (Style)FindResource("Card"), Width = 400, Margin = new Thickness(0, 0, 16, 16) };
            if (active) card.BorderBrush = Res("Accent");
            var stack = new StackPanel();
            var head = new DockPanel();
            if (active) { var pill = Pill("Active", "AccentSoft", "Accent"); DockPanel.SetDock(pill, Dock.Right); head.Children.Add(pill); }
            if (_profiles.IsLocalOnly(p.Name)) { var lp = Pill("This PC only", "AccentSoft", "Accent"); DockPanel.SetDock(lp, Dock.Right); lp.Margin = new Thickness(0, 0, 6, 0); head.Children.Add(lp); }
            head.Children.Add(IconTile("î ¸", "AccentSoft", "Accent", 40));
            head.Children.Add(new TextBlock { Text = p.Name, Style = (Style)FindResource("H2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(head);
            var rows = new StackPanel { Margin = new Thickness(0, 14, 0, 14) };
            void Row(string glyph, string label, string value) { if (value.Length == 0) return; rows.Children.Add(InfoRow(glyph, label, value)); }
            Row("\uE77B", "Client", p.Client); Row("\uE8A5", "Number", p.ProjectNo);
            Row("\uE7B8", "Model", p.ModelFile.Length > 0 ? Path.GetFileName(p.ModelFile) : ""); Row("\uE8B7", "Inputs", p.InputsFolder);
            if (p.Details.Length > 0) rows.Children.Add(new TextBlock { Text = p.Details, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 60, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 });
            stack.Children.Add(rows);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var use = new Button { Content = active ? "In use" : "Use this project", Style = (Style)FindResource(active ? "SecondaryButton" : "PrimaryButton"), IsEnabled = !active, Padding = new Thickness(14, 7, 14, 7) };
            use.Click += (_, _) => { _profiles.SetActive(p.Name); RefreshProfiles(); NewConversation($"Project changed to {p.Name}."); };
            var edit = new Button { Content = "Edit", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
            edit.Click += (_, _) => EditProfile(p);
            var del = new Button { Content = "Delete", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(4, 0, 0, 0) };
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Delete the project '{p.Name}' from the assistant? Your project files and the shared folder are not touched.", "Delete project", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                _profiles.Delete(p.Name); RefreshProfiles();
            };
            buttons.Children.Add(use); buttons.Children.Add(edit); buttons.Children.Add(del);
            if (_profiles.IsLocalOnly(p.Name))
            {
                var share = new Button { Content = "Share with everyone", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(4, 0, 0, 0) };
                share.Click += (_, _) => { _profiles.Save(p, makeActive: false); RefreshProfiles(); };
                buttons.Children.Add(share);
            }

            stack.Children.Add(buttons);
            card.Child = stack;
            ProjectsPanel.Children.Add(card);
        }
        var add = new Button { Style = (Style)FindResource("TaskCard"), Width = 400, MinHeight = 150, Margin = new Thickness(0, 0, 16, 16) };
        add.Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children =
        {
            IconTile("îœ", "AccentSoft", "Accent", 44),
            new TextBlock { Text = "Add a project", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Name, client, Navisworks model and inputs", Style = (Style)FindResource("Muted"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
        }};
        add.Click += NewProfile_Click;
        ProjectsPanel.Children.Add(add);
    }

    // ------------------------------------------------------------------ quick tasks

    private static readonly Dictionary<string, (string Glyph, string Bg, string Fg)> CategoryLook = new()
    {
        ["Project"] = ("\uE838", "AccentSoft", "Accent"),
        ["Isometric QC"] = ("\uE9D9", "GoodSoft", "Good"),
        ["P&ID QC"] = ("\uE8A5", "WarnSoft", "Warn"),
        ["GA QC"] = ("\uE80F", "PurpleSoft", "Purple"),
        ["Project inputs"] = ("\uE8B7", "TealSoft", "Teal"),
        ["Knowledge"] = ("\uE8F1", "AccentSoft", "Accent"),
    };

    private (string Glyph, string Bg, string Fg) Look(string category) => CategoryLook.TryGetValue(category, out var l) ? l : ("î¥…", "AccentSoft", "Accent");

    private static string Describe(QuickTask t)
    {
        if (!string.IsNullOrWhiteSpace(t.Hint)) return t.Hint!;
        var s = System.Text.RegularExpressions.Regex.Replace(t.Prompt, @"\{\{\?([^}]+)\}\}", m => m.Groups[1].Value.ToLowerInvariant());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\{[a-z]+\}\}", "the project");
        var end = s.IndexOfAny(['.', '?']);
        s = end > 20 ? s[..end] : s;
        return s.Length > 96 ? s[..93] + "â€¦" : s;
    }

    private Button TaskCard(QuickTask t)
    {
        var (glyph, bg, fg) = Look(t.Category);
        var head = new DockPanel();
        head.Children.Add(IconTile(glyph, bg, fg, 34));
        head.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = Describe(t), Style = (Style)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var b = new Button { Style = (Style)FindResource("TaskCard"), Content = stack, Tag = t, ToolTip = t.Prompt, IsEnabled = Prompt.IsEnabled };
        b.Click += QuickTask_Click;
        return b;
    }

    /// <summary>Quietly installs the skills, commands and agents the company approved (never overwrites anything the user changed).</summary>
    private void PullSharedKit()
    {
        try
        {
            if (_settings.HasShared && Directory.Exists(_settings.SharedRoot!))
            {
                Pec.Qc.Core.Knowledge.KitSync.ForThisPc(_settings).Pull();
                Pec.Qc.Core.Tools.ToolRegistry.From(_settings).Sync();      // custom tools the colleagues made arrive with the skills
                Pec.Qc.Core.Knowledge.OfflineQueue.Sync(_settings);          // lessons and activity kept here while the share was away go back to it
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* shared folder offline: the status light already says so */ }
    }

    private void AllTasks_Click(object sender, RoutedEventArgs e) => NavTasks.IsChecked = true;

    /// <summary>Responsive cards: as many columns as fit the window (at least one), each card stretched to share the row evenly.</summary>
    private static void FitCards(Panel panel, double available, double minCard)
    {
        const double gap = 16;
        var cols = Math.Max(1, (int)Math.Floor(available / (minCard + gap)));   // every card also carries a 16 px right margin
        var width = Math.Max(minCard * 0.8, available / cols - gap - 0.5);
        foreach (var c in panel.Children.OfType<FrameworkElement>()) c.Width = width;
    }

    private readonly HashSet<WrapPanel> _responsive = new();

    /// <summary>Sizes the cards of a panel from the panel's OWN width, and again whenever that width changes.</summary>
    private void Responsive(WrapPanel p, double minCard)
    {
        if (_responsive.Add(p)) p.SizeChanged += (_, e) => { if (e.WidthChanged && p.ActualWidth > 0) FitCards(p, p.ActualWidth, minCard); };
        if (p.ActualWidth > 0) FitCards(p, p.ActualWidth, minCard);
    }

    private void FitAllCards()
    {
        Responsive(WelcomeTasks, 250);
        Responsive(ProjectsPanel, 360);
        Responsive(KnowledgePanel, 360);
        foreach (var wp in TasksPanel.Children.OfType<WrapPanel>()) Responsive(wp, 250);
    }

    private void RefreshTasks()
    {
        var tasks = QuickTasks.Load();
        WelcomeTasks.Children.Clear();
        foreach (var t in tasks.Take(8)) WelcomeTasks.Children.Add(TaskCard(t));
        AllTasksLink.Visibility = tasks.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
        FitAllCards();
        TasksPanel.Children.Clear();
        foreach (var group in tasks.GroupBy(t => t.Category))
        {
            var (glyph, bg, fg) = Look(group.Key);
            TasksPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10), Children =
            {
                IconTile(glyph, bg, fg, 28), new TextBlock { Text = group.Key, Style = (Style)FindResource("H2"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            }});
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var t in group) wrap.Children.Add(TaskCard(t));
            TasksPanel.Children.Add(wrap);
        }
        FitAllCards();
        UpdateInput();
    }

    private async void QuickTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuickTask t }) return;
        var answers = new Dictionary<string, string>();
        var questions = QuickTasks.Questions(t.Prompt);
        if (questions.Count > 0)
        {
            var w = new InputWindow(t.Title, questions) { Owner = this };
            if (w.ShowDialog() != true) return;
            answers = w.Answers;
        }
        GoAssistant();
        await SendAsync(QuickTasks.Fill(t.Prompt, _profiles.Active, answers));
    }

    private void EditTasks_Click(object sender, RoutedEventArgs e)
    {
        var f = _settings.EffectiveQuickTasksFile;
        try
        {
            if (!File.Exists(f)) PromptBuilder.SeedDefaults(_settings, AppContext.BaseDirectory);
            Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open {f}: {ex.Message}", "Quick tasks"); }
    }

    // ------------------------------------------------------------------ knowledge dashboard

    private void RefreshKnowledge_Click(object sender, RoutedEventArgs e) { RefreshShared(); BuildKnowledge(); }

    private Border InfoCard(string glyph, string bg, string fg, string title, string? subtitle, UIElement body, params (string Text, Action Do)[] actions)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(IconTile(glyph, bg, fg, 38));
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H2") });
        if (subtitle is not null) titles.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Muted"), FontSize = 12 });
        head.Children.Add(titles);
        stack.Children.Add(head);
        body.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 14, 0, 10));
        stack.Children.Add(body);
        if (actions.Length > 0)
        {
            var row = new WrapPanel();
            foreach (var (text, act) in actions)
            {
                var b = new Button { Content = text, Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 4) };
                b.Click += (_, _) => act();
                row.Children.Add(b);
            }
            stack.Children.Add(row);
        }
        return new Border { Style = (Style)FindResource("Card"), Width = 410, Margin = new Thickness(0, 0, 16, 16), Child = stack };
    }

    private UIElement Lines(IEnumerable<string> items, string empty, int max = 8)
    {
        var list = items.ToList();
        var p = new StackPanel();
        if (list.Count == 0) p.Children.Add(new TextBlock { Text = empty, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        foreach (var s in list.Take(max)) p.Children.Add(new TextBlock { Text = "â€¢  " + s, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2) });
        if (list.Count > max) p.Children.Add(new TextBlock { Text = $"and {list.Count - max} more", Style = (Style)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        return p;
    }

    private void AskAssistant(string prompt) { GoAssistant(); _ = SendAsync(prompt); }

    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open {path}: {ex.Message}", "Folder"); }
    }

    private void BuildKnowledge()
    {
        KnowledgePanel.Children.Clear();
        _settings = Settings.Load();

        // shared folder
        if (_settings.HasShared)
        {
            var hub = SharedHub.Status(_settings.SharedRoot!);
            var body = new StackPanel();
            body.Children.Add(new TextBox { Text = _settings.SharedRoot, IsReadOnly = true, Style = (Style)FindResource("Field"), FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
            if (hub.Reachable)
            {
                var grid = new UniformGrid { Columns = 3 };
                foreach (var f in new[] { "skills", "learning", "projects", "documents", "requirements", "reports" })
                    grid.Children.Add(new StackPanel { Margin = new Thickness(0, 2, 0, 6), Children =
                    {
                        new TextBlock { Text = hub.Counts.GetValueOrDefault(f).ToString(), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent") },
                        new TextBlock { Text = f, Style = (Style)FindResource("Muted"), FontSize = 11 },
                    }});
                body.Children.Add(grid);
            }
            var state = !hub.Reachable ? "Offline: using the local copy" : hub.Writable ? "Connected" : "Read only";
            KnowledgePanel.Children.Add(InfoCard("î“", hub.Reachable ? "GoodSoft" : "WarnSoft", hub.Reachable ? "Good" : "Warn", "Shared folder", state, body,
                ("Open folder", () => OpenFolder(_settings.SharedRoot!)), ("Change", () => Settings_Click(this, new RoutedEventArgs()))));
        }
        else
            KnowledgePanel.Children.Add(InfoCard("î“", "WarnSoft", "Warn", "Shared folder", "Not set: knowledge stays on this PC",
                Lines([], "Set one folder (on the server) so everyone shares skills, lessons, client rules, custom tools and updates."),
                ("Set the shared folder", () => Settings_Click(this, new RoutedEventArgs()))));

        // skills
        // the active project's inputs: what is there and what each QC type is still missing
        {
            var proj = _profiles.Active;
            var folder = proj is null ? null : string.IsNullOrWhiteSpace(proj.InputsFolder) ? _profiles.DefaultInputsFolder(proj.Name) : proj.InputsFolder;
            if (proj is null || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                KnowledgePanel.Children.Add(InfoCard("î¢·", "WarnSoft", "Warn", "Project inputs", proj is null ? "No active project" : "No inputs folder yet",
                    Lines([], "The PMS, line list, P&IDs and vendor data of a project live in its inputs folder. Add or edit the project to set one."),
                    ("Projects", () => NavProjects.IsChecked = true)));
            else
            {
                var lib = new Pec.Qc.Core.Inputs.InputsLibrary(folder);
                var inv = lib.Inventory();
                var byKind = Enum.GetValues<Pec.Qc.Core.Inputs.InputKind>().Select(k => (k, n: inv.Count(d => d.Kind == k))).Where(x => x.n > 0)
                    .Select(x => $"{Pec.Qc.Core.Inputs.InputKinds.Label(x.k)}: {x.n}");
                var missing = Enum.GetValues<Pec.Qc.Core.Findings.QcType>().Select(t => (t, miss: lib.Coverage(t, inv).Where(c => !c.Present).Select(c => Pec.Qc.Core.Inputs.InputKinds.Label(c.Kind)).ToList()))
                    .Where(x => x.miss.Count > 0).Select(x => $"{Pec.Qc.Core.Findings.QcTypes.Label(x.t)} lacks: {string.Join(", ", x.miss)}");
                var body = new StackPanel();
                body.Children.Add(Lines(byKind, "The folder is empty."));
                if (missing.Any()) { var m = Lines(missing, ""); m.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 0)); body.Children.Add(m); }
                KnowledgePanel.Children.Add(InfoCard("î¢·", missing.Any() ? "WarnSoft" : "GoodSoft", missing.Any() ? "Warn" : "Good", "Project inputs", $"{proj.Name}: {inv.Count} files", body,
                    ("Open folder", () => OpenFolder(folder)), ("What can be checked?", () => AskAssistant("List the project inputs for the active project and say which QC checks each one supports and what is missing."))));
            }
        }

        var skills = PromptBuilder.SkillList(_settings.EffectiveSkillsDirectory);
        KnowledgePanel.Children.Add(InfoCard("î£±", "AccentSoft", "Accent", "Skills", $"{skills.Count} how-to guides", Lines(skills.Select(s => s.Name), "No skills yet."),
            ("Open folder", () => OpenFolder(_settings.EffectiveSkillsDirectory)), ("Ask for a new skill", () => AskAssistant("Create a new shared skill from what we have just done. Show me the guide first."))));

        // custom tools
        var tools = ToolRegistry.From(_settings).List();
        KnowledgePanel.Children.Add(InfoCard("î¥…", "PurpleSoft", "Purple", "Custom tools", $"{tools.Count} shared recipes", Lines(tools.Select(t => $"{t.Name}  â€”  {t.Description}"), "None yet. When you repeat the same steps, ask the assistant: \"Save this as a tool\"."),
            ("List them", () => AskAssistant("What custom tools do we have, and what does each do?")), ("Open folder", () => OpenFolder(_settings.EffectiveToolsDirectory))));

        // company lessons (self-learning)
        var lessons = new LessonStore(_settings);
        var approved = lessons.Approved();
        KnowledgePanel.Children.Add(InfoCard("î»", "TealSoft", "Teal", "Company lessons", $"{approved.Count} approved",
            Lines(approved.TakeLast(6).Select(l => $"[{l.Kind}] {l.Text}"), "None yet. When you correct the assistant it records a lesson; an administrator approves lessons into the company list."),
            ("Show them", () => AskAssistant("Read the company lessons (learn_pull) and summarise what applies to my work.")),
            ("Open folder", () => OpenFolder(Path.Combine(_settings.HasShared ? _settings.SharedRoot! : Settings.DataDirectory, "learning")))));

        // the curator's cards: only for the QC lead and the administrator
        if (Pec.Qc.Core.Users.UserRegistry.RoleOf(_settings, UserIdentity.Current(_settings)) >= Pec.Qc.Core.Users.QcRole.Lead)
        {
            var pendingLessons = new LessonCurator(_settings).Pending();
            KnowledgePanel.Children.Add(InfoCard("", "AccentSoft", "Accent", "Lessons to review", $"{pendingLessons.Count} waiting",
                Lines(pendingLessons.Take(5).Select(p => $"[{p.Lesson.Kind}{(p.Strong ? ", strong" : "")}] {p.Lesson.Text}"), "Nothing waiting: every lesson has been decided."),
                ("Review them", () => { LessonReviewWindow.Show(this, _settings); BuildKnowledge(); })));
            var recent = Pec.Qc.Core.Util.ActivityLog.Read(_settings, DateTime.UtcNow.AddDays(-7), null, null, null, 2000);
            KnowledgePanel.Children.Add(InfoCard("", "AccentSoft", "Accent", "Team activity", $"{recent.Count} lines in 7 days",
                Lines(Pec.Qc.Core.Util.ActivitySummary.ByDay(recent).Take(5).Select(d => $"{d.Day}  {d.User}: {d.Prompts} prompts, {d.Corrections} corrections, {d.ToolErrors} tool errors"), "No activity logged yet."),
                ("Open the report", () => ActivityWindow.Show(this, _settings))));
        }

        // documents and training
        var pack = TrainingPack.Load(Path.Combine(_settings.EffectiveDocumentsDirectory, "pack"));
        var rawDocs = Directory.Exists(_settings.EffectiveDocumentsDirectory) ? Directory.GetFiles(_settings.EffectiveDocumentsDirectory, "*.*", SearchOption.TopDirectoryOnly).Length : 0;
        KnowledgePanel.Children.Add(InfoCard("î¢¥", "WarnSoft", "Warn", "Documents & training", pack is null ? "Not converted yet" : $"{pack.Documents.Count} documents converted",
            Lines(pack?.Documents.Select(d => $"{d.Title}  ({d.Pages} pages, {d.Sections.Count} sections)") ?? [], rawDocs > 0 ? $"{rawDocs} file(s) in the documents folder. Convert them so the assistant can read them section by section." : "Put the client's procedures and training here (PDF, Markdown, text)."),
            ("Open folder", () => OpenFolder(_settings.EffectiveDocumentsDirectory)), ("Convert & index", () => AskAssistant("Convert the documents in my documents folder so they can be searched (convert_training), then tell me how many documents and sections it holds."))));

        // learning
        var entries = KnowledgeStore.From(_settings).All();
        var byCat = entries.GroupBy(e => e.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}: {g.Count()}");
        KnowledgePanel.Children.Add(InfoCard("î£½", "GoodSoft", "Good", "Learning & context", $"{entries.Count} saved notes", Lines(byCat, "Nothing saved yet. After a task, the assistant offers to save lessons, decisions and client rules (you approve each)."),
            ("Open learning", () => OpenFolder(_settings.EffectiveKnowledgeDirectory)), ("Save what we learned", () => AskAssistant("Record what we learned in this chat: the decisions, the problems found and how they were solved, and anything to avoid. Use remember with the right categories and sources, and show me each entry before saving."))));
    }

    // ------------------------------------------------------------------ guide

    private void BuildHelp()
    {
        if (_helpBuilt) return;
        var file = Path.Combine(AppContext.BaseDirectory, "help", "help.md");
        var text = File.Exists(file) ? File.ReadAllText(file) : "# Guide\n\nThe guide file (help\\help.md) was not found next to the app. Run Setup again to repair the installation.";
        var (doc, toc) = MarkdownView.Render(text);
        _helpToc = toc;
        HelpViewer.Document = doc;
        HelpToc.ItemsSource = toc.Select(t => t.Title).ToList();
        _helpBuilt = true;
    }

    private void HelpToc_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HelpToc.SelectedIndex >= 0 && HelpToc.SelectedIndex < _helpToc.Count) _helpToc[HelpToc.SelectedIndex].Target.BringIntoView();
    }

    // ------------------------------------------------------------------ shared office folder

    /// <summary>Makes sure the shared folder exists, shares this PC's custom tools with it and takes the new ones back.</summary>
    private void RefreshShared()
    {
        if (!_settings.HasShared) { SetStatus(SharedDot, SharedText, Light.Off, "Shared: this PC only", "No shared folder is set (Settings)."); return; }
        var hub = SharedHub.Ensure(_settings.SharedRoot!);
        if (!hub.Reachable) { SetStatus(SharedDot, SharedText, Light.Bad, "Shared: offline", $"Cannot reach {_settings.SharedRoot}. The assistant uses its local copy and syncs later."); return; }
        try { PromptBuilder.SeedDefaults(_settings, AppContext.BaseDirectory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var sync = ToolRegistry.From(_settings).Sync();
        SetStatus(SharedDot, SharedText, hub.Writable ? Light.Good : Light.Warn, hub.Writable ? "Shared: connected" : "Shared: read only",
            $"{_settings.SharedRoot}\nSkills {hub.Counts["skills"]}, learning {hub.Counts["learning"]}, context {hub.Counts["context"]}, documents {hub.Counts["documents"]}, tools {hub.Counts["tools"]}" +
            (sync.ToShared + sync.FromShared > 0 ? $"\nTools synced: {sync.ToShared} sent, {sync.FromShared} received" : ""));
    }

    // ------------------------------------------------------------------ updates

    private async Task CheckUpdateAsync()
    {
        _update = await Updates.CheckAsync(_settings.EffectiveUpdateSource, null, default, _settings.UpdatePublicKey);
        if (_update is not { Available: true, Latest: { } l }) return;
        UpdateText.Text = $"Version {l.Version} is available (you have {_update.Current}).";
        UpdateBar.Visibility = Visibility.Visible;
        // automatic: once per version, only while nothing is running, so a chat is never cut off
        if (_settings.AutoUpdate && _settings.AutoUpdateTried != l.Version && !_open.Values.Any(s => s.Busy))
        {
            _settings.AutoUpdateTried = l.Version;
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            UpdateText.Text = $"Installing version {l.Version}â€¦";
            await StartUpdateAsync(auto: true);
        }
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, string.IsNullOrWhiteSpace(_update?.Latest?.Notes) ? "No release notes were published." : _update!.Latest!.Notes, "What's new in " + _update?.Latest?.Version);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update?.SetupLocation is null) return;
        if (MessageBox.Show(this, "The assistant will close and Setup will update it. Your projects, settings and knowledge are kept. Continue?", "Update", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await StartUpdateAsync(auto: false);
    }

    private async Task StartUpdateAsync(bool auto)
    {
        if (_update?.SetupLocation is not { } loc) return;
        try
        {
            // Setup needs payload.zip next to it: copy both to a temp folder (the source may be a server share or a web address)
            var tmp = Path.Combine(Path.GetTempPath(), "pec-qc-update");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var baseLoc = loc[..(loc.LastIndexOfAny(new[] { '\\', '/' }) + 1)];
            foreach (var name in new[] { "PecQcSetup.exe", "payload.zip" })
            {
                if (baseLoc.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient();
                    await File.WriteAllBytesAsync(Path.Combine(tmp, name), await http.GetByteArrayAsync(baseLoc + name));
                }
                else File.Copy(baseLoc + name, Path.Combine(tmp, name), true);
            }
            // the files were copied from a share many people can write to: check them against the signed release file before anything is run
            var refused = Pec.Qc.Core.Setup.UpdateTrust.CheckFiles(_update!.Latest!, Path.Combine(tmp, "PecQcSetup.exe"), Path.Combine(tmp, "payload.zip"), _update.KeyConfigured);
            if (refused is not null) throw new InvalidOperationException(refused);
            Process.Start(new ProcessStartInfo(Path.Combine(tmp, "PecQcSetup.exe")) { UseShellExecute = true, Arguments = auto ? "/auto" : "" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (auto) UpdateText.Text = "Version " + _update!.Latest!.Version + " is available (the automatic update failed: " + ex.Message + ").";
            else MessageBox.Show(this, "Could not start the update: " + ex.Message, "Update");
        }
    }

    // ------------------------------------------------------------------ master prompt and settings

    private void MasterPrompt_Click(object sender, RoutedEventArgs e)
    {
        var f = _settings.EffectiveMasterPromptFile;
        try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open {f}: {ex.Message}", "Master prompt"); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_settings) { Owner = this };
        if (w.ShowDialog() != true) return;
        _settings = Settings.Load();
        WriteMcpConfigFile();                 // the approval mode and who the user is are passed to the tools: a change takes effect now, not at the next start
        RefreshShared();
        RefreshTasks();
        try { PromptBuilder.SeedDefaults(_settings, AppContext.BaseDirectory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (KnowledgeView.Visibility == Visibility.Visible) BuildKnowledge();
        NewConversation("Folders saved. A new chat was started so the assistant reads from them.");
    }

    // ------------------------------------------------------------------ sign in / out

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_claude is null) return;
        SignInButton.IsEnabled = false;
        Say(Role.System, "A browser window opens so you can sign in with your Claude account. Come back here when it is done.");
        try
        {
            var a = await _claude.LoginAsync();
            SetSignedIn(a.LoggedIn, true, a);
            Say(Role.System, a.LoggedIn ? "Signed in." : "Sign-in was not completed. Press \"Sign in with Claude\" to try again.");
        }
        catch (Exception ex) { Say(Role.System, "Sign-in failed: " + ex.Message); }
        finally { SignInButton.IsEnabled = true; }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_claude is null) return;
        await _claude.LogoutAsync();
        _cur.Data.ClaudeSessionId = null; foreach (var o in _open.Values) o.Data.ClaudeSessionId = null;   // a new sign-in is a new Claude session
        await RefreshAuthAsync();
        Say(Role.System, "Signed out.");
    }

    // ------------------------------------------------------------------ approvals

    // ------------------------------------------------------------------ rendering

    private enum Role { User, Assistant, System }

    private Border IconTile(string glyph, string bgKey, string fgKey, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 3.2), Background = Res(bgKey), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = size * 0.46, Foreground = Res(fgKey), HorizontalAlignment = HorizontalAlignment.Center },
    };

    private Border Pill(string text, string bgKey, string fgKey) => new()
    {
        Style = (Style)FindResource("Pill"), Background = Res(bgKey), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res(fgKey) },
    };

    private UIElement InfoRow(string glyph, string label, string value)
    {
        var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        d.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 13, Foreground = Res("InkFaint"), Width = 24 });
        d.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Muted"), Width = 56, FontSize = 12.5 });
        d.Children.Add(new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value, FontSize = 12.5 });
        return d;
    }

    private void ShowChat(ChatSession s)
    {
        if (s.Started) return;
        s.Started = true;
        if (s != _cur) return;
        WelcomeScroll.Visibility = Visibility.Collapsed;
        ChatScroll.Content = s.Panel;
        ChatScroll.Visibility = Visibility.Visible;
        // notices that were waiting on the welcome screen move into the conversation
        foreach (var n in Notices.Children.OfType<UIElement>().ToList()) { Notices.Children.Remove(n); s.Panel.Children.Add(n); }
    }

    private FrameworkElement Say(Role role, string text) => Say(_cur, role, text);

    private FrameworkElement Say(ChatSession s, Role role, string text, bool persist = true)
    {
        if (role == Role.System && !s.Started)
        {
            var notice = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Background = Res("AccentSoft"), BorderBrush = Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 900 };
            notice.Child = new DockPanel { Children =
            {
                new TextBlock { Text = "î¥†", Style = (Style)FindResource("Icon"), Foreground = Res("Accent"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            }};
            Notices.Children.Add(notice);
            return notice;
        }
        ShowChat(s);
        FrameworkElement el;
        if (role == Role.System)
        {
            el = new Border { Style = (Style)FindResource("Pill"), Background = Res("AccentSoft"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8), Child = new TextBlock { Text = text, Foreground = Res("InkMuted"), FontSize = 12, TextWrapping = TextWrapping.Wrap } };
        }
        else if (role == Role.User)
        {
            el = new Border
            {
                Background = Res("Accent"), CornerRadius = new CornerRadius(16, 16, 4, 16), Padding = new Thickness(16, 11, 16, 11), Margin = new Thickness(120, 8, 0, 8), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White, FontSize = 14, Cursor = Cursors.IBeam },
            };
        }
        else
        {
            var avatar = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Brushes.White, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 12, 0),
                Child = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Margin = new Thickness(5), Stretch = Stretch.Uniform } };
            var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 13, 18, 13), MaxWidth = 800,
                Child = new RichTextBox { Document = MarkdownView.RenderChat(text), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsDocumentEnabled = false, Foreground = Res("Ink") } };
            var row = new DockPanel { Margin = new Thickness(0, 8, 90, 8), HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Add(avatar); row.Children.Add(card);
            DockPanel.SetDock(avatar, Dock.Left);
            el = row;
        }
        s.Panel.Children.Add(el);
        if (s == _cur) ChatScroll.ScrollToEnd();
        if (persist) Persist(s, role, text);
        return el;
    }

    private FrameworkElement Thinking(ChatSession s)
    {
        var dots = new TextBlock { Text = "Working on itâ€¦", Foreground = Res("Accent"), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        dots.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        var el = new Border { Margin = new Thickness(44, 4, 0, 4), Child = dots, HorizontalAlignment = HorizontalAlignment.Left };
        ShowChat(s);
        s.Panel.Children.Add(el);
        if (s == _cur) ChatScroll.ScrollToEnd();
        return el;
    }

    private void BeginStep(ChatSession s, string tool, string input)
    {
        s.StepInput = input;
        s.StepBox = new TextBox { Text = "Input\n" + input, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11.5, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFB)), BorderThickness = new Thickness(0), Padding = new Thickness(10) };
        s.Step = new Expander { Style = (Style)FindResource("StepExpander"), Margin = new Thickness(44, 3, 90, 3), Content = s.StepBox, Header = StepHeader("î¨", "InkMuted", Nice(tool), "runningâ€¦") };
        s.Step.Tag = tool;
        s.Panel.Children.Add(s.Step);
    }

    private void EndStep(ChatSession s, bool error, string result)
    {
        if (s.Step is null || s.StepBox is null) return;
        var tool = (string)s.Step.Tag;
        s.Step.Header = StepHeader(error ? "îœ‘" : "îœ¾", error ? "Bad" : "Good", Nice(tool), error ? "failed" : "done");
        s.StepBox.Text = "Input\n" + s.StepInput + "\n\nResult\n" + result;
        if (error) s.Step.IsExpanded = true;
        s.Step = null;
    }

    private UIElement StepHeader(string glyph, string colorKey, string name, string state) => new StackPanel { Orientation = Orientation.Horizontal, Children =
    {
        new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 12, Foreground = Res(colorKey), Margin = new Thickness(0, 0, 8, 0) },
        new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 12.5 },
        new TextBlock { Text = "   " + state, Foreground = Res("InkFaint"), FontSize = 12 },
    }};

    private static string Nice(string tool) { var s = tool.Replace('_', ' '); return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s; }

    private static string Pretty(string json)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(json).RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        catch (System.Text.Json.JsonException) { return json; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + $"\n... ({s.Length - max} more characters)";
}

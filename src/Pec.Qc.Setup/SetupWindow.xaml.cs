using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Pec.Qc.Core;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Setup;

namespace Pec.Qc.Setup;

public partial class SetupWindow : Window
{
    private sealed record Row(string Mark, Brush Color, string Name, string Detail);

    private readonly InstallPaths _paths = InstallPaths.Default();
    private readonly string _payloadZip = Path.Combine(AppContext.BaseDirectory, "payload.zip");
    private readonly bool _uninstall = HasArg("/uninstall");
    /// <summary>Started by the app for an automatic update: install without questions (keeping the choices made at the first install), then start the new version.</summary>
    private readonly bool _auto = HasArg("/auto");
    private bool _done;
    private readonly List<(Pec.Qc.Core.Setup.McpAddon Addon, System.Windows.Controls.CheckBox Box)> _addonBoxes = [];
    private InstallFolders _defaults = FolderDefaults.For(null);

    private static bool HasArg(string a) => Environment.GetCommandLineArgs().Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));

    public SetupWindow()
    {
        InitializeComponent();
        TargetBox.Text = _paths.AppDir;
        var f = FolderDefaults.For(null);
        _defaults = f;
        SkillsBox.Text = f.Skills; LearningBox.Text = f.Learning; DocsBox.Text = f.Documents;
        SharedBox.TextChanged += (_, _) => SyncFolders();      // the three folders follow the shared folder unless the user edited them
        SharedBox.Text = Settings.Load().SharedRoot ?? "";      // already set by an earlier install, or by the office defaults shipped next to Setup
        if (_uninstall)
        {
            Heading.Text = "Remove PEC QC Assistant";
            Intro.Text = "This removes the app, the Claude Code and Claude Desktop registrations and the skills it installed. Your settings, chats and the shared folder are kept.";
            OptionsPanel.Visibility = Visibility.Collapsed;
            ActionButton.Content = "Remove";
            Loaded += (_, _) => RelaunchFromTempIfInsideTheApp();
        }
        else
        {
            if (_auto) ApplyChoicesOfTheFirstInstall(); else LoadAddons();
            Loaded += async (_, _) =>
            {
                ShowChecks();
                if (_auto && ActionButton.IsEnabled)
                {
                    Log("Automatic update: installing the new version...");
                    await Task.Delay(2500);                                   // the old assistant is closing: let it release its files
                    Action_Click(ActionButton, new RoutedEventArgs());
                }
            };
        }
    }

    /// <summary>Windows starts the uninstaller from inside the folder it is about to delete: run a copy from the temp folder instead, so nothing is left behind.</summary>
    private void RelaunchFromTempIfInsideTheApp()
    {
        var me = Environment.ProcessPath;
        if (me is null || HasArg("/fromtemp") || !me.StartsWith(_paths.AppDir, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "pec-qc-uninstall");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(me));
            File.Copy(me, copy, true);
            Process.Start(new ProcessStartInfo(copy, "/uninstall /fromtemp") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { /* run from here: the Setup folder may be left over */ }
    }

    /// <summary>An update does what the first install did: nothing new is registered or copied behind the user's back.</summary>
    private void ApplyChoicesOfTheFirstInstall()
    {
        var m = new Installer(_paths).ReadManifest();
        if (m is null) return;
        var o = InstallOptions.FromManifest(m);
        OptCode.IsChecked = o.RegisterClaudeCode; OptDesktop.IsChecked = o.RegisterClaudeDesktop; OptShortcut.IsChecked = o.StartMenuShortcut; OptSkills.IsChecked = o.CopySkills;
        OptInstallClaude.IsChecked = false;
    }

    private void SyncFolders()
    {
        var nd = FolderDefaults.For(SharedBox.Text);
        SkillsBox.Text = FolderDefaults.Follow(SkillsBox.Text, _defaults.Skills, nd.Skills);
        LearningBox.Text = FolderDefaults.Follow(LearningBox.Text, _defaults.Learning, nd.Learning);
        DocsBox.Text = FolderDefaults.Follow(DocsBox.Text, _defaults.Documents, nd.Documents);
        _defaults = nd;
    }

    /// <summary>The MCP servers of other products (AutoCAD, Civil 3D, Plant 3D, Navisworks, Revit): listed from the package, ticked when the program is on this PC.</summary>
    private void LoadAddons()
    {
        var addons = Pec.Qc.Core.Setup.McpAddons.LoadFromZip(_payloadZip);
        if (addons.Count == 0) return;
        foreach (var st in Pec.Qc.Core.Setup.McpAddons.Statuses(addons))
        {
            var box = new System.Windows.Controls.CheckBox
            {
                IsChecked = st.Detected, Margin = new Thickness(0, 6, 0, 0),
                Content = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Inlines =
                    {
                        new System.Windows.Documents.Run(st.Addon.Title) { FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run(st.Detected ? "  (" + st.DetectedAs + " found)" : "  (program not found on this PC)") { Foreground = st.Detected ? Brushes.SeaGreen : Brushes.Gray },
                        new System.Windows.Documents.LineBreak(),
                        new System.Windows.Documents.Run(st.Addon.Description + " Source: " + st.Addon.Source + ".") { Foreground = Brushes.Gray, FontSize = 12 },
                    },
                },
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "Addon_" + st.Addon.Id);
            AddonsPanel.Children.Add(box);
            _addonBoxes.Add((st.Addon, box));
        }
        AddonsCard.Visibility = Visibility.Visible;
    }

    private void ShowChecks()
    {
        var rows = new Installer(_paths).Check(null, SharedBox.Text.Trim())
            .Select(c => new Row(c.Ok ? "✓" : c.Required ? "✗" : "!", c.Ok ? Brushes.SeaGreen : c.Required ? Brushes.Firebrick : Brushes.DarkOrange, c.Name, c.Detail)).ToList();
        var pkg = File.Exists(_payloadZip);
        rows.Add(new Row(pkg ? "✓" : "✗", pkg ? Brushes.SeaGreen : Brushes.Firebrick, "Installer package", pkg ? "payload.zip found." : "payload.zip is missing next to the installer."));
        Checks.ItemsSource = rows;
        // Claude Code may be installed by Setup itself when that option is ticked: it only blocks when it is missing and Setup was told not to install it
        var claudeMissing = rows.Any(r => r.Name == "Claude Code" && r.Mark == "✗");
        var blockers = rows.Where(r => r.Mark == "✗" && !(claudeMissing && r.Name == "Claude Code" && OptInstallClaude.IsChecked == true)).ToList();
        ActionButton.IsEnabled = pkg && blockers.Count == 0;
        if (!ActionButton.IsEnabled) Log("Fix the items marked ✗, then start Setup again.");
    }

    private void Pick(System.Windows.Controls.TextBox box, string title)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (d.ShowDialog(this) == true) box.Text = d.FolderName;
    }

    private void BrowseShared_Click(object sender, RoutedEventArgs e) { Pick(SharedBox, "Shared office folder"); ShowChecks(); }
    private void BrowseSkills_Click(object sender, RoutedEventArgs e) => Pick(SkillsBox, "Skills folder");
    private void BrowseLearning_Click(object sender, RoutedEventArgs e) => Pick(LearningBox, "Learning folder");
    private void BrowseDocs_Click(object sender, RoutedEventArgs e) => Pick(DocsBox, "Documents folder");

    private void Log(string s) { LogBox.AppendText(s + Environment.NewLine); LogBox.ScrollToEnd(); }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            if (!_uninstall) Process.Start(new ProcessStartInfo(Path.Combine(_paths.AppDir, Installer.AppExe)) { UseShellExecute = true });
            Close(); return;
        }
        ActionButton.IsEnabled = false;
        var inst = new Installer(_paths, s => Dispatcher.Invoke(() => Log(s)));
        try
        {
            if (_uninstall)
            {
                var addonsDone = Pec.Qc.Core.Setup.McpAddons.InstalledIds(Path.Combine(_paths.AppDir, Pec.Qc.Core.Setup.McpAddons.FolderName));
                await Task.Run(() => inst.Uninstall());
                Log("PEC QC Assistant was removed.");
                if (addonsDone.Count > 0) Log("The MCP servers installed with it (" + string.Join(", ", addonsDone) + ") are separate products and were NOT removed: remove them from Claude's settings and from the Autodesk program's add-ins if you no longer want them.");
                ActionButton.Content = "Finish";
            }
            else
            {
                var folders = new InstallFolders(SkillsBox.Text.Trim(), LearningBox.Text.Trim(), DocsBox.Text.Trim(), SharedBox.Text.Trim());
                foreach (var d in new[] { folders.Shared ?? "", folders.Skills, folders.Learning, folders.Documents }.Where(d => d.Length > 0))
                    Directory.CreateDirectory(d);          // fails early (and clearly) when a shared drive is not reachable
                var installClaude = OptInstallClaude.IsChecked == true;      // read controls here, on the UI thread, not inside Task.Run
                var opt = new InstallOptions(OptCode.IsChecked == true, OptDesktop.IsChecked == true, OptShortcut.IsChecked == true, OptSkills.IsChecked == true);
                var chosenAddons = _addonBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Addon).ToList();
                if (chosenAddons.Any(a => a.ClosesApps) && MessageBox.Show(this, "The installers you ticked for " + string.Join(", ", chosenAddons.Where(a => a.ClosesApps).Select(a => a.Title)) + " may close AutoCAD, Revit or Claude on this PC. Save your work now.\n\nContinue with these MCP servers? (No installs the QC tools only.)", "MCP servers", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) chosenAddons.Clear();
                await Task.Run(() =>
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "pec-qc-setup");
                    Installer.ExtractPayload(_payloadZip, tmp);
                    if (installClaude && Pec.Qc.Core.Claude.ClaudeCli.Find() is null) inst.InstallClaudeCode(out _);
                    inst.Install(tmp, opt, null, folders);
                    try   // skills, commands and agents the company has approved (made by colleagues with Claude)
                    {
                        var st = Settings.Load();
                        if (st.HasShared)
                        {
                            var pulled = Pec.Qc.Core.Knowledge.KitSync.ForThisPc(st).Pull();
                            Dispatcher.Invoke(() => Log($"Shared skills, commands and agents: {pulled.Added.Count} added, {pulled.Updated.Count} updated."));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dispatcher.Invoke(() => Log("Shared skills could not be pulled now: " + ex.Message)); }
                    // keep this installer in the app folder: Windows "Uninstall" starts it with /uninstall
                    var setupDir = Path.Combine(_paths.AppDir, "Setup");
                    Directory.CreateDirectory(setupDir);
                    var keep = Path.Combine(setupDir, Installer.SetupExe);
                    if (!string.Equals(Environment.ProcessPath, keep, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, keep, true);
                    Directory.Delete(tmp, true);
                    foreach (var a in chosenAddons)       // one at a time, each in its own window; the scripts are checked against the release's manifest first
                    {
                        var r = Pec.Qc.Core.Setup.McpAddons.Run(Path.Combine(_paths.AppDir, Pec.Qc.Core.Setup.McpAddons.FolderName), a, s => Dispatcher.Invoke(() => Log(s)));
                        Dispatcher.Invoke(() => Log(r.Message));
                    }
                });
                Log("Installed. Start \"PEC QC Assistant\" and sign in with your Claude account.");
                ActionButton.Content = "Start the assistant";
            }
            _done = true;
            ActionButton.IsEnabled = true;
            if (_auto && !_uninstall) { await Task.Delay(1500); Action_Click(ActionButton, new RoutedEventArgs()); }   // starts the new version and closes Setup
        }
        catch (Exception ex) { Log("Failed: " + ex.Message); ActionButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

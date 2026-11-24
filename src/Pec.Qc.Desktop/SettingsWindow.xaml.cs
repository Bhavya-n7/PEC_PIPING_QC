using System.IO;
using System.Windows;
using Pec.Qc.Core;
using Pec.Qc.Core.Install;
using Pec.Qc.Core.Users;

namespace Pec.Qc.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Settings _s;

    public SettingsWindow(Settings s)
    {
        InitializeComponent();
        _s = s;
        SharedBox.Text = s.SharedRoot ?? "";
        SkillsBox.Text = s.EffectiveSkillsDirectory;
        LearningBox.Text = s.EffectiveKnowledgeDirectory;
        DocsBox.Text = s.EffectiveDocumentsDirectory;
        MasterBox.Text = s.EffectiveMasterPromptFile;
        UpdateBox.Text = s.EffectiveUpdateSource ?? "";
        UserNameBox.Text = s.UserName ?? ""; EmployeeBox.Text = s.EmployeeCode ?? ""; CompanyBox.Text = s.Company ?? "";
        AutonomyBox.ItemsSource = new[] { "Full: works until the task is done; files, web and commands on this PC (recommended)", "Standard: files in its own workspace and the web, no commands", "Careful: the QC tools only" };
        AutonomyBox.SelectedIndex = s.EffectiveAutonomy switch { Pec.Qc.Core.Claude.ClaudeAutonomy.Careful => 2, Pec.Qc.Core.Claude.ClaudeAutonomy.Standard => 1, _ => 0 };
        ApprovalBox.ItemsSource = new[] { "Ask once per task (recommended)", "Ask for every change", "Never ask" };
        ApprovalBox.SelectedIndex = s.EffectiveApprovalMode switch { Pec.Qc.Core.Approvals.ApprovalMode.Strict => 1, Pec.Qc.Core.Approvals.ApprovalMode.Off => 2, _ => 0 };
        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        NavisBox.Text = s.NavisworksHome ?? "";
        _prev = new Settings { SharedRoot = SharedBox.Text.Trim() };
        SharedBox.TextChanged += (_, _) => FollowShared();
        RefreshStates();
    }

    private Settings _prev = new();

    /// <summary>The four advanced boxes follow the shared folder while they still show what it gave before (so a typed or browsed shared folder moves them too).</summary>
    private void FollowShared()
    {
        var now = new Settings { SharedRoot = SharedBox.Text.Trim() };
        SkillsBox.Text = Pec.Qc.Core.Setup.FolderDefaults.Follow(SkillsBox.Text, _prev.EffectiveSkillsDirectory, now.EffectiveSkillsDirectory);
        LearningBox.Text = Pec.Qc.Core.Setup.FolderDefaults.Follow(LearningBox.Text, _prev.EffectiveKnowledgeDirectory, now.EffectiveKnowledgeDirectory);
        DocsBox.Text = Pec.Qc.Core.Setup.FolderDefaults.Follow(DocsBox.Text, _prev.EffectiveDocumentsDirectory, now.EffectiveDocumentsDirectory);
        MasterBox.Text = Pec.Qc.Core.Setup.FolderDefaults.Follow(MasterBox.Text, _prev.EffectiveMasterPromptFile, now.EffectiveMasterPromptFile);
        _prev = now;
    }

    private void RefreshStates()
    {
        var nw = NavisworksInstall.Find(string.IsNullOrWhiteSpace(NavisBox.Text) ? null : NavisBox.Text.Trim());
        NavisStateText.Text = nw is null ? "Navisworks Manage not found: isometric and GA checks need it." : $"Found {nw.Name}.";
        var who = UserIdentity.FromSettings(_s);
        RoleText.Text = who.IsComplete ? "Your role: " + QcRoles.Name(UserRegistry.RoleOf(_s, who)) + "  (an administrator changes roles)" : "";
    }

    private void BrowseNavis_Click(object sender, RoutedEventArgs e) { Pick(NavisBox, "Navisworks Manage install folder"); RefreshStates(); }

    private void Pick(System.Windows.Controls.TextBox box, string title)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (d.ShowDialog(this) == true) box.Text = d.FolderName;
    }

    private void BrowseShared_Click(object sender, RoutedEventArgs e) => Pick(SharedBox, "Shared office folder");

    private void BrowseSkills_Click(object sender, RoutedEventArgs e) => Pick(SkillsBox, "Skills folder");
    private void BrowseLearning_Click(object sender, RoutedEventArgs e) => Pick(LearningBox, "Learning folder");
    private void BrowseDocs_Click(object sender, RoutedEventArgs e) => Pick(DocsBox, "Documents folder");

    private void BrowseUpdate_Click(object sender, RoutedEventArgs e) => Pick(UpdateBox, "Update source folder");

    private void BrowseMaster_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Master prompt file", Filter = "Markdown or text (*.md;*.txt)|*.md;*.txt|All files|*.*", CheckFileExists = false };
        if (d.ShowDialog(this) == true) MasterBox.Text = d.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.SharedRoot = SharedBox.Text.Trim();
        // a folder left at what the shared folder would give stays "derived", so moving the shared folder later moves it too
        var derived = new Settings { SharedRoot = _s.SharedRoot };
        string? Own(string text, string def) => string.IsNullOrWhiteSpace(text) || text.Trim().Equals(def, StringComparison.OrdinalIgnoreCase) ? null : text.Trim();
        _s.SkillsDirectory = Own(SkillsBox.Text, derived.EffectiveSkillsDirectory);
        _s.KnowledgeDirectory = Own(LearningBox.Text, derived.EffectiveKnowledgeDirectory);
        _s.DocumentsDirectory = Own(DocsBox.Text, derived.EffectiveDocumentsDirectory);
        _s.MasterPromptFile = Own(MasterBox.Text, derived.EffectiveMasterPromptFile);
        _s.UpdateSource = Own(UpdateBox.Text, derived.EffectiveUpdateSource ?? "");
        _s.UserName = UserNameBox.Text.Trim(); _s.EmployeeCode = EmployeeBox.Text.Trim(); _s.Company = CompanyBox.Text.Trim();
        _s.Autonomy = AutonomyBox.SelectedIndex switch { 1 => "standard", 2 => "careful", _ => "full" };
        _s.ApprovalMode = ApprovalBox.SelectedIndex switch { 1 => "strict", 2 => "off", _ => "task" };
        _s.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _s.NavisworksHome = string.IsNullOrWhiteSpace(NavisBox.Text) ? null : NavisBox.Text.Trim();
        if (_s.HasShared)
        {
            var hub = Pec.Qc.Core.Knowledge.SharedHub.Ensure(_s.SharedRoot!);
            if (!hub.Reachable) { MessageBox.Show(this, $"The shared folder cannot be reached or created: {_s.SharedRoot}", "Settings"); return; }
            if (!hub.Writable) MessageBox.Show(this, "The shared folder is read only for you: you can use what is in it, but new tools, skills and learning cannot be saved there.", "Settings");
        }
        foreach (var d in new[] { _s.EffectiveSkillsDirectory, _s.EffectiveKnowledgeDirectory, _s.EffectiveDocumentsDirectory }.Where(d => d.Length > 0))
            try { if (d.Length > 0) Directory.CreateDirectory(d); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, $"Cannot use the folder '{d}': {ex.Message}", "Settings"); return; }
        _s.Save();
        DialogResult = true;
    }
}

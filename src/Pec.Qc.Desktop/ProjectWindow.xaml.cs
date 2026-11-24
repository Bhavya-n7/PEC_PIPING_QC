using System.IO;
using System.Windows;
using Pec.Qc.Core.Claude;

namespace Pec.Qc.Desktop;

/// <summary>Create or edit a QC project card. The name is the key of the card; the inputs folder defaults to the project's folder on the shared drive.</summary>
public partial class ProjectWindow : Window
{
    public ProjectCard? Result { get; private set; }
    private readonly Func<string, string?> _defaultInputs;

    /// <param name="defaultInputs">The default inputs folder for a project name (null when there is no shared folder).</param>
    public ProjectWindow(ProjectCard? existing, Func<string, string?> defaultInputs)
    {
        InitializeComponent();
        _defaultInputs = defaultInputs;
        NameBox.TextChanged += (_, _) => ShowDefaultNote();
        InputsBox.TextChanged += (_, _) => ShowDefaultNote();
        if (existing is not null)
        {
            NameBox.Text = existing.Name; ClientBox.Text = existing.Client; ProjectNoBox.Text = existing.ProjectNo;
            ModelBox.Text = existing.ModelFile; InputsBox.Text = existing.InputsFolder; PacksBox.Text = existing.RulePacks; DetailsBox.Text = existing.Details;
            NameBox.IsReadOnly = true;     // the name is the key of the card
        }
        ShowDefaultNote();
    }

    private void ShowDefaultNote()
    {
        var def = NameBox.Text.Trim().Length > 0 ? _defaultInputs(NameBox.Text.Trim()) : null;
        InputsNote.Text = InputsBox.Text.Trim().Length > 0 ? ""
            : def is not null ? "Empty: the shared folder " + def + " will be created and used. Put the PMS, line list, P&IDs and vendor data there."
            : "Empty: no inputs folder yet (there is no shared folder). Browse to a folder on this PC.";
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Navisworks model", Filter = "Navisworks files (*.nwd;*.nwf)|*.nwd;*.nwf|All files|*.*", CheckFileExists = true };
        if (d.ShowDialog(this) == true) ModelBox.Text = d.FileName;
    }

    private void BrowseInputs_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Project inputs folder", InitialDirectory = Directory.Exists(InputsBox.Text) ? InputsBox.Text : null };
        if (d.ShowDialog(this) == true) InputsBox.Text = d.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "Give the project a name.", "QC project"); return; }
        var model = ModelBox.Text.Trim();
        if (model.Length > 0 && !model.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase) && !model.EndsWith(".nwf", StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this, "The model file is not a Navisworks file (.nwd or .nwf). Save anyway?", "QC project", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var inputs = InputsBox.Text.Trim();
        if (inputs.Length == 0 && _defaultInputs(name) is { } def)
        {
            try { Directory.CreateDirectory(def); inputs = def; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, "The shared inputs folder could not be created: " + ex.Message + "\n\nThe project is saved without an inputs folder; set one later.", "QC project"); }
        }
        Result = new ProjectCard
        {
            Name = name, Client = ClientBox.Text.Trim(), ProjectNo = ProjectNoBox.Text.Trim(), ModelFile = model, InputsFolder = inputs,
            RulePacks = PacksBox.Text.Trim(), Details = DetailsBox.Text.Trim(),
        };
        DialogResult = true;
    }
}

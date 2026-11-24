using System.Windows;
using System.Windows.Controls;

namespace Pec.Qc.Desktop;

/// <summary>Asks for the few values a quick task needs.</summary>
public partial class InputWindow : Window
{
    private readonly Dictionary<string, TextBox> _boxes = [];
    public Dictionary<string, string> Answers { get; } = [];

    public InputWindow(string title, IEnumerable<string> questions)
    {
        InitializeComponent();
        TitleText.Text = title;
        foreach (var q in questions)
        {
            Fields.Children.Add(new TextBlock { Text = q, Margin = new Thickness(0, 6, 0, 2) });
            var box = new TextBox { Padding = new Thickness(5) };
            _boxes[q] = box;
            Fields.Children.Add(box);
        }
        Loaded += (_, _) => _boxes.Values.FirstOrDefault()?.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var empty = _boxes.Where(b => string.IsNullOrWhiteSpace(b.Value.Text)).Select(b => b.Key).ToList();
        if (empty.Count > 0) { MessageBox.Show(this, "Please fill in: " + string.Join(", ", empty), "A few details"); return; }
        foreach (var (q, box) in _boxes) Answers[q] = box.Text.Trim();
        DialogResult = true;
    }
}

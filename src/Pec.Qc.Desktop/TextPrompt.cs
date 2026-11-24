using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pec.Qc.Desktop;

/// <summary>A small "type a name" dialog (used to rename a chat).</summary>
internal static class TextPrompt
{
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        string? result = null;
        var box = new TextBox { Text = initial, Padding = new Thickness(10, 8, 10, 8), FontSize = 14, Margin = new Thickness(0, 6, 0, 16) };
        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(22, 8, 22, 8), Margin = new Thickness(10, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(22, 8, 22, 8) };
        var win = new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = 440,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = (System.Windows.Media.Brush)Application.Current.FindResource("AppBg"),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13,
            Content = new StackPanel
            {
                Margin = new Thickness(22, 18, 22, 18),
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        ok.Click += (_, _) => { result = box.Text.Trim(); win.DialogResult = true; };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pec.Qc.Core;

namespace Pec.Qc.Desktop;

/// <summary>Asked once at first start: who is using the assistant (name, employee code, company). Everything the assistant does is logged against it.</summary>
internal static class IdentityWindow
{
    public static UserIdentity? Ask(Window owner, UserIdentity current)
    {
        UserIdentity? result = null;
        TextBox Field(string text) => new() { Text = text, Padding = new Thickness(10, 8, 10, 8), FontSize = 14, Margin = new Thickness(0, 4, 0, 12) };
        var name = Field(current.Name); var code = Field(current.EmployeeCode);
        var company = Field(current.Company.Length > 0 ? current.Company : "Petrocon Engineers & Consultants");
        var ok = new Button { Content = "Continue", IsDefault = true, IsEnabled = false, Padding = new Thickness(26, 9, 26, 9), HorizontalAlignment = HorizontalAlignment.Right };
        void Check() => ok.IsEnabled = name.Text.Trim().Length > 0 && code.Text.Trim().Length > 0;
        name.TextChanged += (_, _) => Check(); code.TextChanged += (_, _) => Check(); Check();
        TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold };

        var win = new Window
        {
            Title = "Who is using the assistant?", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = 500,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = (Brush)Application.Current.FindResource("AppBg"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13,
            Content = new StackPanel
            {
                Margin = new Thickness(26, 22, 26, 22),
                Children =
                {
                    new TextBlock { Text = "Welcome", FontSize = 22, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = "Please tell the assistant who you are. Your prompts and what the assistant did are written to a shared log, so the team can support you, follow up on accuracy, and the assistant can learn from every task and share it with everyone.",
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), Foreground = (Brush)Application.Current.FindResource("InkMuted"),
                    },
                    Label("Your name"), name, Label("Employee code"), code, Label("Company"), company, ok,
                },
            },
        };
        ok.Click += (_, _) => { result = new UserIdentity(name.Text.Trim(), code.Text.Trim(), company.Text.Trim()); win.DialogResult = true; };
        win.Loaded += (_, _) => (name.Text.Length == 0 ? name : code).Focus();
        return win.ShowDialog() == true ? result : null;
    }
}

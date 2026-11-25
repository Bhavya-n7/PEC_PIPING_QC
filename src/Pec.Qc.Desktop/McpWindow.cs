using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pec.Qc.Core.McpInstall;

namespace Pec.Qc.Desktop;

/// <summary>
/// Installs the MCP servers for the Autodesk programs (AutoCAD, Plant 3D, Civil 3D, Navisworks, Revit) with the program's own installer: nothing opens a console, nothing asks a question.
/// Each line shows whether the program is on this PC and whether its server is installed; "Install" does everything (download, check, plug-in, server, test, Claude).
/// </summary>
internal sealed class McpWindow : Window
{
    private readonly McpEnv _env = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _stepText = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), Text = "Ready." };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Height = 170, Visibility = Visibility.Collapsed };
    private readonly Button _details = new() { Content = "Show details", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _all = new() { Content = "Install all that are possible", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _cancel = new() { Content = "Stop", Padding = new Thickness(14, 9, 14, 9), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0) };
    private CancellationTokenSource? _cts;
    private bool _busy;

    public McpWindow(Window owner)
    {
        Owner = owner; Title = "MCP servers for Autodesk programs"; Width = 760; Height = 700; MinWidth = 560; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("AppBg"); FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); FontSize = 13;
        _all.Style = (Style)Application.Current.FindResource("PrimaryButton"); _cancel.Style = (Style)Application.Current.FindResource("SecondaryButton");
        _details.Style = (Style)Application.Current.FindResource("GhostButton");
        _details.Click += (_, _) => { _log.Visibility = _log.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; _details.Content = _log.Visibility == Visibility.Visible ? "Hide details" : "Show details"; };
        _all.Click += async (_, _) => await InstallManyAsync(McpCatalog.Status(_env).Where(s => s.HostFound).Select(s => s.Product).ToList());
        _cancel.Click += (_, _) => _cts?.Cancel();

        var head = new StackPanel { Margin = new Thickness(26, 22, 26, 10) };
        head.Children.Add(new TextBlock { Text = "MCP servers", FontSize = 22, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = "An MCP server lets Claude work with an Autodesk program that is open on this PC. Press Install: the assistant downloads the server from its publisher, checks it, installs the plug-in, tests the server and connects it to Claude. " +
                   "Close the Autodesk program first. You can come back here at any time to install another one or to repair one.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)Application.Current.FindResource("InkMuted"),
        });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Margin = new Thickness(26, 6, 26, 6), Children = { _rows } } };
        var foot = new StackPanel { Margin = new Thickness(26, 10, 26, 18) };
        foot.Children.Add(_stepText); foot.Children.Add(_bar); foot.Children.Add(_details); foot.Children.Add(_log);
        foot.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { _all, _cancel } });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(head, 0); Grid.SetRow(scroll, 1); Grid.SetRow(foot, 2);
        grid.Children.Add(head); grid.Children.Add(scroll); grid.Children.Add(foot);
        Content = grid;
        Closing += (_, e) => { if (_busy && MessageBox.Show(this, "An installation is running. Stop it and close?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true; else _cts?.Cancel(); };
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        var any = false;
        foreach (var s in McpCatalog.Status(_env))
        {
            any |= s.HostFound;
            var install = new Button
            {
                Content = s.Installed ? "Reinstall" : "Install", Padding = new Thickness(18, 8, 18, 8), MinWidth = 96, VerticalAlignment = VerticalAlignment.Center, IsEnabled = s.HostFound && !_busy,
                Style = (Style)Application.Current.FindResource(s.Installed ? "SecondaryButton" : "PrimaryButton"), ToolTip = s.HostFound ? null : s.HostText,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(install, "McpInstall_" + s.Product.Id);
            var product = s.Product; install.Click += async (_, _) => await InstallManyAsync([product]);
            var badge = new TextBlock
            {
                Text = s.Installed ? "Installed" : s.HostFound ? "Ready to install" : "Program not found on this PC", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = s.Installed ? new SolidColorBrush(Color.FromRgb(0x1F, 0x8A, 0x4C)) : s.HostFound ? (Brush)Application.Current.FindResource("Accent") : (Brush)Application.Current.FindResource("InkMuted"),
            };
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = s.Product.Title, FontSize = 15, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = s.Product.Description, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("InkMuted"), Margin = new Thickness(0, 2, 0, 4) },
                    new TextBlock { Text = (s.HostFound ? "Found: " : "") + s.HostText + "   ·   Source: " + s.Product.Source, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("InkMuted"), TextWrapping = TextWrapping.Wrap },
                    badge,
                },
            };
            var dock = new DockPanel(); DockPanel.SetDock(install, Dock.Right); dock.Children.Add(install); dock.Children.Add(text);
            _rows.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), Child = dock });
        }
        _all.IsEnabled = any && !_busy;
    }

    private async Task InstallManyAsync(IReadOnlyList<McpProduct> products)
    {
        if (_busy || products.Count == 0) return;
        _busy = true; _cts = new CancellationTokenSource(); _all.IsEnabled = false; _cancel.Visibility = Visibility.Visible; _log.Clear(); Refresh();
        var done = new List<McpInstallResult>();
        foreach (var p in products)
        {
            _stepText.Text = "Installing " + p.Title + "..."; _bar.Value = 0;
            var r = await McpCatalog.InstallAsync(p, _env,
                m => Dispatcher.Invoke(() => { _log.AppendText(m + Environment.NewLine); _log.ScrollToEnd(); }),
                s => Dispatcher.Invoke(() => _stepText.Text = p.Title + ": " + s),
                pc => Dispatcher.Invoke(() => _bar.Value = pc), _cts.Token);
            done.Add(r);
            if (!r.Ok) { _stepText.Text = r.Message; if (r.NeedsClose) break; }
            if (_cts.IsCancellationRequested) break;
        }
        _busy = false; _cancel.Visibility = Visibility.Collapsed; Refresh();
        var okAll = done.Count > 0 && done.All(d => d.Ok);
        _bar.Value = okAll ? 100 : _bar.Value;
        var summary = string.Join("\n\n", done.Select(d => (d.Ok ? "✔ " : "✖ ") + d.Message + (d.Ok ? "" : "\nLog: " + d.LogFile)));
        _stepText.Text = okAll ? "Done. Restart Claude to use the new servers." : done.LastOrDefault()?.Message ?? "Stopped.";
        MessageBox.Show(this, summary, Title, MessageBoxButton.OK, okAll ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}

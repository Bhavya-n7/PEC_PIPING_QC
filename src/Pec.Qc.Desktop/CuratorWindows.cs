using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Pec.Qc.Core;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.Util;

namespace Pec.Qc.Desktop;

/// <summary>For the QC lead and the administrator: the lessons the team recorded that nobody has decided on, to approve into the company list or reject.</summary>
internal static class LessonReviewWindow
{
    public static void Show(Window owner, Settings settings)
    {
        var curator = new LessonCurator(settings);
        var who = UserIdentity.Current(settings);
        var list = new StackPanel();
        var status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("InkMuted") };
        AutomationProperties.SetAutomationId(status, "ReviewStatus");
        var boxes = new List<(CheckBox Box, string Id)>();

        void Load()
        {
            list.Children.Clear(); boxes.Clear();
            var pending = curator.Pending();
            if (pending.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing to review: every lesson has been decided.", Margin = new Thickness(0, 8, 0, 0) });
            foreach (var p in pending)
            {
                var l = p.Lesson;
                var box = new CheckBox
                {
                    Margin = new Thickness(0, 0, 0, 12),
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
                        Text = $"[{l.Kind}{(p.Strong ? $", strong: {p.People} people" : "")}] {l.Text}\n{l.User} {l.EmployeeCode}, {l.Ts[..Math.Min(10, l.Ts.Length)]}" + (l.Context.Length > 0 ? $"  |  {l.Context}" : ""),
                    },
                };
                AutomationProperties.SetAutomationId(box, "Lesson_" + l.Id);
                boxes.Add((box, l.Id)); list.Children.Add(box);
            }
        }

        string[] Selected() => boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToArray();
        var approve = new Button { Content = "Approve selected", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) };
        var reject = new Button { Content = "Reject selected", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) };
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(18, 8, 18, 8) };
        AutomationProperties.SetAutomationId(approve, "ApproveLessons"); AutomationProperties.SetAutomationId(reject, "RejectLessons");
        approve.Click += (_, _) =>
        {
            var ids = Selected(); if (ids.Length == 0) { status.Text = "Tick the lessons to approve first."; return; }
            try
            {
                var r = curator.Approve(ids, who.Display);
                AuditLog.Write(settings, "learn_review", "(app)", "approve|" + string.Join(",", ids.OrderBy(x => x)), $"approved {r.Approved}");
                status.Text = $"Approved {r.Approved} lesson(s). LEARNED.md was rebuilt; every PC reads it at the start of its next session.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not write to the shared folder: " + e.Message; }
            Load();
        };
        reject.Click += (_, _) =>
        {
            var ids = Selected(); if (ids.Length == 0) { status.Text = "Tick the lessons to reject first."; return; }
            try
            {
                var r = curator.Reject(ids, who.Display, "rejected in the app");
                AuditLog.Write(settings, "learn_review", "(app)", "reject|" + string.Join(",", ids.OrderBy(x => x)), $"rejected {r.Rejected}");
                status.Text = $"Rejected {r.Rejected} lesson(s): they are hidden from everyone.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = "Could not write to the shared folder: " + e.Message; }
            Load();
        };

        var win = new Window
        {
            Title = "Lessons to review", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 760, Height = 620,
            Background = (Brush)Application.Current.FindResource("AppBg"), FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13,
            Content = new DockPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    Docked(new TextBlock { Text = "What the team has taught the assistant", FontSize = 20, FontWeight = FontWeights.SemiBold }, Dock.Top),
                    Docked(new TextBlock { Text = "Lessons help everyone the moment they are recorded. Approve the ones that are true rules into the company list; reject wrong ones to hide them. Strong = several different people recorded the same thing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14), Foreground = (Brush)Application.Current.FindResource("InkMuted") }, Dock.Top),
                    Docked(new StackPanel { Orientation = Orientation.Vertical, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { approve, reject, close } }, status } }, Dock.Bottom),
                    new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                },
            },
        };
        AutomationProperties.SetAutomationId(win, "LessonReviewWindow");
        Load();
        win.ShowDialog();
    }

    private static UIElement Docked(UIElement e, Dock d) { DockPanel.SetDock(e, d); return e; }
}

/// <summary>For the QC lead and the administrator: who used the assistant, on which days, how often it was corrected and where tools failed.</summary>
internal static class ActivityWindow
{
    public static void Show(Window owner, Settings settings)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        void Col(string header, string path, double width = 80) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width });
        Col("Day", "Day", 90); Col("Person", "User", 150); Col("Code", "EmployeeCode", 80); Col("Prompts", "Prompts", 70); Col("Corrections", "Corrections", 90); Col("Tool calls", "ToolCalls", 80);
        Col("Tool errors", "ToolErrors", 85); Col("Errors", "Errors", 60); Col("Chats", "Chats", 55); Col("Most used tools", "ToolsText", 260);
        AutomationProperties.SetAutomationId(grid, "ActivityGrid");
        var summary = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)Application.Current.FindResource("InkMuted"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(summary, "ActivitySummary");
        var days = new ComboBox { Width = 140, ItemsSource = new[] { "Last day", "Last 7 days", "Last 30 days" }, SelectedIndex = 1, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(days, "ActivityDays");

        void Load()
        {
            var n = days.SelectedIndex switch { 0 => 1, 2 => 30, _ => 7 };
            var rows = ActivityLog.Read(settings, DateTime.UtcNow.AddDays(-n), null, null, null, 2000);
            grid.ItemsSource = ActivitySummary.ByDay(rows).Select(d => new
            {
                d.Day, d.User, d.EmployeeCode, d.Prompts, d.Corrections, d.ToolCalls, d.ToolErrors, d.Errors, d.Chats, ToolsText = string.Join(", ", d.TopTools),
            }).ToList();
            summary.Text = rows.Count == 0 ? "Nothing logged in that time." : $"{rows.Count} lines from {rows.Select(r => r.User).Distinct().Count()} person(s). Corrections and tool errors are where the assistant was inaccurate: ask it to learn from them.";
        }
        days.SelectionChanged += (_, _) => Load();

        var win = new Window
        {
            Title = "Team activity", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 980, Height = 560,
            Background = (Brush)Application.Current.FindResource("AppBg"), FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13,
            Content = new DockPanel { Margin = new Thickness(22), Children = { Docked(new TextBlock { Text = "Who used the assistant", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }, Dock.Top), Docked(days, Dock.Top), Docked(summary, Dock.Bottom), grid } },
        };
        AutomationProperties.SetAutomationId(win, "ActivityWindow");
        Load();
        win.ShowDialog();
    }

    private static UIElement Docked(UIElement e, Dock d) { DockPanel.SetDock(e, d); return e; }
}

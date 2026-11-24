using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pec.Qc.Desktop;

/// <summary>A small Markdown reader for the guide and the chat: headings, bullets, numbered lists, tables, bold, inline code, paragraphs.</summary>
public static class MarkdownView
{
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x27, 0x48, 0xC9));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x11, 0x19, 0x36));
    private static readonly Brush CodeBack = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF9));
    private static readonly Brush HeaderBack = new SolidColorBrush(Color.FromRgb(0xEA, 0xEF, 0xFF));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xE2, 0xE7, 0xF1));

    /// <summary>The guide: large headings, with a contents list (the level-2 headings).</summary>
    public static (FlowDocument Doc, List<(string Title, Block Target)> Toc) Render(string markdown) => Build(markdown, chat: false);

    /// <summary>A chat answer: compact sizes, no contents.</summary>
    public static FlowDocument RenderChat(string markdown) => Build(markdown, chat: true).Doc;

    private static (FlowDocument Doc, List<(string, Block)> Toc) Build(string markdown, bool chat)
    {
        var doc = new FlowDocument
        {
            Background = chat ? Brushes.Transparent : Brushes.White, Foreground = Ink, FontFamily = new FontFamily("Segoe UI"), FontSize = chat ? 14 : 14,
            PagePadding = chat ? new Thickness(0) : new Thickness(28, 20, 28, 28), LineHeight = chat ? 21 : 22,
        };
        var toc = new List<(string, Block)>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            Block block;
            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(lines[i++].Trim());
                i--;
                block = Table(rows, chat);
            }
            else if (line.StartsWith("# ")) block = Heading(line[2..], chat ? 18 : 26, new Thickness(0, chat ? 6 : 0, 0, chat ? 6 : 10));
            else if (line.StartsWith("## "))
            {
                block = Heading(line[3..], chat ? 16 : 19, new Thickness(0, chat ? 10 : 22, 0, 6));
                toc.Add((line[3..], block));
            }
            else if (line.StartsWith("### ")) block = Heading(line[4..], chat ? 14.5 : 16, new Thickness(0, chat ? 8 : 14, 0, 4));
            else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ")) block = Bullet("•", line.TrimStart()[2..], chat);
            else if (Regex.Match(line, @"^(\d+)\.\s+(.*)$") is { Success: true } n) block = Bullet(n.Groups[1].Value + ".", n.Groups[2].Value, chat);
            else block = Paragraph(line, chat);
            doc.Blocks.Add(block);
        }
        return (doc, toc);
    }

    private static Paragraph Heading(string text, double size, Thickness margin)
    {
        var p = new Paragraph { FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = margin };
        AddInlines(p, text);
        return p;
    }

    private static Paragraph Paragraph(string text, bool chat)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, chat ? 6 : 8) };
        AddInlines(p, text);
        return p;
    }

    private static Paragraph Bullet(string marker, string text, bool chat)
    {
        var p = new Paragraph { Margin = new Thickness(chat ? 14 : 18, 0, 0, chat ? 3 : 3), TextIndent = -16 };
        p.Inlines.Add(new Run(marker + " ") { Foreground = Blue, FontWeight = FontWeights.SemiBold });
        AddInlines(p, text);
        return p;
    }

    private static Table Table(List<string> rows, bool chat)
    {
        static List<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
        var parsed = rows.Select(Cells).ToList();
        var header = parsed[0];
        var body = parsed.Skip(1).Where(r => !r.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))).ToList();     // drop the |---|---| row
        var cols = Math.Max(header.Count, body.Count == 0 ? 0 : body.Max(r => r.Count));
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), BorderBrush = Line, BorderThickness = new Thickness(1) };
        for (var c = 0; c < cols; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        void AddRow(List<string> cells, bool head)
        {
            var row = new TableRow { Background = head ? HeaderBack : Brushes.Transparent };
            for (var c = 0; c < cols; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0), FontSize = chat ? 12.5 : 13, FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal };
                AddInlines(p, c < cells.Count ? cells[c] : "");
                row.Cells.Add(new TableCell(p) { Padding = new Thickness(8, 5, 8, 5), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            group.Rows.Add(row);
        }
        AddRow(header, true);
        foreach (var r in body) AddRow(r, false);
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddInlines(Paragraph p, string text)
    {
        foreach (var part in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4) p.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2) p.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = CodeBack, FontSize = 12.5 });
            else p.Inlines.Add(new Run(part));
        }
    }
}

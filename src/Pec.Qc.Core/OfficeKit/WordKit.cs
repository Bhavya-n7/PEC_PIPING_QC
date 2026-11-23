using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Pec.Qc.Core.OfficeKit;

/// <summary>Create and modify Word documents (.docx) without Word installed (Open XML SDK, MIT).</summary>
public static class WordKit
{
    /// <summary>Blocks as JSON: [{"type":"heading","level":1,"text":"Title"},{"type":"paragraph","text":"...","bold":true},{"type":"bullets","items":["a","b"]},{"type":"numbered","items":[..]},{"type":"table","rows":[["h1","h2"],["a","b"]]},{"type":"pagebreak"}]</summary>
    public static string Create(string path, string blocksJson, bool overwrite)
    {
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) path += ".docx";
        if (File.Exists(path) && !overwrite) throw new IOException($"{path} already exists: pass overwrite=true to replace it, or use word_edit to change it.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        AppendBlocks(main.Document.Body!, blocksJson);
        main.Document.Save();
        return path;
    }

    public static (string Saved, IReadOnlyList<string> Done, string? Backup) Edit(string path, string opsJson, string? saveAs)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        if (path.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("The old .doc format cannot be edited here: convert it to .docx first.");
        var target = string.IsNullOrWhiteSpace(saveAs) ? path : (saveAs!.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? saveAs : saveAs + ".docx");
        string? bak = null;
        if (File.Exists(target)) { bak = target + ".bak_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Copy(target, bak, true); }
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(path, target, true);
        var done = new List<string>();
        using (var doc = WordprocessingDocument.Open(target, true))
        {
            var main = doc.MainDocumentPart ?? throw new InvalidDataException("The file has no document part.");
            var body = main.Document!.Body ?? throw new InvalidDataException("The document has no body.");
            using var ops = JsonDocument.Parse(opsJson);
            if (ops.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'operations' must be a JSON array.");
            var i = 0;
            foreach (var op in ops.RootElement.EnumerateArray())
            {
                i++;
                var kind = (ExcelKit.Str(op, "op") ?? "").ToLowerInvariant();
                switch (kind)
                {
                    case "replace":
                    {
                        var find = ExcelKit.Str(op, "find") ?? throw new ArgumentException($"Operation {i}: missing 'find'.");
                        var rep = ExcelKit.Str(op, "replace") ?? "";
                        var n = 0;
                        foreach (var p in AllParagraphs(main).ToList()) n += ReplaceInParagraph(p, find, rep);
                        done.Add($"replace '{find}' → '{rep}': {n} place(s)");
                        break;
                    }
                    case "append":
                    {
                        var blocks = op.TryGetProperty("blocks", out var b) ? b.GetRawText() : throw new ArgumentException($"Operation {i}: missing 'blocks'.");
                        var sect = body.Elements<SectionProperties>().LastOrDefault();
                        var tmp = new Body(); AppendBlocks(tmp, blocks);
                        foreach (var el in tmp.ChildElements.ToList()) { el.Remove(); if (sect is not null) body.InsertBefore(el, sect); else body.AppendChild(el); }
                        done.Add("append blocks");
                        break;
                    }
                    case "deleteparagraph":
                    {
                        var find = ExcelKit.Str(op, "contains") ?? throw new ArgumentException($"Operation {i}: missing 'contains'.");
                        var n = 0;
                        foreach (var p in body.Descendants<Paragraph>().Where(p => p.InnerText.Contains(find, StringComparison.OrdinalIgnoreCase)).ToList()) { p.Remove(); n++; }
                        done.Add($"delete paragraphs containing '{find}': {n}");
                        break;
                    }
                    case "setcell":
                    {
                        var t = op.TryGetProperty("table", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetInt32() : 1;
                        var row = op.GetProperty("row").GetInt32(); var col = op.GetProperty("column").GetInt32();
                        var tbl = body.Elements<Table>().ElementAtOrDefault(t - 1) ?? throw new ArgumentException($"Operation {i}: table {t} does not exist.");
                        var cell = tbl.Elements<TableRow>().ElementAtOrDefault(row - 1)?.Elements<TableCell>().ElementAtOrDefault(col - 1) ?? throw new ArgumentException($"Operation {i}: cell {row},{col} does not exist.");
                        var par = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
                        par.RemoveAllChildren<Run>();
                        par.AppendChild(new Run(new Text(ExcelKit.Str(op, "text") ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                        done.Add($"table {t} cell {row},{col}");
                        break;
                    }
                    default: throw new ArgumentException($"Operation {i}: unknown op '{kind}'. Known: replace, append, deleteParagraph, setCell.");
                }
            }
            main.Document.Save();
        }
        return (target, done, bak);
    }

    private static IEnumerable<Paragraph> AllParagraphs(MainDocumentPart main)
    {
        foreach (var p in main.Document!.Descendants<Paragraph>()) yield return p;
        foreach (var h in main.HeaderParts) foreach (var p in h.Header.Descendants<Paragraph>()) yield return p;
        foreach (var f in main.FooterParts) foreach (var p in f.Footer.Descendants<Paragraph>()) yield return p;
    }

    /// <summary>Replaces text even when Word split it over several runs: the paragraph text is rebuilt in the first run (the formatting of that run is kept).</summary>
    private static int ReplaceInParagraph(Paragraph p, string find, string rep)
    {
        var texts = p.Descendants<Text>().ToList();
        if (texts.Count == 0) return 0;
        var full = string.Concat(texts.Select(t => t.Text));
        if (!full.Contains(find, StringComparison.Ordinal)) return 0;
        var count = (full.Length - full.Replace(find, "", StringComparison.Ordinal).Length) / find.Length;
        // fast path: the text lies inside single runs
        var inRun = texts.Any(t => t.Text.Contains(find, StringComparison.Ordinal));
        if (inRun && texts.All(t => !PartialAcross(texts, t, find)))
        {
            foreach (var t in texts) if (t.Text.Contains(find, StringComparison.Ordinal)) { t.Text = t.Text.Replace(find, rep, StringComparison.Ordinal); t.Space = SpaceProcessingModeValues.Preserve; }
            return count;
        }
        texts[0].Text = full.Replace(find, rep, StringComparison.Ordinal); texts[0].Space = SpaceProcessingModeValues.Preserve;
        for (var k = 1; k < texts.Count; k++) texts[k].Text = "";
        return count;
    }

    private static bool PartialAcross(List<Text> texts, Text t, string find)
    {
        // true when a match would start inside t but end in a later run (so a per-run replace would miss it)
        var idx = texts.IndexOf(t);
        if (idx == texts.Count - 1) return false;
        for (var len = 1; len < find.Length; len++)
            if (t.Text.EndsWith(find[..len], StringComparison.Ordinal) && texts[idx + 1].Text.StartsWith(find[len..], StringComparison.Ordinal)) return true;
        return false;
    }

    private static void AppendBlocks(Body body, string blocksJson)
    {
        using var doc = JsonDocument.Parse(blocksJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'blocks' must be a JSON array of {type, ...}.");
        var sect = body.Elements<SectionProperties>().LastOrDefault();
        void Add(OpenXmlElement e) { if (sect is not null) body.InsertBefore(e, sect); else body.AppendChild(e); }
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            var type = (ExcelKit.Str(b, "type") ?? "paragraph").ToLowerInvariant();
            switch (type)
            {
                case "heading":
                {
                    var level = b.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? Math.Clamp(lv.GetInt32(), 1, 3) : 1;
                    Add(Para(ExcelKit.Str(b, "text") ?? "", "Heading" + level, true));
                    break;
                }
                case "paragraph": Add(Para(ExcelKit.Str(b, "text") ?? "", null, ExcelKit.Bool(b, "bold") == true, ExcelKit.Bool(b, "italic") == true)); break;
                case "bullets":
                case "numbered":
                {
                    var n = 0;
                    foreach (var it in b.GetProperty("items").EnumerateArray())
                    {
                        var p = Para((type == "bullets" ? "•  " : (++n) + ".  ") + (it.GetString() ?? ""), null, false);
                        p.PrependChild(new ParagraphProperties(new Indentation { Left = "540", Hanging = "270" }));
                        Add(p);
                    }
                    break;
                }
                case "table":
                {
                    var t = new Table(new TableProperties(
                        new TableBorders(
                            new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                            new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                        new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
                    var first = true;
                    foreach (var row in b.GetProperty("rows").EnumerateArray())
                    {
                        var tr = new TableRow();
                        foreach (var c in row.EnumerateArray())
                        {
                            var cellText = c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString();
                            var tc = new TableCell(Para(cellText, null, first && ExcelKit.Bool(b, "header") != false));
                            if (first && ExcelKit.Bool(b, "header") != false) tc.TableCellProperties = new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "E8EEF7" });
                            tr.AppendChild(tc);
                        }
                        t.AppendChild(tr); first = false;
                    }
                    Add(t); Add(new Paragraph());
                    break;
                }
                case "pagebreak": Add(new Paragraph(new Run(new Break { Type = BreakValues.Page }))); break;
                default: throw new ArgumentException("unknown block type '" + type + "'. Known: heading, paragraph, bullets, numbered, table, pagebreak.");
            }
        }
    }

    private static Paragraph Para(string text, string? style, bool bold, bool italic = false)
    {
        var rp = new RunProperties();
        if (bold) rp.AppendChild(new Bold());
        if (italic) rp.AppendChild(new Italic());
        var p = new Paragraph();
        if (style is not null) p.AppendChild(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var run = new Run(rp.HasChildren ? (RunProperties)rp.CloneNode(true) : new RunProperties());
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.AppendChild(new Break());
            run.AppendChild(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        p.AppendChild(run);
        return p;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }))));
        styles.AppendChild(new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        var sizes = new[] { "36", "30", "26" };
        for (var i = 1; i <= 3; i++)
            styles.AppendChild(new Style(new StyleName { Val = "heading " + i }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "80" }, new OutlineLevel { Val = i - 1 }),
                new StyleRunProperties(new Bold(), new Color { Val = "1F3864" }, new FontSize { Val = sizes[i - 1] }))
            { Type = StyleValues.Paragraph, StyleId = "Heading" + i });
        sp.Styles = styles;
        sp.Styles.Save();
    }
}

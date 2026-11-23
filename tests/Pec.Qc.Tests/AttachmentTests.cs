using System.IO.Compression;
using System.Text;
using Pec.Qc.Core.Claude;
using Pec.Qc.Core.Sources;

namespace Pec.Qc.Tests;

public class OfficeTextTests
{
    private static string Zip(TempDir t, string name, params (string Entry, string Xml)[] parts)
    {
        var path = System.IO.Path.Combine(t.Path, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, xml) in parts) { using var w = new StreamWriter(z.CreateEntry(e).Open(), new UTF8Encoding(false)); w.Write(xml); }
        return path;
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    [Fact] public void Word_text_headings_and_tables_are_read()
    {
        using var t = new TempDir();
        var f = Zip(t, "a.docx", ("word/document.xml", $"<w:document {W}><w:body>" +
            "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Scope</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Design pressure</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>10 bar</w:t></w:r></w:p>" +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Tag</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Size</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>P-101</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>6 in</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
            "</w:body></w:document>"));
        var r = OfficeText.Read(f);
        Assert.Equal("Word document", r.Kind);
        Assert.Contains("# Scope", r.Text); Assert.Contains("Design pressure\t10 bar", r.Text);
        Assert.Contains("Tag | Size", r.Text); Assert.Contains("P-101 | 6 in", r.Text);
    }

    private static string Workbook(TempDir t) => Zip(t, "a.xlsx",
        ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"Equipment\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Lines\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/></Relationships>"),
        ("xl/sharedStrings.xml", $"<sst {S}><si><t>Tag</t></si><si><t>Height</t></si><si><t>T-101</t></si></sst>"),
        ("xl/worksheets/sheet1.xml", $"<worksheet {S}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\" t=\"s\"><v>1</v></c></row>" +       // A and C: B is empty and must stay an empty column
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"C2\"><v>12.5</v></c></row></sheetData></worksheet>"),
        ("xl/worksheets/sheet2.xml", $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>L-1</t></is></c><c r=\"B1\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>"));

    [Fact] public void Excel_sheets_keep_their_columns_and_resolve_shared_strings()
    {
        using var t = new TempDir();
        var r = OfficeText.Read(Workbook(t));
        Assert.Equal(["Equipment", "Lines"], r.Sheets);
        Assert.Contains("=== Sheet: Equipment ===", r.Text);
        Assert.Contains("Tag\t\tHeight", r.Text);          // the empty B column is kept
        Assert.Contains("T-101\t\t12.5", r.Text);
        Assert.Contains("L-1\tTRUE", r.Text);
    }

    [Fact] public void One_sheet_can_be_asked_for_by_name_or_number_and_a_wrong_one_lists_the_sheets()
    {
        using var t = new TempDir(); var f = Workbook(t);
        Assert.DoesNotContain("Equipment", OfficeText.Read(f, "Lines").Text);
        Assert.Contains("Equipment", OfficeText.Read(f, "1").Text);
        var e = Assert.Throws<ArgumentException>(() => OfficeText.Read(f, "Nope"));
        Assert.Contains("Equipment, Lines", e.Message);
    }

    [Fact] public void PowerPoint_slides_are_read_in_numeric_order()
    {
        using var t = new TempDir();
        string Slide(string text) => $"<p:sld xmlns:p=\"p\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
        var f = Zip(t, "a.pptx", ("ppt/slides/slide10.xml", Slide("tenth")), ("ppt/slides/slide2.xml", Slide("second")), ("ppt/slides/slide1.xml", Slide("first")));
        var text = OfficeText.Read(f).Text;
        Assert.True(text.IndexOf("first") < text.IndexOf("second") && text.IndexOf("second") < text.IndexOf("tenth"));
    }

    [Fact] public void Csv_is_cut_at_the_row_limit_and_reports_it()
    {
        using var t = new TempDir();
        var f = t.File("a.csv", string.Join("\n", Enumerable.Range(1, 50).Select(i => $"r{i},x")));
        var r = OfficeText.Read(f, maxRows: 10);
        Assert.True(r.Truncated); Assert.Contains("r10,x", r.Text); Assert.DoesNotContain("r11,x", r.Text);
    }

    [Fact] public void Old_office_formats_and_broken_files_give_a_clear_message()
    {
        using var t = new TempDir();
        Assert.Contains("docx, xlsx, pptx", Assert.Throws<NotSupportedException>(() => OfficeText.Read(t.File("a.xls", "x"))).Message);
        Assert.Contains("not a valid Office file", Assert.Throws<InvalidDataException>(() => OfficeText.Read(t.File("b.docx", "this is not a zip"))).Message);
        Assert.Throws<FileNotFoundException>(() => OfficeText.Read(System.IO.Path.Combine(t.Path, "none.docx")));
    }
}

public class ChatAttachmentTests
{
    [Theory]
    [InlineData("photo.JPG", AttachKind.Image)] [InlineData("scan.png", AttachKind.Image)] [InlineData("spec.pdf", AttachKind.Pdf)]
    [InlineData("list.xlsx", AttachKind.Office)] [InlineData("note.docx", AttachKind.Office)] [InlineData("a.dxf", AttachKind.Cad)]
    [InlineData("a.dwg", AttachKind.Cad)] [InlineData("plant.e57", AttachKind.Scan)] [InlineData("m.nwd", AttachKind.Navis)]
    [InlineData("old.xls", AttachKind.OldOffice)] [InlineData("a.txt", AttachKind.Text)] [InlineData("a.xyz", AttachKind.Other)]
    public void Files_are_classified_by_extension(string name, AttachKind kind) => Assert.Equal(kind, ChatAttachments.Classify(name));

    [Fact] public void A_file_is_copied_into_the_workspace_with_a_safe_unique_name()
    {
        using var t = new TempDir(); var src = t.File("in/my report (final).xlsx", "data"); var work = System.IO.Path.Combine(t.Path, "work");
        var b = ChatAttachments.Batch();
        var a1 = ChatAttachments.Stage(src, work, b); var a2 = ChatAttachments.Stage(src, work, b);
        Assert.StartsWith(work, a1.Path); Assert.True(File.Exists(a1.Path)); Assert.NotEqual(a1.Path, a2.Path);
        Assert.Equal(AttachKind.Office, a1.Kind);
        Assert.True(File.Exists(src));                                                    // the user's original stays
    }

    [Fact] public void Empty_missing_and_oversized_files_are_refused_with_a_message()
    {
        using var t = new TempDir(); var work = t.Path + "w";
        Assert.Contains("empty", Assert.Throws<InvalidDataException>(() => ChatAttachments.Stage(t.File("e.pdf", ""), work, "b")).Message);
        Assert.Throws<FileNotFoundException>(() => ChatAttachments.Stage(System.IO.Path.Combine(t.Path, "none.pdf"), work, "b"));
        var big = System.IO.Path.Combine(t.Path, "big.pdf");
        using (var fs = File.Create(big)) fs.SetLength(ChatAttachments.MaxBytes + 1);
        Assert.Contains("limit", Assert.Throws<InvalidDataException>(() => ChatAttachments.Stage(big, work, "b")).Message);
    }

    [Fact] public void The_prompt_note_names_every_file_and_how_to_open_it()
    {
        var files = new[]
        {
            new Attachment(@"C:\w\a.xlsx", "a.xlsx", AttachKind.Office, 2048), new Attachment(@"C:\w\b.png", "b.png", AttachKind.Image, 1_500_000),
            new Attachment(@"C:\w\c.dxf", "c.dxf", AttachKind.Cad, 10), new Attachment(@"C:\w\d.dwg", "d.dwg", AttachKind.Cad, 10),
        };
        var note = ChatAttachments.PromptNote(files);
        Assert.Contains("attached 4 files", note);
        Assert.Contains("a.xlsx (2 KB) - read it with the file_read tool", note);
        Assert.Contains("b.png (1.4 MB) - an image: look at it with the Read tool", note);
        Assert.Contains("dxf_read", note); Assert.Contains("save it as DXF", note);
        Assert.Contains(@"Path: C:\w\a.xlsx", note);
        Assert.Equal("", ChatAttachments.PromptNote([]));
    }

    [Fact] public void Pasted_image_bytes_are_saved_and_old_batches_are_cleaned()
    {
        using var t = new TempDir(); var work = t.Path;
        var a = ChatAttachments.StageBytes([1, 2, 3], "pasted-image.png", work, "old");
        Assert.True(File.Exists(a.Path)); Assert.Equal(AttachKind.Image, a.Kind);
        Directory.SetLastWriteTime(System.IO.Path.GetDirectoryName(a.Path)!, DateTime.Now.AddDays(-9));
        ChatAttachments.StageBytes([1], "new.png", work, "new");
        Assert.Equal(1, ChatAttachments.Cleanup(work, 7));
        Assert.False(File.Exists(a.Path));
    }
}

public class ChatArgumentTests
{
    [Fact] public void Without_a_readable_folder_claude_has_no_builtin_tools()
    {
        var a = ClaudeCli.ChatArguments("m.json", "SYS", null);
        Assert.Equal("", a[a.IndexOf("--tools") + 1]);
        Assert.DoesNotContain(a, x => x.StartsWith("Read("));
    }

    [Fact] public void With_a_readable_folder_only_Read_is_enabled_and_limited_to_that_folder()
    {
        var a = ClaudeCli.ChatArguments("m.json", "SYS", null, readableFolder: @"C:\Users\x\work\attachments");
        Assert.Equal("Read", a[a.IndexOf("--tools") + 1]);
        var i = a.IndexOf("--allowedTools");
        Assert.Equal("Read(//C/Users/x/work/attachments/**)", a[i + 2]);
        Assert.StartsWith("mcp__", a[i + 1]);
        Assert.StartsWith("--", a[i + 3]);                                    // the rule belongs to --allowedTools, not to a stray argument
    }

    [Fact] public void The_read_rule_uses_the_absolute_form_without_a_drive_colon()
    {
        Assert.Equal("Read(//D/data/PEC/work/attachments/**)", ChatAttachments.ReadRule(@"D:\data\PEC\work\attachments\"));
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Claude;

public enum AttachKind { Image, Pdf, Office, Text, Cad, Scan, Navis, OldOffice, Other }

public sealed record Attachment(string Path, string Name, AttachKind Kind, long Size);

/// <summary>
/// Files a user attaches to a chat message. They are copied into the assistant's workspace (the folder Claude Code runs in) so Claude can read them by path, and the
/// message carries a short note naming each file and the right way to read it: images, PDFs and text with Claude's Read tool, Word / Excel / PowerPoint with
/// the file_read tool, and drawings and models (PDF, NWD) with the matching QC tool.
/// </summary>
public static class ChatAttachments
{
    public const long MaxBytes = 30L * 1024 * 1024;
    public const int MaxFiles = 10;

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".xlsm", ".pptx", ".csv", ".tsv" };
    private static readonly HashSet<string> OldOffice = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".xls", ".ppt" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".log", ".sql", ".yaml", ".yml", ".html", ".htm", ".ini", ".cfg", ".bat", ".ps1", ".py", ".cs", ".js", ".ts", ".css", ".pcf", ".csv",
    };

    public static AttachKind Classify(string name)
    {
        var ext = Path.GetExtension(name);
        if (Images.Contains(ext)) return AttachKind.Image;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return AttachKind.Pdf;
        if (Office.Contains(ext)) return AttachKind.Office;
        if (OldOffice.Contains(ext)) return AttachKind.OldOffice;
        if (ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return AttachKind.Cad;
        if (ext.Equals(".e57", StringComparison.OrdinalIgnoreCase)) return AttachKind.Scan;
        if (ext.Equals(".nwd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".nwc", StringComparison.OrdinalIgnoreCase)) return AttachKind.Navis;
        if (Text.Contains(ext)) return AttachKind.Text;
        return AttachKind.Other;
    }

    /// <summary>The Claude Code permission rule that lets the Read tool open files under the attachments folder and nowhere else (absolute path form: //C/dir/**).</summary>
    public static string ReadRule(string attachmentsFolder) => PathRule("Read", attachmentsFolder);

    /// <summary>A permission rule for one tool limited to a folder: Tool(//C/dir/**).</summary>
    public static string PathRule(string tool, string folder)
    {
        var p = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
        if (p.Length > 1 && p[1] == ':') p = p[0] + p[2..];
        return tool + "(//" + p + "/**)";
    }

    public static string Batch() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    private static string Safe(string name)
    {
        var n = Regex.Replace(Path.GetFileName(name), @"[^\w.\- ()]", "_").Trim();
        return n.Length == 0 ? "file" : n.Length > 120 ? n[^120..] : n;
    }

    private static string Folder(string workDir, string batch) => Path.Combine(workDir, "attachments", batch);

    /// <summary>Copies a file the user picked into the workspace and describes it. Throws a message fit to show the user when it cannot be attached.</summary>
    public static Attachment Stage(string source, string workDir, string batch)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException($"'{Path.GetFileName(source)}' was not found.");
        if (info.Length == 0) throw new InvalidDataException($"'{info.Name}' is empty.");
        if (info.Length > MaxBytes) throw new InvalidDataException($"'{info.Name}' is {info.Length / 1048576.0:0.#} MB; the limit is {MaxBytes / 1048576} MB per file.");
        var dir = Folder(workDir, batch); Directory.CreateDirectory(dir);
        var dest = Unique(dir, Safe(info.Name));
        File.Copy(info.FullName, dest, true);
        return new Attachment(dest, Path.GetFileName(dest), Classify(dest), info.Length);
    }

    /// <summary>For an image pasted from the clipboard (bytes, no file name).</summary>
    public static Attachment StageBytes(byte[] data, string name, string workDir, string batch)
    {
        if (data.Length == 0) throw new InvalidDataException("The pasted image is empty.");
        if (data.Length > MaxBytes) throw new InvalidDataException("The pasted image is too large.");
        var dir = Folder(workDir, batch); Directory.CreateDirectory(dir);
        var dest = Unique(dir, Safe(name));
        File.WriteAllBytes(dest, data);
        return new Attachment(dest, Path.GetFileName(dest), Classify(dest), data.Length);
    }

    private static string Unique(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        for (var i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return p;
    }

    /// <summary>Removes attachment batches older than the given number of days (the user's own copies stay where they were).</summary>
    public static int Cleanup(string workDir, int days = 7)
    {
        var root = Path.Combine(workDir, "attachments"); var n = 0;
        if (!Directory.Exists(root)) return 0;
        foreach (var d in Directory.GetDirectories(root))
            try { if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-days)) { Directory.Delete(d, true); n++; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return n;
    }

    public static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1048576 ? $"{bytes / 1024.0:0} KB" : $"{bytes / 1048576.0:0.#} MB";

    public static string HowToRead(Attachment a) => a.Kind switch
    {
        AttachKind.Image => "an image: look at it with the Read tool",
        AttachKind.Pdf => "a PDF (drawing or document): read it with the Read tool, or pdf_read for the text of every page (on a drawing that text carries the tags, line numbers and notes)",
        AttachKind.Office => "read it with the file_read tool (text of the Word, Excel, PowerPoint or CSV file); to read cells or change a workbook or document use excel_inspect, excel_read, excel_edit, word_edit",
        AttachKind.Text => "a text file: read it with the Read tool",
        AttachKind.Cad => "a CAD drawing (DXF or DWG): read its texts, tags, attributes, layers and line lengths with cad_read (a DWG is converted through AutoCAD on this PC); for how it looks, ask for a PDF export",
        AttachKind.Scan => "a laser scan (point cloud): there is no reader for it; say so, and ask what the user needs from it (a coordinate, a distance, a clash) so that it can be done another way, for example by a script tool or from the model",
        AttachKind.Navis => "a Navisworks model: export it to data with model_export (modelFile = this path; apply=true after the user agrees; it runs Navisworks headless on a copy and takes 30 s to some minutes), then model_summary / model_line; or set it as the project's model so every check uses it",
        AttachKind.OldOffice => "an old Office format: convert it (for example with Excel/Word through PowerShell COM, or LibreOffice) to xlsx, docx or pptx and read that; only if nothing works, tell the user to save it in the new format",
        _ => "an unfamiliar file type: look at its first bytes (Read, or a script tool) to see what it is; many engineering formats are text or zip archives and can be read; if it cannot be read with the tools here, say exactly that and ask for an export (PDF, DXF, Excel)",
    };

    /// <summary>The text added to the user's message so Claude knows what was attached and how to open each file.</summary>
    public static string PromptNote(IReadOnlyList<Attachment> files)
    {
        if (files.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine();
        sb.AppendLine($"[The user attached {files.Count} file{(files.Count == 1 ? "" : "s")}. They are on this PC; open each one before you answer.]");
        for (var i = 0; i < files.Count; i++)
            sb.AppendLine($"{i + 1}. {files[i].Name} ({Size(files[i].Size)}) - {HowToRead(files[i])}. Path: {files[i].Path}");
        sb.AppendLine("Do not claim to have read a file you did not open. If a file cannot be read, say so.");
        return sb.ToString().TrimEnd();
    }
}

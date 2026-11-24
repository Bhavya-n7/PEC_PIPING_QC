using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pec.Qc.Core.Knowledge;

namespace Pec.Qc.Mcp.Tools;

[McpServerToolType]
public static class TrainingTools
{
    private static string PackDir() => Path.Combine(Resolve.Settings().EffectiveDocumentsDirectory, "pack");

    [McpServerTool(Name = "convert_training")]
    [Description("Convert a folder of training and client documents (PDF, Markdown, text) into the knowledge pack in the documents folder: one Markdown file per document with page markers and headings, plus pack.json. Only changed documents are converted again, so run it whenever new training is added. The pack is a plain folder and can sit on a server. Then call build_manual_index so the new text is searchable.")]
    public static async Task<object> ConvertTraining(
        [Description("Folder with the documents to convert")] string sourceFolder,
        CancellationToken ct = default)
    {
        if (Resolve.Require(Pec.Qc.Core.Users.QcRole.Lead, "Converting documents into the shared knowledge pack") is { } denied) return denied;
        if (!Directory.Exists(sourceFolder)) throw new McpException($"Folder not found: {sourceFolder}");
        if (Resolve.NeedApproval("convert_training", Path.GetFullPath(sourceFolder).ToLowerInvariant(), $"Convert the documents in {sourceFolder} into the shared knowledge pack", PackDir()) is { } wait) return wait;
        var report = await Task.Run(() => TrainingPack.Convert(sourceFolder, PackDir()), ct);
        return new
        {
            pack = PackDir(), report.Converted, report.Unchanged, report.Failed, report.Messages,
            documents = report.Index.Documents.Select(d => new { d.Id, d.Title, d.Pages, sections = d.Sections.Count }),
            next = "Call build_manual_index to make the new documents searchable.",
        };
    }

    [McpServerTool(Name = "training_overview", ReadOnly = true)]
    [Description("What the knowledge pack contains: each document with its id, pages and the list of its main sections (title and page). Use it to find where something is, then read only that part with read_training.")]
    public static object TrainingOverview([Description("Also list sub-sections")] bool detailed = false)
    {
        var idx = TrainingPack.Load(PackDir()) ?? throw new McpException("There is no knowledge pack yet. Use convert_training on the folder with the training documents.");
        return new
        {
            updated = idx.Updated,
            documents = idx.Documents.Select(d => new
            {
                d.Id, d.Title, d.Pages,
                sections = d.Sections.Where(s => detailed || !s.Title.StartsWith("   ")).Take(detailed ? 400 : 60).Select(s => $"{s.Title} (p{s.Page})"),
            }),
        };
    }

    [McpServerTool(Name = "read_training", ReadOnly = true)]
    [Description("Read one part of a converted document: a section by (part of) its title, or a range of pages. Returns only that part, so it stays small. Quote the document and page when you rely on it.")]
    public static object ReadTraining(
        [Description("Document id from training_overview")] string document,
        [Description("Part of a section title, e.g. 'Weld Joint numbering'")] string? section = null,
        [Description("First page (when no section is given)")] int fromPage = 1,
        [Description("Last page (when no section is given)")] int toPage = 1)
    {
        var dir = PackDir();
        var text = !string.IsNullOrWhiteSpace(section)
            ? TrainingPack.ReadSection(dir, document, section)
            : TrainingPack.ReadPages(dir, document, fromPage, Math.Min(toPage, fromPage + 9));
        if (text is null) throw new McpException($"Nothing found for document '{document}'" + (section is null ? "" : $", section '{section}'") + ". Check training_overview.");
        const int Max = 12000;
        return new { document, section, text = text.Length <= Max ? text : text[..Max], truncated = text.Length > Max };
    }
}

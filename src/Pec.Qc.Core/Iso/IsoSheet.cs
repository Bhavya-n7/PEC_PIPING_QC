namespace Pec.Qc.Core.Iso;

/// <summary>One BOM line of an isometric. Qty is inches for PIPE and a count for everything else (null when the text is not a quantity).</summary>
public sealed record BomRow(string Section, string Category, string PtNo, string Description, string Size, string QtyText, double? Qty);

/// <summary>One piece of the cut pipe length table.</summary>
public sealed record CutPiece(string Piece, string LengthText, double? LengthIn, string Size);

/// <summary>An E / N / U coordinate label (inches) printed on the drawing. Cont is ON or FROM for a connection to another line (ContLine) and empty for a plain point.</summary>
public sealed record CoordLabel(double E, double N, double U, string Cont, string ContLine, string Raw);

/// <summary>"CONT. ON DRG 2" / "CONT. FROM DRG 1": the link from this sheet to another sheet of the same line.</summary>
public sealed record SheetLink(string Kind, int Drg);

/// <summary>What was read from one sheet (page) of an isometric PDF: title block, BOM, cut table, coordinates and sheet links.</summary>
public sealed class IsoSheet
{
    public string File { get; set; } = "";
    public int Page { get; set; }
    public string LineNo { get; set; } = "";
    public int Sheet { get; set; }
    public int Sheets { get; set; }
    public string Revision { get; set; } = "";
    public Dictionary<string, string> Params { get; } = new(StringComparer.Ordinal);
    public List<BomRow> Bom { get; } = [];
    public List<CutPiece> Cuts { get; } = [];
    public List<CoordLabel> Coords { get; } = [];
    public List<SheetLink> Links { get; } = [];
    public List<string> Warnings { get; } = [];
}

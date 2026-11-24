using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pec.Qc.Tests;

/// <summary>What the synthetic isometric and its matching model say. Change one value to make a drawing that is wrong in exactly that one way. Nothing here is client data.</summary>
public sealed record IsoSpec
{
    public string LineNo { get; init; } = "2\"-XX-10A00100-A100A-N-HS-001";
    public string ContLine { get; init; } = "2\"-XX-10A00200-A100A-N-HS-002";
    public string Class { get; init; } = "A100A";
    public string TestPress { get; init; } = "300";
    public string DesignPress { get; init; } = "200";
    public string DesignTemp { get; init; } = "325";
    public int Tees { get; init; } = 2;
    public int Supports { get; init; } = 2;
    public string Pipe1 { get; init; } = "5'0\"";       // cut piece 1 (2 pieces in all; the BOM pipe quantity is what the pieces add up to unless PipeBom is given)
    public string? PipeBom { get; init; }
    public string HeadE { get; init; } = "1000\"";      // the head coordinate printed on the drawing (the model head is at E 1000 in)
    public int Sheets { get; init; } = 1;      // "n / Sheets" printed in the corner
    public int Pages { get; init; } = 1;       // pages actually in the PDF
    public string Revision { get; init; } = "00";
    public string PidNo { get; init; } = "X-PID-0001-10";
}

public static class IsoTestKit
{
    public const double RefH = 841.889;

    private static readonly string[] Labels = ["TEST PRESS. (psig)", "TEST MEDIUM", "DESIGN TEMP. (deg F)", "DESIGN PRESS. (psig)", "OPER TEMP. (deg F)", "OPER PRESS. (psig)", "PAINT SYSTEM",
        "INSU. TYPE", "INSU. THK", "PHASE", "P&ID NO", "STRESS CRITICALITY", "PIPNG CLASS", "PWHT"];

    /// <summary>A two-sheet isometric in the layout the reader expects (A3 landscape: BOM at the right, cut table under it, title block bottom centre, coordinate labels).</summary>
    public static void WritePdf(string path, IsoSpec s)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        for (var sheet = 1; sheet <= s.Pages; sheet++)
        {
            var p = b.AddPage(1190.551, RefH);
            void T(double x, double y, string text) => p.AddText(text, 6, new PdfPoint(x, RefH - (y + 6)), font);   // y = the top of the line, as the reader measures it
            // BOM: fabrication
            T(817, 32, "FABRICATION MATERIALS");
            T(817, 51, "PT"); T(836, 51, "COMPONENT DESCRIPTION"); T(1084, 51, "N.S."); T(1122, 51, "QTY"); T(817, 61, "NO"); T(1084, 61, "(INS)");
            T(836, 80, "PIPE");
            var pipeBom = s.PipeBom ?? "10'0\"";
            T(821, 99, "1"); T(836, 99, "Seamless, Pipes, ASME B36.19, ASTM A312"); T(1084, 99, "2"); T(1122, 99, pipeBom);
            T(836, 128, "FITTINGS");
            T(821, 147, "2"); T(836, 147, "Equal Tee, Seamless, ASME B16.9"); T(1084, 147, "2 x 2"); T(1127, 147, s.Tees.ToString());
            T(836, 176, "FLANGES");
            T(821, 195, "3"); T(836, 195, "Weldneck Flange, ASME B16.5"); T(1084, 195, "2"); T(1127, 195, "2");
            // BOM: erection
            T(817, 224, "ERECTION MATERIALS");
            T(817, 244, "PT"); T(836, 244, "COMPONENT DESCRIPTION"); T(1084, 244, "N.S."); T(1122, 244, "QTY"); T(817, 253, "NO"); T(1084, 253, "(INS)");
            T(836, 273, "SUPPORTS");
            T(821, 292, "4"); T(836, 292, "WE04 - WEDGE FOR DIAM 2\" TO 24\""); T(1084, 292, "2"); T(1127, 292, s.Supports.ToString());
            // cut pipe lengths
            T(846, 484, "CUT PIPE LENGTH");
            T(819, 504, "PIECE"); T(855, 504, "CUT"); T(904, 504, "N.S."); T(821, 513, "NO"); T(852, 513, "LENGTH"); T(904, 513, "(INS)");
            T(821, 523, "<1>"); T(852, 523, s.Pipe1); T(908, 523, "2");
            T(821, 532, "<2>"); T(852, 532, "5'0\""); T(908, 532, "2");
            // coordinate labels: the head, and the connection to the next line
            T(193, 111, $"E {s.HeadE}"); T(193, 120, "N 2000\""); T(193, 130, "U 100\"");
            T(193, 292, "CONT. ON"); T(193, 301, s.ContLine); T(193, 311, "E 1120\""); T(193, 320, "N 2000\""); T(193, 330, "U 100\"");
            if (sheet < s.Sheets) { T(66, 136, "CONT. ON"); T(66, 145, $"DRG {sheet + 1}"); }
            if (sheet > 1) { T(66, 160, "CONT. FROM"); T(66, 169, $"DRG {sheet - 1}"); }
            // title block
            var values = new[] { s.TestPress, "H", s.DesignTemp, s.DesignPress, "15.6-273", "10", "NOT PAINTED", "N", "NA", "Two Phase", s.PidNo, "No", s.Class, "No" };
            for (var i = 0; i < Labels.Length; i++) { T(531, 678 + i * 9.3, Labels[i]); T(606, 678 + i * 9.3, values[i]); }
            T(648, 779, "CALC NO :"); T(690, 779, "NA");
            T(970, 676, s.Revision);
            T(1010, 779, s.LineNo);
            T(1122, 805, sheet.ToString()); T(1132, 805, "/"); T(1134, 805, s.Sheets.ToString());
        }
        File.WriteAllBytes(path, b.Build());
    }

    /// <summary>The model that matches the default <see cref="IsoSpec"/>: a pipe with one branch (2 flanges, 2 tees, 2 supports, 10 ft of pipe along E) and the next line starting where this one ends.</summary>
    public static string ModelJson(Action<Dictionary<string, object>>? tweakPipe = null, Func<List<object>, List<object>>? tweakItems = null, IsoSpec? spec = null, bool duplicate = false)
    {
        spec ??= new IsoSpec();
        object Item(string type, string name, string spre, double eMm, string? extra = null)
        {
            var a = new Dictionary<string, string> { ["POS"] = $"E {eMm:0.###}mm N 50800mm U 2540mm" };
            if (spre.Length > 0) a["SPRE"] = spre;
            return new { name, type, attrs = a };
        }
        var items = new List<object>
        {
            Item("FLAN", "FLANGE 1 of BRANCH", "/A100A/FLAN-50", 25500),
            Item("TEE", "TEE 1 of BRANCH", "/A100A/TEE-50-50", 26400),
            Item("ATTA", "ATTACHMENT 1 of BRANCH", "/SUPP/ATTA-1", 26500),
            Item("ATTA", "ATTACHMENT 2 of BRANCH", "/SUPP/ATTA-1", 26600),
            Item("ATTA", "ATTACHMENT 3 of BRANCH", "/A100A/ATTA-9", 26700),   // a class-spec attachment: not a support
            Item("TEE", "TEE 2 of BRANCH", "/A100A/TEE-50-50", 27400),
            Item("FLAN", "FLANGE 2 of BRANCH", "/A100A/FLAN-50", 28348),
        };
        if (tweakItems is not null) items = tweakItems(items);
        var pipeAttrs = new Dictionary<string, object>
        {
            ["NAME"] = "/" + spec.LineNo, ["TYPE"] = "PIPE", ["BORE"] = "50mm", ["TEMP"] = "162.78degC", ["PRES"] = "1378951pascal", ["TPRESS"] = "2068427pascal", ["PSPE"] = "/A100A",
            [":TESTINGMEDIUM"] = "H", [":OPETEMPERATURE"] = "15.6-273", [":OPEPRESSURE"] = "68947.6pascal", [":INSUTYPE"] = "N", [":INSUTHK"] = "NA", [":PAINTSYSTEM"] = "NOT PAINTED",
            [":PHASE"] = "Two Phase", [":P&ID-NO"] = "X-PID-0001-10", [":STRESSCRITICALITY"] = "No", [":PWHT"] = "No", [":CALC-NO"] = "NA",
        };
        tweakPipe?.Invoke(pipeAttrs);
        object Branch(string name, string head, string tail, List<object> its) => new
        {
            name, type = "BRAN", attrs = new Dictionary<string, string> { ["HPOS"] = head, ["TPOS"] = tail, ["TPRESS"] = "2068427pascal", ["PRES"] = "1378951pascal", ["TEMP"] = "162.78degC", ["PSPE"] = "/A100A" }, items = its,
        };
        object Pipe(string path) => new { name = "/" + spec.LineNo, type = "PIPE", attrs = pipeAttrs, path, branches = new[] { Branch("/" + spec.LineNo + "/B1", "E 25400mm N 50800mm U 2540mm", "E 28448mm N 50800mm U 2540mm", items) } };
        var next = new
        {
            name = "/" + spec.ContLine, type = "PIPE", attrs = new Dictionary<string, string> { ["NAME"] = "/" + spec.ContLine, ["TYPE"] = "PIPE", ["BORE"] = "50mm", ["PSPE"] = "/A100A" }, path = "model.nwd > a.nwd > /AREA",
            branches = new[] { Branch("/" + spec.ContLine + "/B1", "E 28448mm N 50800mm U 2540mm", "E 30000mm N 50800mm U 2540mm", []) },
        };
        var lines = new List<object> { Pipe("model.nwd > a.nwd > /AREA"), next };
        if (duplicate) lines.Add(Pipe("model.nwd > a.nwd > /AREA-COPY"));
        return JsonSerializer.Serialize(new { schema = 1, exported = "2026-01-01T10:00:00", source = "x.nwd", totalItems = 100, lines, nozzles = Array.Empty<object>() });
    }
}

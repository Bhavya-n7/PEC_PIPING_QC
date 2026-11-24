using System.Globalization;
using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Model;

/// <summary>Unit reading and conversion. The model is in mm / Pa / degC, the isometrics print feet-inches, psig and degF.</summary>
public static partial class Units
{
    public const double MmPerInch = 25.4;
    public const double PaPerPsi = 6894.757293168;

    /// <summary>DN (mm) to the NPS (inch) text printed on isometrics.</summary>
    public static readonly IReadOnlyDictionary<string, string> DnToNps = new Dictionary<string, string>
    {
        ["8"] = "1/4", ["10"] = "3/8", ["15"] = "1/2", ["20"] = "3/4", ["25"] = "1", ["32"] = "1 1/4", ["40"] = "1 1/2", ["50"] = "2",
        ["65"] = "2 1/2", ["80"] = "3", ["90"] = "3 1/2", ["100"] = "4", ["125"] = "5", ["150"] = "6", ["200"] = "8", ["250"] = "10",
        ["300"] = "12", ["350"] = "14", ["400"] = "16", ["450"] = "18", ["500"] = "20", ["550"] = "22", ["600"] = "24",
    };

    // 15'7 9/16"   8'9 7/16"   2 7/8"   61997 7/16"   5'  7"
    [GeneratedRegex("""^\s*(?:(?<ft>-?\d+)\s*')?\s*(?:(?<in>-?\d+)(?![\d/]))?\s*(?:(?<fn>\d+)\s*/\s*(?<fd>\d+))?\s*"?\s*$""")]
    private static partial Regex FtIn();

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*([A-Za-z]+)?\s*$")]
    private static partial Regex Quantity();

    [GeneratedRegex(@"([ENUWSD])\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*(mm|m|in|ft)?")]
    private static partial Regex Axis();

    /// <summary>15'7 9/16" gives 187.5625 inches; null when the text is not a length.</summary>
    public static double? FeetInchesToInches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace('’', '\'').Replace('”', '"').Replace('″', '"');
        var m = FtIn().Match(t);
        if (!m.Success || !(m.Groups["ft"].Success || m.Groups["in"].Success || m.Groups["fn"].Success)) return null;
        var sign = t.TrimStart().StartsWith('-') ? -1.0 : 1.0;
        double ft = m.Groups["ft"].Success ? Math.Abs(int.Parse(m.Groups["ft"].Value, CultureInfo.InvariantCulture)) : 0;
        double inch = m.Groups["in"].Success ? Math.Abs(int.Parse(m.Groups["in"].Value, CultureInfo.InvariantCulture)) : 0;
        var fd = m.Groups["fd"].Success ? int.Parse(m.Groups["fd"].Value, CultureInfo.InvariantCulture) : 0;
        var frac = m.Groups["fn"].Success && fd != 0 ? int.Parse(m.Groups["fn"].Value, CultureInfo.InvariantCulture) / (double)fd : 0.0;
        return sign * (ft * 12 + inch + frac);
    }

    public static double InToMm(double v) => v * MmPerInch;
    public static double MmToIn(double v) => v / MmPerInch;
    public static double PaToPsi(double pa) => pa / PaPerPsi;
    public static double CToF(double c) => c * 9.0 / 5.0 + 32.0;

    /// <summary>187.5625 gives 15'7 9/16".</summary>
    public static string FormatFeetInches(double inches, int denom = 16)
    {
        var sign = inches < 0 ? "-" : "";
        var total = (long)Math.Round(Math.Abs(inches) * denom, MidpointRounding.AwayFromZero);
        var whole = total / denom; var frac = (int)(total % denom);
        var ft = whole / 12; var inch = whole % 12;
        var f = frac > 0 ? $" {frac / Gcd(frac, denom)}/{denom / Gcd(frac, denom)}" : "";
        return ft > 0 ? $"{sign}{ft}'{inch}{f}\"" : $"{sign}{inch}{f}\"";
    }

    /// <summary>61997.4375 gives 61997 7/16" (the way isometrics print coordinates).</summary>
    public static string FormatInches(double inches, int denom = 16)
    {
        var sign = inches < 0 ? "-" : "";
        var total = (long)Math.Round(Math.Abs(inches) * denom, MidpointRounding.AwayFromZero);
        var whole = total / denom; var frac = (int)(total % denom);
        var f = frac > 0 ? $" {frac / Gcd(frac, denom)}/{denom / Gcd(frac, denom)}" : "";
        return $"{sign}{whole}{f}\"";
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>1378951pascal gives (1378951, "pascal"); 163degC gives (163, "degC"); 50mm gives (50, "mm").</summary>
    public static (double Value, string Unit)? ParseQuantity(string? text)
    {
        if (text is null) return null;
        var m = Quantity().Match(text);
        return m.Success ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value) : null;
    }

    /// <summary>"E 1574734.888mm N 3254362.766mm U 35623.92mm" gives (E, N, U) in mm. W, S and D are read as negative E, N and U.</summary>
    public static (double E, double N, double U)? ParsePosition(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var vals = new Dictionary<char, double>();
        foreach (Match m in Axis().Matches(text))
        {
            var axis = m.Groups[1].Value[0];
            var v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            v *= m.Groups[3].Value switch { "m" => 1000.0, "in" => MmPerInch, "ft" => 12 * MmPerInch, _ => 1.0 };
            if (axis is 'W' or 'S' or 'D') { axis = axis == 'W' ? 'E' : axis == 'S' ? 'N' : 'U'; v = -v; }
            vals[axis] = v;
        }
        return vals.Count == 3 && vals.ContainsKey('E') && vals.ContainsKey('N') && vals.ContainsKey('U') ? (vals['E'], vals['N'], vals['U']) : null;
    }

    public static double Dist((double E, double N, double U) a, (double E, double N, double U) b) =>
        Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N) + (a.U - b.U) * (a.U - b.U));

    /// <summary>"2 x 2" gives ("2"); "3 x 2" gives ("2","3"): unordered, without repeats.</summary>
    public static string[] NpsFromText(string? text) =>
        (text ?? "").Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Replace("\"", "")).Where(p => p.Length > 0)
            .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
}

namespace Pec.Qc.Core.Findings;

/// <summary>The three kinds of QC the team does (from QCTYPES.MD).</summary>
public enum QcType { Isometric, Pid, Ga }

public static class QcTypes
{
    public static string Code(QcType t) => t switch { QcType.Isometric => "iso", QcType.Pid => "pid", _ => "ga" };

    public static string Label(QcType t) => t switch { QcType.Isometric => "Piping isometric check", QcType.Pid => "P&ID check", _ => "GA drawing check" };

    /// <summary>Accepts the codes and the natural names ("iso", "isometric", "p&amp;id", "GA drawing"...). Null when it is not one of the three.</summary>
    public static QcType? Parse(string? text)
    {
        var k = new string((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (k.StartsWith("iso") || k.Contains("isometric")) return QcType.Isometric;
        if (k is "pid" or "pandid" or "pids" || k.StartsWith("pid") || k.StartsWith("pandid")) return QcType.Pid;
        if (k == "ga" || k.StartsWith("ga") && k.Contains("draw") || k.Contains("generalarrangement")) return QcType.Ga;
        return null;
    }
}

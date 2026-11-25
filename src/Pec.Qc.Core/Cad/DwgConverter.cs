using System.Diagnostics;

namespace Pec.Qc.Core.Cad;

/// <summary>Converts a DWG to an ASCII DXF with the console version of AutoCAD (accoreconsole.exe, installed with AutoCAD) on a COPY of the drawing, headless, with a time limit.</summary>
public static class DwgConverter
{
    /// <summary>The newest accoreconsole.exe under Program Files\Autodesk, or null.</summary>
    public static string? FindConsole()
    {
        var env = Environment.GetEnvironmentVariable("PEC_QC_ACCORE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var a = Path.Combine(root, "Autodesk");
            if (!Directory.Exists(a)) continue;
            var hit = Directory.GetDirectories(a, "AutoCAD *").OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "accoreconsole.exe")).FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Returns the path of the DXF written into <paramref name="outDir"/>. Throws a message fit for the user when it cannot be done.</summary>
    public static string ToDxf(string dwg, string outDir, int timeoutSeconds = 180)
    {
        var exe = FindConsole() ?? throw new InvalidOperationException("AutoCAD is not installed on this PC (accoreconsole.exe was not found), so the DWG cannot be read. Ask the user to save it as DXF or PDF, or to run this on a PC with AutoCAD.");
        if (!File.Exists(dwg)) throw new FileNotFoundException("The DWG was not found: " + dwg);
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(Path.GetTempPath(), "pecqc-dwg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var copy = Path.Combine(work, "in.dwg"); File.Copy(dwg, copy, true);                  // never open the user's file
            var dxf = Path.Combine(outDir, Path.GetFileNameWithoutExtension(dwg) + ".dxf");
            if (File.Exists(dxf)) File.Delete(dxf);
            var scr = Path.Combine(work, "run.scr");
            File.WriteAllText(scr, "FILEDIA 0\r\n_.DXFOUT \"" + dxf + "\" 16\r\n_.QUIT _Y\r\n");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("/i"); psi.ArgumentList.Add(copy); psi.ArgumentList.Add("/s"); psi.ArgumentList.Add(scr); psi.ArgumentList.Add("/l"); psi.ArgumentList.Add("en-US");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("AutoCAD's console did not start.");
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException($"AutoCAD did not finish within {timeoutSeconds} seconds and was stopped."); }
            if (!File.Exists(dxf) || new FileInfo(dxf).Length < 100)
                throw new InvalidOperationException("AutoCAD did not write the DXF. " + Tail(so.GetAwaiter().GetResult()));
            return dxf;
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    private static string Tail(string s) { s = s.Trim(); return s.Length > 400 ? "..." + s[^400..] : s; }
}

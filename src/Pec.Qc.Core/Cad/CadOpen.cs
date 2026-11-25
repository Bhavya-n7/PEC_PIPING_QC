namespace Pec.Qc.Core.Cad;

public sealed record CadOpened(DxfDrawing Drawing, string How);

/// <summary>Opens a DXF or DWG as data. A DWG is read by the reader built into the product; AutoCAD's console is used only when that reader cannot understand the file and AutoCAD is installed.</summary>
public static class CadOpen
{
    public static CadOpened Open(string path)
    {
        if (!Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return new CadOpened(DxfReader.Read(path), "DXF");
        Exception native;
        try { return new CadOpened(DwgReaderNative.Read(path), "DWG (built-in reader)"); }
        catch (Exception e) when (e is not OutOfMemoryException) { native = e; }
        if (DwgConverter.FindConsole() is null)
            throw new InvalidOperationException($"This DWG could not be read by the built-in reader ({native.Message}) and AutoCAD is not installed here to convert it. Ask the user to save it as DXF or PDF.");
        return new CadOpened(DxfReader.Read(DwgConverter.ToDxf(path, Path.Combine(Settings.DataDirectory, "cad-cache"))), "DWG (through AutoCAD, the built-in reader failed: " + native.Message + ")");
    }
}

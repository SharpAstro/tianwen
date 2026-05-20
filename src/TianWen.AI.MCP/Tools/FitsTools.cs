using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using TianWen.Lib.Imaging;

namespace TianWen.AI.MCP.Tools;

/// <summary>
/// FITS file inspection tools. Phase A surfaces a single header-summary tool;
/// later phases will add per-channel stats, star detection, plate-solve,
/// pixel sampling, and PNG render.
/// </summary>
[McpServerToolType]
public class FitsTools
{
    [McpServerTool, Description("Read FITS header summary (dimensions, DATE-OBS, exposure, instrument, telescope, gain, temperature, target, etc.) as a multi-line text block. Accepts plain or .gz-compressed FITS files.")]
    public static string Header(
        [Description("Absolute path to a FITS file (.fits or .fits.gz).")] string path)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";
        if (!Image.TryReadFitsHeader(path, out var info)) return $"ERROR: not a FITS file: {path}";
        return info.ToString();
    }
}

using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.AI.MCP.Tools;

/// <summary>
/// FITS file inspection tools. Phase A: <see cref="Header"/>. Phase B adds
/// per-channel stats, star detection, plate-solve, and pixel sampling --
/// all thin shims over existing TianWen.Lib APIs.
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

    [McpServerTool, Description("Per-channel pixel statistics (mean, median, MAD, pedestal, threshold). Pass channel=-1 (default) for all channels. NOTE: loads the full image into memory; ~120 MB for a 3008x3008 RGB float32 frame.")]
    public static string Stats(
        [Description("Absolute path to a FITS file.")] string path,
        [Description("Channel index. -1 (default) = all channels.")] int channel = -1)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";
        if (!Image.TryReadFitsFile(path, out var image)) return $"ERROR: not a FITS file: {path}";

        var sb = new StringBuilder(512);
        sb.AppendLine($"File:        {path}");
        sb.AppendLine($"Shape:       {image.ChannelCount}x{image.Height}x{image.Width}");
        sb.AppendLine($"Range:       [{image.MinValue}, {image.MaxValue}]");
        var first = channel < 0 ? 0 : channel;
        var last = channel < 0 ? image.ChannelCount - 1 : channel;
        for (var c = first; c <= last && c < image.ChannelCount; c++)
        {
            var h = image.Statistics(c);
            sb.AppendLine($"[ch {c}] mean={h.Mean:G6} median={h.Median:G6} mad={h.MAD:G6}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Detect stars in the image via TianWen's FindStarsAsync (channel 0). Returns one ImagedStar record per line (HFD/FWHM/SNR/Flux/XCentroid/YCentroid/Ellipticity).")]
    public static async Task<string> FindStars(
        [Description("Absolute path to a FITS file.")] string path,
        [Description("Minimum SNR floor for star detection (default 20).")] float snrMin = 20f,
        [Description("Maximum stars to return (default 500).")] int maxStars = 500,
        CancellationToken ct = default)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";
        if (!Image.TryReadFitsFile(path, out var image)) return $"ERROR: not a FITS file: {path}";

        var stars = await image.FindStarsAsync(channel: 0, snrMin: snrMin, maxStars: maxStars, cancellationToken: ct);
        var sb = new StringBuilder(64 + 64 * stars.Count);
        sb.AppendLine($"Detected {stars.Count} stars (snrMin={snrMin}, maxStars={maxStars}):");
        foreach (var s in stars) sb.AppendLine(s.ToString());
        return sb.ToString();
    }

    [McpServerTool, Description("Plate-solve a FITS file via the registered IPlateSolverFactory (priority: CatalogPlateSolver, ASTAP, Astrometry.net). CatalogPlateSolver does NOT support blind solving -- supply a search hint via hintRa+hintDec. Returns the WCS solution + match counts.")]
    public static async Task<string> PlateSolve(
        IPlateSolverFactory factory,
        [Description("Absolute path to a FITS file.")] string path,
        [Description("Search-origin RA hint in hours. Optional but strongly recommended.")] double? hintRa = null,
        [Description("Search-origin Dec hint in degrees. Required when hintRa is set.")] double? hintDec = null,
        [Description("Search radius in degrees around the hint. Default 5.")] double searchRadius = 5.0,
        [Description("Pixel scale arcsec/px. Default null = read from FITS PIXSIZE+FOCALLEN headers.")] double? pixelScale = null,
        [Description("Scale tolerance fraction passed to the solver. Default 0.03 (+/- 3%).")] float range = 0.03f,
        CancellationToken ct = default)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";

        WCS? hint = (hintRa is { } ra && hintDec is { } dec) ? new WCS(ra, dec) : null;
        ImageDim? dim = pixelScale is { } scale && Image.TryReadFitsHeader(path, out var info)
            ? new ImageDim((float)scale, info.Width, info.Height)
            : null;

        var result = await factory.SolveFileAsync(path, dim, range, hint, hint is null ? null : searchRadius, ct);
        return result.ToString();
    }

    [McpServerTool, Description("Read pixel values at specified (x,y) coordinates. Points encoded as 'x1,y1;x2,y2;...'. Returns one line per point: 'x,y -> [ch0, ch1, ch2]'.")]
    public static string Pixels(
        [Description("Absolute path to a FITS file.")] string path,
        [Description("Semicolon-separated list of 'x,y' integer pixel coordinates (e.g. '100,200;512,512').")] string points)
    {
        if (!File.Exists(path)) return $"ERROR: file not found: {path}";
        if (!Image.TryReadFitsFile(path, out var image)) return $"ERROR: not a FITS file: {path}";

        var sb = new StringBuilder(256);
        var w = image.Width;
        var h = image.Height;
        var chans = image.ChannelCount;
        var spans = Enumerable.Range(0, chans).Select(c => image.GetChannelSpan(c).ToArray()).ToArray();

        foreach (var p in points.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = p.Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
            {
                sb.AppendLine($"{p} -> ERROR: not 'x,y'");
                continue;
            }
            if ((uint)x >= w || (uint)y >= h)
            {
                sb.AppendLine($"{x},{y} -> ERROR: out of bounds (image is {w}x{h})");
                continue;
            }
            var i = y * w + x;
            sb.Append($"{x},{y} -> [");
            for (var c = 0; c < chans; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append(spans[c][i].ToString("G6"));
            }
            sb.AppendLine("]");
        }
        return sb.ToString();
    }
}

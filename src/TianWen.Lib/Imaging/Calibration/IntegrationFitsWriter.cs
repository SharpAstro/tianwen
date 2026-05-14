using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Writes an <see cref="IntegrationResult"/> to disk as two FITS files: a
/// master image at the caller-supplied path, and the per-pixel rejection-
/// fraction map at the same path with the <c>.rejection.fits</c> suffix
/// appended. Stack-specific provenance lands on the master's headers
/// (<c>STACK_N</c>, <c>REJ_RATE</c>, <c>REJ_TOT</c>) so downstream consumers
/// (the FITS viewer, third-party tools) can identify a stacked frame.
/// </summary>
/// <remarks>
/// Two files rather than a multi-extension FITS (MEF) because: simpler code
/// (no FITS.Lib internal reflection), every FITS viewer opens both files
/// natively (most won't show the second HDU of an MEF), and the rejection
/// map is genuinely a separate artifact most users don't need to look at.
/// MEF is the standard PixInsight / SetiAstro format; we may revisit when
/// Phase 10's memory-mapped sink lands and MEF becomes natural.
/// </remarks>
public static class IntegrationFitsWriter
{
    /// <summary>Suffix appended to the master path for the rejection map FITS.</summary>
    public const string RejectionMapSuffix = ".rejection.fits";

    /// <summary>
    /// Writes <paramref name="result"/> to <paramref name="masterPath"/>
    /// (the master master image) plus a sibling <c>.rejection.fits</c> file
    /// for the rejection map. The rejection map is skipped when no
    /// rejection actually occurred (<see cref="IntegrationResult.TotalRejections"/>
    /// == 0) to avoid littering disk with all-zero maps.
    /// </summary>
    /// <param name="masterPath">Output path for the master image. Must end
    /// with <c>.fits</c> or <c>.fit</c>.</param>
    /// <param name="result">The integration output to persist.</param>
    /// <param name="wcs">Optional WCS to embed in the master's header.
    /// The rejection map inherits no WCS (it's a per-pixel statistic,
    /// not a sky image).</param>
    public static void Write(string masterPath, IntegrationResult result, WCS? wcs = null)
    {
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            ["STACK_N"] = (result.FrameCount, "Number of frames combined into this master"),
            ["REJ_TOT"] = ((long)result.TotalRejections, "Total per-pixel rejections across the stack"),
            ["REJ_RATE"] = (result.MeanRejectionRate, "Mean rejection rate (rejections / (frames * pixels * channels))"),
            ["SWCREATE"] = ("TianWen.Imaging.Calibration.Integrator", "Software that created the master"),
        };

        result.Master.WriteToFitsFile(masterPath, wcs, extras);

        if (result.TotalRejections > 0)
        {
            var rejectionPath = RejectionPathFor(masterPath);
            var rejExtras = new Dictionary<string, (object Value, string Comment)>
            {
                ["STACK_N"] = (result.FrameCount, "Frames the rejection map was computed against"),
                ["REJ_RATE"] = (result.MeanRejectionRate, "Mean rejection rate (this map's average)"),
                ["SWCREATE"] = ("TianWen.Imaging.Calibration.Integrator", "Software that created this rejection map"),
                ["IMAGETYP"] = ("REJECTION", "Per-pixel rejection-fraction map [0, 1]"),
            };
            result.RejectionMap.WriteToFitsFile(rejectionPath, wcs: null, rejExtras);
        }
    }

    /// <summary>Computes the rejection-map sibling path for a given master path.</summary>
    public static string RejectionPathFor(string masterPath)
    {
        // strip trailing .fits / .fit (case-insensitive), then append .rejection.fits
        var dir = Path.GetDirectoryName(masterPath);
        var stem = Path.GetFileNameWithoutExtension(masterPath);
        var combined = string.IsNullOrEmpty(dir) ? stem : Path.Combine(dir, stem);
        return combined + RejectionMapSuffix;
    }
}

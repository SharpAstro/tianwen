using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Stacking;

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

    /// <summary>Value stamped into the FITS <c>SWCREATE</c> header of every
    /// master + rejection map this writer produces. Used by
    /// <see cref="IsTianWenMaster(string)"/> to discriminate our own outputs
    /// from arbitrary FITS files a user may have parked in the output dir.</summary>
    public const string SoftwareCreator = "TianWen.Imaging.Stacking.Integrator";

    /// <summary>Prefix used to recognise <see cref="SoftwareCreator"/> values
    /// across versions (older masters were stamped
    /// <c>TianWen.Imaging.Calibration.Integrator</c> before the namespace
    /// split -- both share this prefix).</summary>
    private const string SoftwareCreatorPrefix = "TianWen.";

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
    /// <param name="strategy">Which <see cref="IIntegrationStrategy"/>
    /// produced this master -- stamped into the <c>STRATEGY</c> FITS
    /// header so downstream tools can tell a drizzle master from an
    /// AHD+stack master without having to read pixel data. Null is
    /// allowed for non-pipeline callers (tests, manual workflows) that
    /// don't have a strategy kind handy.</param>
    public static void Write(string masterPath, IntegrationResult result, WCS? wcs = null, IntegrationStrategyKind? strategy = null)
    {
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            ["STACK_N"] = (result.FrameCount, "Number of frames combined into this master"),
            ["REJ_TOT"] = ((long)result.TotalRejections, "Total per-pixel rejections across the stack"),
            ["REJ_RATE"] = (result.MeanRejectionRate, "Mean rejection rate (rejections / (frames * pixels * channels))"),
            ["SWCREATE"] = (SoftwareCreator, "Software that created the master"),
        };
        if (strategy is { } s)
        {
            extras["STRATEGY"] = (s.ToString(), "Integration strategy used (IntegrationStrategyKind)");
        }

        result.Master.WriteToFitsFile(masterPath, wcs, extras);

        if (result.TotalRejections > 0)
        {
            var rejectionPath = RejectionPathFor(masterPath);
            var rejExtras = new Dictionary<string, (object Value, string Comment)>
            {
                ["STACK_N"] = (result.FrameCount, "Frames the rejection map was computed against"),
                ["REJ_RATE"] = (result.MeanRejectionRate, "Mean rejection rate (this map's average)"),
                ["SWCREATE"] = (SoftwareCreator, "Software that created this rejection map"),
                ["IMAGETYP"] = ("REJECTION", "Per-pixel rejection-fraction map [0, 1]"),
            };
            if (strategy is { } s2)
            {
                rejExtras["STRATEGY"] = (s2.ToString(), "Integration strategy used (IntegrationStrategyKind)");
            }
            result.RejectionMap.WriteToFitsFile(rejectionPath, wcs: null, rejExtras);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is a FITS file whose
    /// <c>SWCREATE</c> header was stamped by this writer (any TianWen
    /// stacking master / rejection map). Used to safely wipe stale outputs
    /// at the start of a run without touching unrelated FITS files that
    /// share the output directory. Header-only read -- no pixel data.
    /// Returns false for any read failure (missing file, corrupt header,
    /// not a FITS file, no SWCREATE).
    /// </summary>
    public static bool IsTianWenMaster(string path)
    {
        try
        {
            using var bufferedReader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
            using var fitsFile = new Fits(bufferedReader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
            var hdu = fitsFile.ReadHDUHeaderOnly();
            var swcreate = hdu?.Header?.GetStringValue("SWCREATE");
            return swcreate?.StartsWith(SoftwareCreatorPrefix, StringComparison.Ordinal) == true;
        }
        catch
        {
            return false;
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

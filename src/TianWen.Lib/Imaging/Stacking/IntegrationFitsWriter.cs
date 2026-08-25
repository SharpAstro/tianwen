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
    /// <param name="modifiedBy">Stamped into <c>SWMODIFY</c> (MaxIm DL's
    /// "software that modified the image" card) on the MASTER only, for
    /// callers writing an image something changed after integration -- the
    /// enhance path passes <c>SharpenPipeline.SoftwareModifier</c> when it
    /// re-writes the sharpened master through this writer. The rejection map
    /// never carries it: the map is the ORIGINAL integration statistic,
    /// re-written verbatim beside the modified pixels. Null (the default)
    /// writes no card.</param>
    public static void Write(string masterPath, IntegrationResult result, WCS? wcs = null, IntegrationStrategyKind? strategy = null, string? modifiedBy = null)
    {
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            ["STACK_N"] = (result.FrameCount, "Number of frames combined into this master"),
            // The SBFITSEXT spelling of the same fact, for readers that know that vocabulary
            // (MaxIm DL, TheSkyX). Beside STACK_N rather than instead of it: our own scan and
            // FrameInfo keep reading STACK_N first, and capture software in the wild writes
            // SNAPSHOT=1 on RAW subs, so the count alone must never be the product marker.
            ["SNAPSHOT"] = (result.FrameCount, "Number of images combined (SBFITSEXT)"),
            ["REJ_TOT"] = ((long)result.TotalRejections, "Total per-pixel rejections across the stack"),
            ["REJ_RATE"] = (result.MeanRejectionRate, "Mean rejection rate (rejections / (frames * pixels * channels))"),
            ["SWCREATE"] = (SoftwareCreator, "Software that created the master"),
        };
        if (strategy is { } s)
        {
            extras["STRATEGY"] = (s.ToString(), "Integration strategy used (IntegrationStrategyKind)");
        }
        if (modifiedBy is not null)
        {
            extras["SWMODIFY"] = (modifiedBy, "Software that modified this image");
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
    /// Returns true when a <c>SWCREATE</c> header value marks a TianWen-produced
    /// image -- a stacking master, rejection map, or any DERIVED product (an AI
    /// sharpen / enhance output inherits the master's <c>SWCREATE</c>). TianWen
    /// never writes raw light subs to disk, so any TianWen-stamped FITS is a
    /// processed output, never a fresh light. The scanner uses this to keep
    /// processed outputs parked alongside the lights from being re-ingested as
    /// frames -- once sharpened they carry no <c>STACK_N</c> and an
    /// <c>IMAGETYP=Light</c> copied from the original subs, so the STACK_N
    /// filter alone misses them.
    /// </summary>
    public static bool IsTianWenProduct(string? swcreate)
        => swcreate?.StartsWith(SoftwareCreatorPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// True when EITHER card marks the file as ours. <c>SWMODIFY</c> is not a nicety here: a derived
    /// file INHERITS its source's <c>SWCREATE</c>, so an <c>image sharpen</c> of a N.I.N.A. sub keeps
    /// <c>SWCREATE='N.I.N.A. ...'</c> and carries no <c>STACK_N</c> either -- invisible to both of the
    /// older checks, and re-ingested as a fresh light.
    ///
    /// <para>That is not hypothetical. Removing stars from 135 lights, which is how a comet layer is
    /// built, drops 135 such files beside the originals; the next scan of that folder stacks 270
    /// frames and calls it 135. <see cref="Enhancement.SharpenPipeline.SoftwareModifier"/> was written
    /// precisely so this guard could exist, and deliberately shares the <c>TianWen.</c> prefix.</para>
    /// </summary>
    public static bool IsTianWenProduct(string? swcreate, string? swmodify)
        => IsTianWenProduct(swcreate) || IsTianWenProduct(swmodify);

    // Provenance signals are NOT equally strong, and it is worth knowing which one caught a file.
    //
    // STRONG -- a property of the DATA: STACK_N / NUMFRAME say "this image is an integration of N
    // frames", which is true regardless of who wrote it. That is what excludes Astro Pixel Processor
    // integrations, and it has to be: APP behaves exactly like our own enhance layer and PRESERVES the
    // capture software's SWCREATE, so on this dataset its 10 integrations are indistinguishable from
    // the 506 raw subs by authorship alone. The count card is the only thing between them.
    //
    // WEAK -- a property of AUTHORSHIP: a TianWen SWCREATE or SWMODIFY. Needed anyway, because a
    // star-removed light is a SINGLE frame and has no count card, so nothing about its data says it is
    // derived. Prefer the strong signal wherever a file has one.
    //
    // AND SWMODIFY IS OVERLOADED HERE, WHICH IS A KNOWN WEAKNESS RATHER THAN A DESIGN. Its correct
    // meaning is "our software modified someone else's file" -- which FitsHeaderEditor header-tagging
    // also is: `dataset tag-filter` and friends amended 525 frames of the 10P set. What the scan
    // actually needs is the narrower "we produced these PIXELS, do not re-ingest". The two only fail
    // to collide because header surgery writes no SW* card at all, which is a convention and not a
    // guarantee: the day tagging stamps SWMODIFY honestly, every frame it touched drops out of its own
    // stack. A dedicated "derived pixel product" card would make this a fact instead of an accident.
    //
    // Nor is authorship sufficient on its own. An APP channel composite
    // (Sag_Triplet_OIII-HOO_1.fits) carries NO provenance card whatsoever -- no SWCREATE, no
    // SWMODIFY, no count card, no IMAGETYP, EXPTIME=0 -- and is identifiable only by having three
    // image planes where a raw OSC sub is a 1-channel mosaic. See docs/known-limitations.md.

    /// <summary>
    /// Returns true when <paramref name="path"/> is a FITS file whose
    /// <c>SWCREATE</c> header was stamped by this writer (any TianWen
    /// stacking master / rejection map / derived product). Used to safely
    /// wipe stale outputs at the start of a run without touching unrelated
    /// FITS files that share the output directory. Header-only read -- no
    /// pixel data. Returns false for any read failure (missing file, corrupt
    /// header, not a FITS file, no SWCREATE).
    /// </summary>
    public static bool IsTianWenMaster(string path)
    {
        try
        {
            using var bufferedReader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
            using var fitsFile = new Fits(bufferedReader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
            var hdu = fitsFile.ReadFirstImageHduHeaderOnly();
            return IsTianWenProduct(hdu?.Header?.GetStringValue("SWCREATE"));
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

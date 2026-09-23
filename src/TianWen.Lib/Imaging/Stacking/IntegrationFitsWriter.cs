using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Numerics;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Calibration;

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

    /// <summary>Header card naming which kind of map a <c>.rejection.fits</c> sidecar holds.</summary>
    /// <remarks>
    /// Absent on every sidecar written before 2026-09-08, and its absence must never be read as either
    /// kind: a reader that wants coverage has to see <see cref="CoverageMapKind"/> stated. An older
    /// drizzle sidecar IS coverage and simply cannot say so, which costs its master the exact crop and
    /// leaves it the estimated one.
    /// </remarks>
    public const string MapKindCard = "MAPKIND";

    /// <summary>Accumulated per-pixel weight: high means well covered. What drizzle emits.</summary>
    public const string CoverageMapKind = "COVERAGE";

    /// <summary>Per-pixel rejected/total: high means heavily rejected. What kappa-sigma emits.</summary>
    public const string RejectionMapKind = "REJECTION";

    /// <summary>Suffix of the coverage sidecar a non-drizzle master carries BESIDE its rejection map,
    /// on the master's stem like <see cref="RejectionMapSuffix"/>. A second <c>.fits</c> in the same
    /// folder, so anything enumerating masters asks <see cref="IsMapSidecarPath"/> about it too.</summary>
    public const string CoverageMapSuffix = ".coverage.fits";

    /// <summary>Suffix of the bad pixel map sidecar: the mask the integration actually applied, kept
    /// rather than recomputed and thrown away.</summary>
    /// <remarks>
    /// <b>It is on the SENSOR's geometry, not the master's</b>, being built from the calibration dark
    /// and the registration residuals before anything is warped onto a canvas. So it sits beside the
    /// master as provenance, not as an overlay: nothing may assume it aligns with the master's pixels,
    /// which is why it carries its own dimensions and <c>INSTRUME</c>.
    /// </remarks>
    public const string BadPixelMapSuffix = ".badpixels.fits";

    /// <summary>Per-pixel "do not trust this photosite": what a bad pixel map holds.</summary>
    public const string BadPixelMapKind = "BADPIXEL";

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
    /// <param name="badPixelMask">The photosites this integration refused to use, one
    /// <see cref="BitMatrix"/> per channel, written beside the master as
    /// <see cref="BadPixelMapSuffix"/>. Null writes none, which is what a run with no matched dark
    /// produces. Note it is on the SENSOR's geometry, not the master's canvas.</param>
    public static void Write(string masterPath, IntegrationResult result, WCS? wcs = null, IntegrationStrategyKind? strategy = null, string? modifiedBy = null, AlignmentProvenance? alignment = null, BitMatrix[]? badPixelMask = null)
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
        // ALIGNMNT is written ALWAYS, including for an ordinary star-aligned stack. Absence would
        // otherwise conflate "sidereal" with "written before this card existed", and every master on
        // disk today is in the second group -- the same ambiguity that let a missing FILTER read as
        // "no filter in the light path". FITS keywords cap at 8 characters, which is why this is not
        // spelled ALIGNMENT.
        var basis = alignment?.Basis ?? AlignmentProvenance.Sidereal.Basis;
        extras["ALIGNMNT"] = (basis, "Registration basis (Sidereal | Comet)");
        if (alignment is { } al && al.DriftPxPerHour is { } drift)
        {
            // The drift is what makes the alignment reproducible without re-querying an ephemeris,
            // and CANVAS px/hr is the basis it was applied in: the star solution has already absorbed
            // dither and field rotation by the time it acts, so a sky rate would not round-trip.
            extras["DRIFTX"] = ((double)drift.X, "Target drift, canvas px/hr (X)");
            extras["DRIFTY"] = ((double)drift.Y, "Target drift, canvas px/hr (Y)");
            if (al.RateSource is { Length: > 0 } src)
            {
                extras["DRIFTSRC"] = (src, "How the drift was obtained (Horizons | Manual)");
            }
            if (al.TargetBody is { Length: > 0 } body)
            {
                extras["TRACKOBJ"] = (body, "Body the stack was registered on");
            }
        }
        if (alignment is { CanvasOriginX: { } originX, CanvasOriginY: { } originY })
        {
            // Two masters from one reference overlay by the difference of these; the canvas is the
            // union of each run's frame footprints, so neither its extent nor its origin is shared.
            extras["CANVASX0"] = (originX, "Pixel (0,0) of this image in reference-frame px, x");
            extras["CANVASY0"] = (originY, "Pixel (0,0) of this image in reference-frame px, y");
        }
        if (alignment?.ReferenceFrame is { Length: > 0 } reference)
        {
            extras["REFFRAME"] = (reference, "Reference frame whose pixel space CANVASX0/Y0 are in");
        }
        if (modifiedBy is not null)
        {
            extras["SWMODIFY"] = (modifiedBy, "Software that modified this image");
        }

        result.Master.WriteToFitsFile(masterPath, wcs, extras);

        if (result.TotalRejections > 0 && result.RejectionMap is { } rejection)
        {
            WriteRejectionMap(masterPath, rejection, result.FrameCount, result.MeanRejectionRate, strategy);
        }
        if (result.Coverage is { } coverage)
        {
            WriteCoverageMap(masterPath, coverage, result.FrameCount, strategy);
        }
        WriteBadPixelMap(masterPath, badPixelMask, result.Master.ImageMeta, result.FrameCount);
    }

    /// <summary>
    /// Writes the per-pixel coverage plane beside a master whose first sidecar is a rejection
    /// fraction, at the master's path plus <see cref="CoverageMapSuffix"/>.
    /// </summary>
    /// <remarks>
    /// A second file rather than a change of meaning for the first: the drizzle strategies put their
    /// weight in the <c>.rejection.fits</c> slot and say so with <c>MAPKIND</c>, and every other
    /// strategy's rejection fraction stays where its readers and tests expect it. What those masters
    /// lacked was a plane the exact crop tier could use at all; without one every consumer fell to
    /// <see cref="CoverageEdgeWalk"/>, which declines an edge whose band never settles, and a 350 px
    /// dither strip of 2.7x noise reached the gallery on the V1045 Ori master with 264 px trimmed.
    /// </remarks>
    public static void WriteCoverageMap(string masterPath, Image coverage, int frameCount, IntegrationStrategyKind? strategy = null)
    {
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            ["STACK_N"] = (frameCount, "Frames the coverage was counted over"),
            ["SWCREATE"] = (SoftwareCreator, "Software that created this coverage map"),
            ["IMAGETYP"] = ("COVERAGE", "Per-pixel count of frames with a finite sample"),
            [MapKindCard] = (CoverageMapKind, "Frames with a finite sample per pixel; high is well covered"),
        };
        if (strategy is { } s)
        {
            extras["STRATEGY"] = (s.ToString(), "Integration strategy used (IntegrationStrategyKind)");
        }
        WriteMapSidecar(CoveragePathFor(masterPath), coverage, extras);
    }

    /// <summary>
    /// How a per-pixel map goes to disk, for every map this writer produces: narrowed to an integer
    /// container scaled to the values it actually holds, then gzipped.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves are needed and the order matters.</b> A map is held as float32 because
    /// that is what a plane is, and float32 is what makes it both large and incompressible: measured
    /// on one real 3072x3060x3 coverage map of 81 frames, the file is 112.8 MB and gzip alone takes
    /// it to 94.7 MB, a ratio of 1.2, because the low mantissa bits of a weight are noise. Quantised
    /// to 16 bits first the same map is 56.4 MB and gzips to 2.09 MB; at 8 bits, 28.2 MB gzipping to
    /// 1.71 MB. <b>The compression comes from the quantisation.</b> A store of 139 such masters was
    /// therefore carrying several GB of mantissa noise.</para>
    ///
    /// <para><b>What the narrowing costs.</b> A map whose samples are already whole numbers and fit
    /// keeps unit steps, so a coverage COUNT is stored as that count exactly and reads back exactly,
    /// in any tool, with no scale to believe. Anything else (a drizzle's accumulated weight, a
    /// rejection fraction) spreads its own range over the container through <c>BSCALE</c>, so the
    /// step is the smallest the data allow rather than a fixed one: 65535 levels of whatever the map
    /// holds, against the 0.95-of-median comparison the crop tier makes of it.</para>
    ///
    /// <para>Public because the storage decision is worth one home: the dataset bake writes its own
    /// masters and reaches the same rule through <see cref="WriteCoverageMap"/> and
    /// <see cref="WriteRejectionMap"/> rather than restating it.</para>
    /// </remarks>
    public static FitsSampleStorage MapStorage(Image map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var wholeNumbers = true;
        var observedMax = 0.0;
        for (var c = 0; c < map.ChannelCount; c++)
        {
            var plane = map.GetChannelSpan(c);
            for (var i = 0; i < plane.Length; i++)
            {
                var v = plane[i];
                if (!float.IsFinite(v))
                {
                    continue;
                }

                if (v > observedMax)
                {
                    observedMax = v;
                }

                if (wholeNumbers && v != MathF.Round(v))
                {
                    wholeNumbers = false;
                }
            }
        }

        // The OBSERVED peak, not the declared one: a coverage plane is labelled with the frame count
        // while a drizzle's weights top out below it, and scaling to what is actually there buys the
        // finer step. Nothing is lost, since no sample exceeds it by construction.
        var depth = wholeNumbers && observedMax <= byte.MaxValue ? BitDepth.Int8 : BitDepth.Int16;
        return FitsSampleStorage.Spanning(depth, observedMax, wholeNumbers);
    }

    /// <summary>Writes one map sidecar at its logical path, in the storage
    /// <see cref="MapStorage"/> chooses and gzipped.</summary>
    private static void WriteMapSidecar(
        string logicalPath,
        Image map,
        Dictionary<string, (object Value, string Comment)> extras)
        => map.WriteToFitsFile(CompressedPathFor(logicalPath), wcs: null, extras, MapStorage(map));

    /// <summary>
    /// Writes the per-pixel map beside a master, at the master's path plus
    /// <see cref="RejectionMapSuffix"/>.
    /// </summary>
    /// <remarks>
    /// Its own method because the stacker is no longer the only producer of masters worth cropping:
    /// a dataset bake retains one per session, and without this sidecar every consumer of those is
    /// pushed onto <see cref="CoverageEdgeWalk"/>, which ESTIMATES where coverage ends from the noise
    /// profile. That estimate refuses an edge whose band never settles, and the ramp it then keeps is
    /// what a background model fits: measured on the QHY294C SMC master, the kept band renders as a
    /// 7-level stripe after flattening. A coverage plane states the answer outright, so it is worth
    /// the one extra file per master.
    /// </remarks>
    public static void WriteRejectionMap(
        string masterPath,
        Image map,
        int frameCount,
        double meanRejectionRate,
        IntegrationStrategyKind? strategy = null)
    {
        var rejectionPath = RejectionPathFor(masterPath);
        // MAPKIND, not IMAGETYP, says which of the two maps this is: the drizzle strategies put the
        // accumulated per-pixel WEIGHT here rather than a rejection fraction, and the two are
        // opposite in sense and different in range. IMAGETYP stays REJECTION for both, because
        // third-party readers key on it and the file is a per-pixel diagnostic map either way.
        var rejExtras = new Dictionary<string, (object Value, string Comment)>
        {
            ["STACK_N"] = (frameCount, "Frames the rejection map was computed against"),
            ["REJ_RATE"] = (meanRejectionRate, "Mean rejection rate (this map's average)"),
            ["SWCREATE"] = (SoftwareCreator, "Software that created this rejection map"),
            ["IMAGETYP"] = ("REJECTION", "Per-pixel rejection-fraction map [0, 1]"),
            ["MAPKIND"] = (RejectionMapKind, "Per-pixel rejected/total; high is heavily rejected"),
        };
        if (strategy is { } s2)
        {
            rejExtras["STRATEGY"] = (s2.ToString(), "Integration strategy used (IntegrationStrategyKind)");
        }
        WriteMapSidecar(rejectionPath, map, rejExtras);
    }

    /// <summary>
    /// Writes the bad pixel mask the integration applied, beside the master, at the master's path
    /// plus <see cref="BadPixelMapSuffix"/>. No-op for a null or empty mask, which is what a run
    /// with no matched dark and no usable registration residual produces.
    /// </summary>
    /// <remarks>
    /// <para><b>Why keep it at all.</b> The mask is computed on every run and was then discarded, so
    /// the one artefact that explains a master's defect handling did not exist. The incident that
    /// makes the case: an EVEN sampling stride phase-locked to the CFA, 100% of blue was flagged
    /// hot, and the master was written with an all-NaN blue plane while the session reported
    /// success. <c>BadPixelDetection.DefaultMaxMaskedFraction</c> is what stops that now; a written
    /// map is what would have SHOWN it, at a glance, before anyone read a log.</para>
    ///
    /// <para><b>And why keep one per master rather than one per camera.</b> A sensor's defect
    /// population grows and moves over years, so the interesting object is the SERIES, not the
    /// latest. One beside each master is that series for free, dated and instrumented by the
    /// master's own header, at a cost the compression makes negligible. The archive's own APP maps
    /// are the same thing done by hand, one per processing run.</para>
    /// </remarks>
    public static void WriteBadPixelMap(
        string masterPath,
        BitMatrix[]? mask,
        in ImageMeta meta,
        int frameCount,
        string? source = null)
    {
        if (mask is not { Length: > 0 })
        {
            return;
        }

        var flagged = BadPixelDetection.CountMaskedPixels(mask);
        var pixels = (long)mask.Length * mask[0].Rows * mask[0].Columns;
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            // APP's own card names where APP has one, so its maps and ours read the same way.
            ["CALFRAME"] = ("BadPixelMap", "bad pixel map for instrument " + (meta.Instrument ?? "unknown")),
            ["NPIX"] = (pixels, "raw number of pixels"),
            ["NBADPIX"] = (flagged, "number of bad pixels"),
            ["PBADPIX"] = (pixels > 0 ? flagged * 100.0 / pixels : 0.0, "percentage of bad pixels"),
            ["SWCREATE"] = (SoftwareCreator, "Software that created this bad pixel map"),
            ["IMAGETYP"] = ("BADPIXEL", "Per-pixel mask: 127 trusted, 255 flagged"),
            [MapKindCard] = (BadPixelMapKind, "Photosites the integration refused to use"),
        };
        if (frameCount > 0)
        {
            extras["STACK_N"] = (frameCount, "Frames the master beside this map was built from");
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            extras["MASKSRC"] = (source, "Which detector(s) produced this mask");
        }

        WriteMapSidecar(BadPixelPathFor(masterPath), BadPixelMap.ToImage(mask, meta), extras);
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
            using var fitsFile = Image.OpenFitsHeader(path);
            var hdu = fitsFile.ReadFirstImageHduHeaderOnly();
            return IsTianWenProduct(hdu?.Header?.GetStringValue("SWCREATE"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the coverage plane that sits beside <paramref name="masterPath"/>, when there is one and it
    /// says it is coverage. This is what lets a consumer read a master's exact covered area instead of
    /// estimating it -- see <see cref="Image.LargestCoveredRectangle(Image, double, int)"/> against
    /// <see cref="CoverageEdgeWalk"/>.
    /// </summary>
    /// <remarks>
    /// The header is read on its own first, so a sidecar that turns out to be a rejection map costs one
    /// 2880-byte block rather than a full-frame decode. False for a missing file, an unreadable one, a
    /// rejection map, and a sidecar written before <see cref="MapKindCard"/> existed: none of those can
    /// be shown to be coverage, and guessing is how a rejection FRACTION would be read as a frame count
    /// and crop the master to nothing.
    /// </remarks>
    public static bool TryReadCoverageMap(string masterPath, [NotNullWhen(true)] out Image? coverage)
    {
        coverage = null;
        if (string.IsNullOrEmpty(masterPath))
        {
            return false;
        }

        // The coverage-named sidecar first, then the rejection slot, which is where a drizzle
        // master written before coverage had a name of its own kept its weight plane. Each is
        // looked for compressed first, since that is how they are written now, then uncompressed,
        // which is how every store before that holds them.
        //
        // The ORDER is what keeps this cheap. The card check below cannot skip the data block of a
        // compressed file (a gzip stream does not seek), so asking it about a rejection FRACTION
        // that happens to be compressed costs a full decode to learn it is not coverage. Looking
        // where coverage actually lives first means that only happens for a master that has no
        // coverage sidecar at all.
        var coveragePath = ExistingSidecarPath(CoveragePathFor(masterPath));
        if (coveragePath is not null)
        {
            // This name carries coverage and nothing else: this writer is its only producer and
            // WriteCoverageMap is its only caller. That is why it is not asked to say so again --
            // the MAPKIND check exists for the AMBIGUOUS slot below, where a weight plane and a
            // rejection fraction are both possible and are opposite in sense.
            return Image.TryReadFitsFile(coveragePath, out coverage);
        }

        if (ExistingSidecarPath(RejectionPathFor(masterPath)) is { } rejectionPath && SaysCoverage(rejectionPath))
        {
            return Image.TryReadFitsFile(rejectionPath, out coverage);
        }

        return false;
    }

    private static bool SaysCoverage(string path)
    {
        try
        {
            using var fitsFile = Image.OpenFitsHeader(path);
            // Header-only where the data block can be SKIPPED, which needs a seek, which a gzip
            // stream does not have: there the whole HDU is read for one card. The caller's order
            // is what keeps that off the common path.
            var kind = (Image.IsGzipped(path)
                    ? fitsFile.ReadFirstImageHdu()
                    : fitsFile.ReadFirstImageHduHeaderOnly())
                ?.Header?.GetStringValue(MapKindCard);
            return string.Equals(kind?.Trim(), CoverageMapKind, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this path IS a rejection / coverage sidecar rather than a master.
    /// <para>
    /// Both are <c>.fits</c> and they sit in the same folder, so <b>anything enumerating masters has
    /// to ask</b>: a plain <c>*.fits</c> glob started counting every master twice the day a sidecar was
    /// first written beside one, and reported it as an extra master rather than as a new file. Callers
    /// that address a master by its session id never meet this; only the ones that walk the folder do.
    /// </para>
    /// </summary>
    public static bool IsMapSidecarPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(Image.GzipSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^Image.GzipSuffix.Length];
        }

        return name.EndsWith(RejectionMapSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(CoverageMapSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(BadPixelMapSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Computes the rejection-map sibling path for a given master path.</summary>
    public static string RejectionPathFor(string masterPath) => SiblingPath(masterPath, RejectionMapSuffix);

    /// <summary>The coverage sibling of a master whose rejection sidecar is a fraction: the stem plus
    /// <see cref="CoverageMapSuffix"/>. A drizzle master written before coverage had its own slot
    /// carries it in the rejection sidecar instead.</summary>
    public static string CoveragePathFor(string masterPath) => SiblingPath(masterPath, CoverageMapSuffix);

    /// <summary>The bad pixel map sibling of a master: the stem plus
    /// <see cref="BadPixelMapSuffix"/>.</summary>
    public static string BadPixelPathFor(string masterPath) => SiblingPath(masterPath, BadPixelMapSuffix);

    /// <summary>
    /// What a map sidecar is CALLED once written: the logical path plus <see cref="Image.GzipSuffix"/>.
    /// </summary>
    /// <remarks>
    /// The logical path stays the addressable name, so a caller asks for "this master's coverage" and
    /// gets an answer that does not depend on how it happens to be stored; only the two ends that
    /// touch the bytes, this and <see cref="ExistingSidecarPath"/>, know about the suffix.
    /// </remarks>
    public static string CompressedPathFor(string logicalPath) => logicalPath + Image.GzipSuffix;

    /// <summary>
    /// The sidecar actually on disk for a logical sidecar path: the compressed one it is written as
    /// today, else the uncompressed one every store written before carries, else null.
    /// </summary>
    public static string? ExistingSidecarPath(string logicalPath)
    {
        if (string.IsNullOrEmpty(logicalPath))
        {
            return null;
        }

        var compressed = CompressedPathFor(logicalPath);
        return File.Exists(compressed) ? compressed : File.Exists(logicalPath) ? logicalPath : null;
    }

    private static string SiblingPath(string masterPath, string suffix)
    {
        // strip trailing .fits / .fit (case-insensitive), then append the suffix
        var dir = Path.GetDirectoryName(masterPath);
        var stem = Path.GetFileNameWithoutExtension(masterPath);
        var combined = string.IsNullOrEmpty(dir) ? stem : Path.Combine(dir, stem);
        return combined + suffix;
    }
}

/// <summary>
/// What a master was registered ON, stamped into its header so the file says so rather than only its
/// directory. A comet-aligned master is otherwise byte-indistinguishable from a star-aligned one by
/// name and by every existing card.
/// </summary>
/// <param name="Basis">"Sidereal" for an ordinary star-aligned stack, "Comet" when registered on a
/// moving body.</param>
/// <param name="TargetBody">Designation the run tracked, when it knows one. A bare <c>--comet</c>
/// reads it from the frames' own OBJECT card, which already states it, so this may be null.</param>
/// <param name="DriftPxPerHour">The applied drift in CANVAS px/hr.</param>
/// <param name="RateSource">"Horizons" for a derived ephemeris, "Manual" for an explicit
/// <c>--comet-rate</c>. Worth distinguishing: only one of them can be wrong in a way re-running fixes.</param>
/// <param name="CanvasOriginX">Where this master's pixel (0, 0) sits in the REFERENCE frame's pixel
/// space, x. The canvas is the union of the registered frames' footprints, so two masters built from
/// the same reference but different frame sets (a seeing split's sharp and soft thirds, a layer and a
/// re-run) have different extents AND different origins; without this card they cannot be overlaid
/// after the fact, and the number was only ever logged. Written as <c>CANVASX0</c>. An autocrop adds
/// its crop offset, so the card stays true on the cropped file.</param>
/// <param name="CanvasOriginY">The y half of <paramref name="CanvasOriginX"/>, <c>CANVASY0</c>.</param>
/// <param name="ReferenceFrame">File name of the reference frame, <c>REFFRAME</c>, so a master says
/// which frame's pixel space its origin cards are in without the manifest beside it.</param>
public sealed record AlignmentProvenance(
    string Basis,
    string? TargetBody = null,
    Vector2? DriftPxPerHour = null,
    string? RateSource = null,
    int? CanvasOriginX = null,
    int? CanvasOriginY = null,
    string? ReferenceFrame = null)
{
    public static readonly AlignmentProvenance Sidereal = new("Sidereal");

    /// <summary>The same provenance for a crop of the master whose top-left sits at
    /// (<paramref name="cropX"/>, <paramref name="cropY"/>) on the canvas: the origin moves with it.</summary>
    public AlignmentProvenance ForCrop(int cropX, int cropY)
        => this with
        {
            CanvasOriginX = CanvasOriginX is { } ox ? ox + cropX : null,
            CanvasOriginY = CanvasOriginY is { } oy ? oy + cropY : null,
        };
}

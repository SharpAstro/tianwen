using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging.Dataset;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Whether a FITS header's DATAMIN/DATAMAX have to be recomputed from the pixels because the
    /// file did not state a usable pair.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a performance gate, and TianWen's own writer is on the good side of it.</b>
    /// <c>WriteToFitsFile</c> emits both cards for every frame it writes -- which is every captured
    /// light, flat, master and plate-solve input, since they all reach it through
    /// <c>IExternal.WriteFitsFileAsync</c> -- so reading our own files skips a full-frame min/max
    /// traversal. Third-party subs generally do not: measured on three ASI533 frames from N.I.N.A.,
    /// none carried either card. Pinned by <c>FitsDataMinMaxGateTests</c>.</para>
    /// <para>Extracted so the test asserts THIS rule rather than a second copy of it. A restated
    /// predicate would keep passing after the real one changed, which is the failure mode that makes
    /// a performance guard worthless.</para>
    /// <para>Both halves are required: a file stating only DATAMAX leaves min NaN and recalculates
    /// anyway, so writing one without the other buys nothing.</para>
    /// </remarks>
    internal static bool NeedsMinMaxRecalc(float minValue, float maxValue)
        => float.IsNaN(minValue) || minValue < 0 || float.IsNaN(maxValue)
            || maxValue is <= 0 || maxValue <= minValue;

    /// <summary>
    /// Reads a FITS file into a SELF-OWNED image (frame ownership: convention 2). Nothing is required
    /// of the caller and <see cref="Release"/> is a no-op; see the frame-ownership notes on
    /// <see cref="Image"/>, and the <c>pooled</c> overload below for the variant the
    /// caller OWNS.
    /// </summary>
    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        return TryReadFitsFile(fileName, out image, out _);
    }

    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image, out WCS? wcs)
    {
        return TryReadFitsFile(fileName, out image, out wcs, pooled: false);
    }

    /// <summary>
    /// Reads a FITS file, optionally renting the channel arrays from <see cref="Array2DPool{T}"/>
    /// so a bulk reader recycles them instead of handing the GC a fresh large-object array per file.
    ///
    /// <para><b>Pooling is opt-in, and the caller takes on an obligation.</b> It is the difference
    /// between frame-ownership convention 2 (self-owned, nothing required of the caller) and
    /// convention 3 (pool-owned, the caller OWNS the frame) -- see the frame-ownership notes on
    /// <see cref="Image"/>. With <paramref name="pooled"/> set, the returned image's channels carry a
    /// <see cref="ChannelBuffer"/> whose release returns the array to the pool, so
    /// <see cref="Release"/> stops being a no-op: the caller must not touch the image afterwards,
    /// exactly as for a camera frame. Every pre-existing call site passes <see langword="false"/>
    /// and is byte-for-byte unchanged -- several of them <see cref="Release"/> an image and keep
    /// reading it, which is harmless only while file loads own their arrays outright.</para>
    ///
    /// <para>Worth it only for a reader that loops over many frames: it is one full-size
    /// <c>float[,]</c> per channel per file, on the large-object heap, and for any integer BITPIX
    /// there are two live at once (FITS.Lib's typed array plus the float destination). Eight
    /// concurrent readers over 4144x2822x3 int32 frames is ~2.2 GB of churn, which is an
    /// <see cref="OutOfMemoryException"/> on a 16 GB box with other work running.</para>
    /// </summary>
    public static bool TryReadFitsFile(string fileName, [NotNullWhen(true)] out Image? image, out WCS? wcs, bool pooled)
    {
        // A TryX that throws is not a TryX, and this one threw while both its siblings did not:
        // TryReadFitsHeader (just below) has caught since it was written and TryReadTiff wraps its
        // whole body. Only the full-image path was bare, so a file the header scanner would merely
        // SKIP took the viewer's load task down with it -- and silently, because that load runs in a
        // Task.Run, where an escaping exception becomes an unobserved fault rather than a crash.
        //
        // This catch is a QUARANTINE BOUNDARY around FITS.Lib, not general practice. Our own code
        // should never be the thing throwing a NullReferenceException, and where it does the answer is
        // to fix it rather than to swallow it. FITS.Lib is the exception: it is a mechanical port of a
        // Java library whose error handling predates us and does not translate. The bug found here is
        // the shape of the whole class -- BasicHDU.ObservationDate caught a parse failure, assigned
        // null, then cast that null to DateTime, so the handler written to tolerate a bad date WAS the
        // crash. Fixed in FITS.Lib 5.0.401, and there is no reason to believe it was the only one.
        //
        // So the boundary deliberately catches everything, including the NRE class that a narrower
        // filter would let through. Nothing is hidden by it: the caller learns the file is unreadable
        // and ViewerController logs whatever escaped with its stack, which is how the next one gets
        // found and fixed upstream instead of vanishing.
        try
        {
            using var bufferedReader = new BufferedFile(fileName, FileAccess.Read, FileShare.Read, 1000 * 2088);
            using var fitsFile = new Fits(bufferedReader, fileName.EndsWith(".gz"));
            return TryReadFitsFile(fitsFile, out image, out wcs, pooled);
        }
        catch (Exception)
        {
            image = null;
            wcs = null;
            return false;
        }
    }

    /// <summary>
    /// Reads only the FITS header for <paramref name="fileName"/>, skipping the pixel
    /// data block via <c>Fits.ReadHDUHeaderOnly</c>. Returns a header-only handle
    /// suitable for folder enumeration / frame manifests where pixel data isn't
    /// needed yet. Compared to <see cref="TryReadFitsFile(string, out Image?)"/>
    /// this avoids the per-file pixel allocation + read: 36 MB → ~3 KB per
    /// 3008² float32 frame, ~4 s saved on a 100-frame folder scan.
    /// </summary>
    /// <remarks>
    /// Header-parsing logic mirrors <see cref="TryReadFitsFile(Fits, out Image?, out WCS?)"/>
    ///; keep in sync until the two paths are refactored onto a shared
    /// <c>ParseHduMetadata</c> helper.
    /// </remarks>
    public static bool TryReadFitsHeader(string fileName, [NotNullWhen(true)] out Calibration.FrameInfo? frameInfo)
    {
        frameInfo = null;
        // Resilient header read over an UNTRUSTED archive: a malformed / truncated / locked FITS,
        // or a header FITS.Lib itself can't parse, must be SKIPPED (return false), never fatal -- a
        // single bad file cannot abort a 20k-frame archive scan. The known offender is
        // BasicHDU.ObservationDate, which NREs by unboxing a null DateTime when DATE-OBS is missing
        // or unparseable (FitsDate throws -> result stays null -> (DateTime)null); such frames
        // (focusing junk, SharpCap captures, non-standard exports) are not usable session frames
        // anyway. Honour the TryX contract rather than propagate.
        try
        {
            using var bufferedReader = new BufferedFile(fileName, FileAccess.Read, FileShare.Read, 4 * 2880);
            using var fitsFile = new Fits(bufferedReader, fileName.EndsWith(".gz"));
            var hdu = fitsFile.ReadFirstImageHduHeaderOnly();
            if (hdu?.Axes?.Length is not { } axisLength
                || hdu.Data is not ImageData
                || !(BitDepth.FromValue(hdu.BitPix) is { } bitDepth))
            {
                return false;
            }

            int height, width, channelCount;
            switch (axisLength)
            {
                case 2:
                    height = hdu.Axes[0];
                    width = hdu.Axes[1];
                    channelCount = 1;
                    break;
                case 3:
                    channelCount = hdu.Axes[0];
                    height = hdu.Axes[1];
                    width = hdu.Axes[2];
                    break;
                default:
                    return false;
            }

            var imageMeta = ParseImageMetaFromHeader(hdu, channelCount);
            var stackedFrameCount = ReadStackedFrameCount(hdu.Header);
            frameInfo = new Calibration.FrameInfo(fileName, width, height, channelCount, bitDepth, imageMeta, stackedFrameCount);
            return true;
        }
        catch (Exception)
        {
            frameInfo = null;
            return false;
        }
    }

    // Pointing intent for the master / re-solve hint. Captures the INTENDED
    // target -- sexagesimal OBJCTRA/OBJCTDEC, falling back to decimal RA/DEC
    // degrees (NINA writes both; neither throws) -- NOT the solved WCS centre
    // (CRVAL, which WCS.FromHeader handles separately). Mirrors WCS.FromHeader's
    // OBJCTRA/OBJCTDEC -> RA/DEC fallback order (see the reasoning there: RA/DEC
    // is the position the mount REPORTED and is only as good as its sync) so a
    // master written WITHOUT an embedded WCS still carries the coordinates needed
    // to (re-)plate-solve it, and so the two parse sites cannot answer differently.
    private static (double RaHours, double DecDeg) ParseTargetCoords(Header header)
    {
        // OBJCTRA "HH MM SS", OBJCTDEC "+DD MM SS" (space-separated).
        var objctRa = header.GetStringValue("OBJCTRA");
        var objctDec = header.GetStringValue("OBJCTDEC");
        if (!string.IsNullOrWhiteSpace(objctRa) && !string.IsNullOrWhiteSpace(objctDec))
        {
            var hours = CoordinateUtils.HMSToHours(objctRa.Replace(' ', ':'));
            var deg = CoordinateUtils.DMSToDegree(objctDec.Replace(' ', ':'));
            if (!double.IsNaN(hours) && !double.IsNaN(deg))
            {
                return (hours, deg);
            }
        }

        // Only reached when OBJCTRA/OBJCTDEC are absent or unparseable.
        var raDeg = header.GetDoubleValue("RA", double.NaN);
        var decDeg = header.GetDoubleValue("DEC", double.NaN);
        if (!double.IsNaN(raDeg) && !double.IsNaN(decDeg))
        {
            return (raDeg / 15.0, decDeg);
        }

        return (double.NaN, double.NaN);
    }

    /// <summary>Interprets the string form of the CFAIMAGE card, which capture/processing tools write
    /// inconsistently: N.I.N.A. omits it, some tools write a quoted <c>'T'</c>/<c>'F'</c>, and Astro
    /// Pixel Processor writes the Bayer PATTERN string (e.g. <c>'RGGB'</c>); reading that as a boolean
    /// yields <c>false</c> and wrongly forces <see cref="SensorType.Monochrome"/>, which drops an
    /// otherwise-matching Bayer master from calibration selection. A KNOWN pattern string therefore
    /// means the frame IS a CFA image; any other non-boolean token ("NONE", a corrupted card, a future
    /// sentinel) keeps the old GetBooleanValue-era safe default of NOT-CFA rather than guessing.
    /// Absent/empty → <c>null</c> (defer to BAYERPAT / COLORTYP).</summary>
    internal static bool? ParseCfaImageFlag(string? cfaImage)
    {
        if (string.IsNullOrWhiteSpace(cfaImage)) return null;
        var v = cfaImage.Trim();
        if (bool.TryParse(v, out var parsed)) return parsed;   // "True" / "False"
        if (v is "T" or "t" or "1") return true;
        if (v is "F" or "f" or "0") return false;
        return SensorType.IsKnownBayerPattern(v);              // "RGGB" => CFA; anything else => not
    }

    /// <summary>Reads the CFAIMAGE card in either of its wire encodings: a quoted STRING card (see
    /// <see cref="ParseCfaImageFlag"/>) via <c>GetStringValue</c>, or a genuine FITS LOGICAL card
    /// (unquoted <c>T</c>/<c>F</c>, the ASCOM form), which <c>GetStringValue</c> cannot see (it
    /// returns null for any non-string card), so that form falls back to <c>GetBooleanValue</c>.
    /// Returns the CFA flag plus the Bayer-pattern candidate when (and only when) the card itself
    /// carried a known pattern, so a boolean-form value can never masquerade as a pattern downstream.</summary>
    private static (bool? IsCfa, string? PatternCandidate) ParseCfaImageCard(Header header)
    {
        if (header.GetStringValue("CFAIMAGE") is { } cfaImage)
        {
            var v = cfaImage.Trim();
            return (ParseCfaImageFlag(v), SensorType.IsKnownBayerPattern(v) ? v : null);
        }
        return (header.ContainsKey("CFAIMAGE") ? header.GetBooleanValue("CFAIMAGE", false) : null as bool?, null);
    }

    /// <summary>Reads an integer-valued camera register card (GAIN / OFFSET / …) that some tools write
    /// as a FLOAT, Astro Pixel Processor masters carry <c>GAIN = 121.0</c>, which <c>GetIntValue</c>
    /// cannot coerce (Int64.Parse fails → silently returns its default, so the value reads "unknown"
    /// and e.g. a matching foreign master drops out of gain-scored calibration). Reads as double and
    /// rounds, exactly as FOCALLEN does for the same float-card reason. Absent or non-finite
    /// (<c>"NaN"</c>/<c>"Infinity"</c> parse as valid doubles but are meaningless register values,
    /// and must not collapse to gain 0) → <c>null</c>.</summary>
    private static int? ReadIntegerLikeCard(Header header, string key)
    {
        var value = header.GetDoubleValue(key, double.NaN);
        return double.IsNaN(value) || double.IsInfinity(value) ? null : (int)Math.Round(value);
    }

    // Shared metadata parse: pulled out of TryReadFitsFile so the header-only
    // path uses the same logic. Min/max value computation stays in the pixel
    // read path because the header DATAMIN/DATAMAX fields are often missing or
    // wrong; the pixel-walk recomputes them.
    private static ImageMeta ParseImageMetaFromHeader(BasicHDU hdu, int channelCount)
    {
        var exposureStartTime = new DateTime(hdu.ObservationDate.Ticks, DateTimeKind.Utc);
        // EXPTIME and EXPOSURE are both in the wild for the same quantity (N.I.N.A. writes both,
        // some capture software only the second), so either will do and 0 is the last resort.
        // The fallback list read { EXPTIME, EXPTIME, 0 } in BOTH copies of this parse, so EXPOSURE
        // was dead everywhere and a frame carrying only that card read as a ZERO-second exposure --
        // which is not inert, because exposure is part of MasterGroupKey and so decides which dark
        // calibrates what.
        var maybeExpTime = hdu.Header.GetDoubleValue("EXPTIME", double.NaN);
        var maybeExposure = hdu.Header.GetDoubleValue("EXPOSURE", double.NaN);
        var exposureDuration = TimeSpan.FromSeconds(new double[] { maybeExpTime, maybeExposure, 0.0 }.First(x => !double.IsNaN(x)));
        var instrument = hdu.Instrument;
        var telescope = hdu.Telescope;
        var pixelSizeX = hdu.Header.GetFloatValue("XPIXSZ", float.NaN);
        var pixelSizeY = hdu.Header.GetFloatValue("YPIXSZ", float.NaN);
        var xbinning = hdu.Header.GetIntValue("XBINNING", 1);
        var ybinning = hdu.Header.GetIntValue("YBINNING", 1);
        // FOCALLEN is often written as a float (e.g. "270.0"), and nom.tam.fits's
        // GetIntValue won't coerce -- falls back to -1, which silently disables
        // pixel-scale derivation downstream (plate solver bails on null ImageDim).
        // Some software emits the keyword as "FOCLEN" instead; accept either.
        var focalLength = (int)Math.Round(hdu.Header.GetDoubleValue("FOCALLEN",
            hdu.Header.GetDoubleValue("FOCLEN", -1.0)));
        var aperture = hdu.Header.GetIntValue("APTDIA", -1);
        var focusPos = hdu.Header.GetIntValue("FOCUSPOS", hdu.Header.GetIntValue("FOCPOS", -1));
        var filterName = hdu.Header.GetStringValue("FILTER");
        var filterClassName = hdu.Header.GetStringValue("FILTCLAS");
        var sensorModel = hdu.Header.GetStringValue("SENSOR") ?? "";
        var ccdTemp = hdu.Header.GetFloatValue("CCD-TEMP", float.NaN);
        var rowOrder = RowOrder.FromFITSValue(hdu.Header.GetStringValue("ROWORDER")) ?? RowOrder.TopDown;
        // FRAME is Astro Pixel Processor's card and comes LAST, only when neither of the usual two
        // is present: APP writes it on derived products ('Other/Processed') where there is no
        // IMAGETYP at all, and that is the only thing distinguishing such a file from a light.
        var frameTypeRaw = hdu.Header.GetStringValue("FRAMETYP")
            ?? hdu.Header.GetStringValue("IMAGETYP")
            ?? hdu.Header.GetStringValue("FRAME");
        var frameType = FrameType.FromFITSValue(frameTypeRaw) ?? FrameType.None;
        var isMaster = FrameType.IsMasterFITSValue(frameTypeRaw);
        // Prefer FILTCLAS, fall back to FILTER. The blank guard is load-bearing: FromName(null)
        // returns None, NOT Unknown, so testing only `!= Unknown` accepted a missing FILTCLAS as a
        // definitive "no filter" and discarded FILTER unread. FILTCLAS is our own convention, so
        // that silently made EVERY third-party file with a filter (all of N.I.N.A.'s) read as
        // unfiltered. Pinned by FitsHeaderEditorTests + FilterHeaderFallbackTests.
        var filter = !string.IsNullOrWhiteSpace(filterClassName) && Filter.FromName(filterClassName) is var f && f != Filter.Unknown
            ? f : Filter.FromName(filterName);
        filter = filter with { RawName = filterName };
        var (isCFA, cfaPattern) = ParseCfaImageCard(hdu.Header);
        var (sensorType, bayerOffsetX, bayerOffsetY) = SensorType.FromFITSValue(
            isCFA,
            channelCount,
            // XBAYROFF/YBAYROFF is the living convention (MaxIm DL, N.I.N.A.). BAYOFFX/BAYOFFY is
            // the Atik Artemis legacy spelling that TianWen wrote until 2026-08-17 (and old SharpCap
            // emitted both sets side by side), kept as the read fallback so those files stay
            // legible. Reading only the legacy name made every N.I.N.A. file's offset silently land
            // at (0,0) -- benign only when (0,0) happens to be true.
            hdu.Header.GetIntValue("XBAYROFF", hdu.Header.GetIntValue("BAYOFFX", 0)),
            hdu.Header.GetIntValue("YBAYROFF", hdu.Header.GetIntValue("BAYOFFY", 0)),
            [hdu.Header.GetStringValue("BAYERPAT"), hdu.Header.GetStringValue("COLORTYP"), cfaPattern]
        );
        var latitude = hdu.Header.GetFloatValue("SITELAT", float.NaN);
        var longitude = hdu.Header.GetFloatValue("SITELONG", float.NaN);
        var siteElevation = hdu.Header.GetFloatValue("SITEELEV", float.NaN);
        // The white balance travels as four cards. All of them have to be present and finite for the
        // calibration to mean anything -- a partial triple is worse than none, since a consumer would
        // apply two channels of somebody else's calibration and leave the third alone.
        var wbR = hdu.Header.GetFloatValue("WBRED", float.NaN);
        var wbG = hdu.Header.GetFloatValue("WBGREEN", float.NaN);
        var wbB = hdu.Header.GetFloatValue("WBBLUE", float.NaN);
        var colourCalibration = float.IsFinite(wbR) && float.IsFinite(wbG) && float.IsFinite(wbB)
            ? new ColourCalibration(wbR, wbG, wbB,
                hdu.Header.GetStringValue("WBSOURCE") ?? ColourCalibration.SpccSource)
            : (ColourCalibration?)null;
        // Guiding: GUIDERMS alone decides presence. The others are refinements of it, and a frame that
        // states a total RMS has been guided whether or not it also managed to record a peak.
        var guideRms = hdu.Header.GetFloatValue("GUIDERMS", float.NaN);
        var guiding = float.IsFinite(guideRms)
            ? new GuidingStats(
                guideRms,
                hdu.Header.GetFloatValue("GUIRMSRA", float.NaN),
                hdu.Header.GetFloatValue("GUIRMSDE", float.NaN),
                hdu.Header.GetFloatValue("GUIDEPK", float.NaN),
                hdu.Header.GetIntValue("GUIDEN", 0))
            : (GuidingStats?)null;
        var objectName = hdu.Header.GetStringValue("OBJECT") ?? "";
        var swModifier = hdu.Header.GetStringValue("SWMODIFY") ?? "";
        // GAIN/OFFSET are int cards in N.I.N.A. files but float cards in e.g. Astro Pixel Processor
        // masters -- ReadIntegerLikeCard coerces both forms (and rejects NaN/Infinity as unknown).
        var gain = (short)(ReadIntegerLikeCard(hdu.Header, "GAIN") ?? -1);
        var camOffset = ReadIntegerLikeCard(hdu.Header, "OFFSET") ?? ReadIntegerLikeCard(hdu.Header, "BLKLEVEL") ?? ReadIntegerLikeCard(hdu.Header, "CAMOFFS") ?? -1;
        var setCCDTemp = hdu.Header.GetFloatValue("SET-TEMP", float.NaN);
        var egain = hdu.Header.GetFloatValue("EGAIN", float.NaN);
        // SATURATE (astrometry.net / SExtractor / PixInsight convention): the ADU level at which the
        // sensor saturates, in the same units as the stored pixel data -> maps directly onto
        // ImageMeta.SensorFullScaleAdu. Written by TianWen itself (round-trip) and some third-party
        // tools; neither N.I.N.A. nor SharpCap emits it. NaN > 0 is false, so absent -> null.
        // Deliberately trusted as written -- no container-bit-depth plausibility gate like
        // ICameraDriver.GetImageAsync's live-path guard: a file-level SATURATE is an assertion by the
        // producing software, and 8-bit-origin data legitimately lands in 16-bit containers
        // (SATURATE=255 + BITPIX=16 is self-consistent, e.g. the PlateSolveTestFile fixture). A
        // genuinely tainted too-LOW claim is defused by the UnitScaleDivisor clamp (never below the
        // observed peak), which degrades exactly to the pre-SATURATE observed-peak behaviour.
        var saturate = hdu.Header.GetFloatValue("SATURATE", float.NaN);
        // SWCREATE is the SharpCap / N.I.N.A. spelling; SOFTWARE is what Astro Pixel Processor
        // writes. Read both so a file states its author whichever package made it -- APP's
        // integrations otherwise look authorless, and its SOFTWARE card is the only thing naming
        // a processing package rather than a capture one.
        var swCreator = hdu.Header.GetStringValue("SWCREATE")
            ?? hdu.Header.GetStringValue("SOFTWARE")
            ?? "";
        // PIERSIDE: N.I.N.A. + most modern capture software write a string ("East"
        // / "West" / "pierEast" / "pierWest"). ASCOM also defines numeric variants
        // (0 = Normal/East, 1 = ThroughThePole/West). Try both.
        var pierSide = ParsePierSide(hdu.Header.GetStringValue("PIERSIDE"));
        var (targetRa, targetDec) = ParseTargetCoords(hdu.Header);
        // The scale the FILE states for itself, preferred over deriving one from FOCALLEN because
        // that card is whatever a human typed into a capture profile. The pixel-read path used to
        // parse this into a local and drop it on the floor while this path never parsed it at all,
        // so a declared scale was unreachable however you opened the file.
        var declaredPixelScale = hdu.Header.GetFloatValue("PIXSCALE", hdu.Header.GetFloatValue("SCALE", float.NaN));

        return new ImageMeta(
            instrument,
            exposureStartTime,
            exposureDuration,
            frameType,
            telescope,
            pixelSizeX,
            pixelSizeY,
            focalLength,
            focusPos,
            filter,
            xbinning,
            ybinning,
            ccdTemp,
            sensorType,
            bayerOffsetX,
            bayerOffsetY,
            rowOrder,
            latitude,
            longitude,
            objectName,
            Gain: gain,
            Offset: camOffset,
            SetCCDTemperature: setCCDTemp,
            ElectronsPerADU: egain,
            SWCreator: swCreator,
            SWModifier: swModifier,
            Aperture: aperture,
            SensorModel: sensorModel,
            TargetRA: targetRa,
            TargetDec: targetDec,
            PierSide: pierSide,
            SensorFullScaleAdu: saturate > 0 ? saturate : null,
            DeclaredPixelScale: declaredPixelScale,
            SiteElevation: siteElevation,
            ColourCalibration: colourCalibration,
            Guiding: guiding
        )
        { IsMaster = isMaster };
    }

    /// <summary>
    /// Parses the FITS <c>PIERSIDE</c> header into a <see cref="Devices.PointingState"/>.
    /// Recognises N.I.N.A.'s strings ("East"/"West"/"pierEast"/"pierWest"), the
    /// ASCOM short forms ("E"/"W"), the ASCOM numeric forms ("0"/"1"), and the
    /// "Normal"/"ThroughThePole" full names. Anything else (including
    /// null / empty / "unknown") returns <see cref="Devices.PointingState.Unknown"/>.
    /// </summary>
    private static Devices.PointingState ParsePierSide(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Devices.PointingState.Unknown;
        }
        var s = raw.Trim();
        // ASCOM standard: 0 = pierEast / Normal, 1 = pierWest / ThroughThePole
        // (these names trade off whether you index by physical-pier or by
        // mount-pointing -- "Normal" / "ThroughThePole" is the ASCOM canon).
        if (s.Equals("0", System.StringComparison.Ordinal) ||
            s.Equals("E", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("East", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pierEast", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("Normal", System.StringComparison.OrdinalIgnoreCase))
        {
            return Devices.PointingState.Normal;
        }
        if (s.Equals("1", System.StringComparison.Ordinal) ||
            s.Equals("W", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("West", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pierWest", System.StringComparison.OrdinalIgnoreCase) ||
            s.Equals("ThroughThePole", System.StringComparison.OrdinalIgnoreCase))
        {
            return Devices.PointingState.ThroughThePole;
        }
        return Devices.PointingState.Unknown;
    }

    /// <summary>
    /// Hands every rented plane back to the pool. Used on the failure path, where no
    /// <see cref="Image"/> is constructed and so nothing else will ever release them.
    /// </summary>
    private static void ReturnRented(float[][,] planes, bool[]? rented)
    {
        if (rented is null)
        {
            return;
        }
        for (var c = 0; c < rented.Length; c++)
        {
            if (rented[c] && planes[c] is { } plane)
            {
                Array2DPool<float>.Return(plane);
            }
        }
    }

    /// <summary>
    /// Wraps the planes as channels, giving each RENTED one a <see cref="ChannelBuffer"/> that
    /// returns the array to <see cref="Array2DPool{T}"/> on release. This is the same ownership
    /// protocol a camera frame uses (<c>DALCameraDriver._freeBuffers</c>); only the recycler
    /// differs. A plane we did not rent gets no buffer, so <see cref="Release"/> leaves it alone.
    /// </summary>
    private static ImmutableArray<Channel> WrapPooledPlanes(float[][,] planes, bool[] rented, float minValue, float maxValue)
    {
        var builder = ImmutableArray.CreateBuilder<Channel>(planes.Length);
        for (var c = 0; c < planes.Length; c++)
        {
            var channel = new Channel(planes[c], default, minValue, maxValue, (byte)c);
            builder.Add(rented[c]
                ? channel with { Buffer = new ChannelBuffer(planes[c], static array => Array2DPool<float>.Return(array)) }
                : channel);
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// How many raw frames were integrated to make this file, or 0 when it is a raw sub. Non-zero
    /// is what marks a file as a stacking PRODUCT, so the scan can drop it instead of re-ingesting
    /// a master as if it were a fresh frame.
    ///
    /// <para>Two spellings, because two producers: <c>STACK_N</c> is what
    /// <see cref="Stacking.IntegrationFitsWriter"/> stamps on our own masters and rejection maps,
    /// and <c>NUMFRAME</c> is Astro Pixel Processor's equivalent. Reading only the first missed
    /// every APP product, and for APP's LIGHT integrations it missed them ENTIRELY: APP marks a
    /// calibration master with <c>IMAGETYP='MASTERFLAT'</c> and friends (which
    /// <see cref="FrameType.IsMasterFITSValue"/> catches), but a light integration keeps
    /// <c>IMAGETYP='LIGHT'</c> and copies the reference sub's header verbatim -- right down to
    /// <c>SWCREATE='N.I.N.A. ...'</c> -- so neither the master flag nor the TianWen-product check
    /// can see it. <c>NUMFRAME</c> is the one card present on all five APP product kinds.</para>
    /// </summary>
    private static int ReadStackedFrameCount(Header header)
        => header.GetIntValue("STACK_N", 0) is var stackN and > 0
            ? stackN
            : header.GetIntValue("NUMFRAME", 0);

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image)
    {
        return TryReadFitsFile(fitsFile, out image, out _);
    }

    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image, out WCS? wcs)
    {
        return TryReadFitsFile(fitsFile, out image, out wcs, pooled: false);
    }

    /// <inheritdoc cref="TryReadFitsFile(string, out Image?, out WCS?, bool)"/>
    public static bool TryReadFitsFile(Fits fitsFile, [NotNullWhen(true)] out Image? image, out WCS? wcs, bool pooled)
    {
        wcs = null;
        // Not ReadHDU: the image need not be in HDU 0, and in a tile-compressed file it never
        // is -- see FitsHduExtensions.
        var hdu = fitsFile.ReadFirstImageHdu();
        if (hdu?.Axes?.Length is not { } axisLength
            || hdu.Data is not ImageData imageData
            || imageData.DataArray is not Array dataArray
            || dataArray.Length == 0
            || !(BitDepth.FromValue(hdu.BitPix) is { } bitDepth)
        )
        {
            image = default;
            return false;
        }

        int height, width, channelCount;

        switch (axisLength)
        {
            case 2:
                height = hdu.Axes[0];
                width = hdu.Axes[1];
                channelCount = 1;
                break;

            case 3:
                channelCount = hdu.Axes[0];
                height = hdu.Axes[1];
                width = hdu.Axes[2];
                break;

            default:
                image = null;
                return false;
        }

        // Pixel-only locals. Everything else this frame says about itself comes from the ONE
        // metadata parse below -- the same one the header-only path uses. It used to be copied out
        // here, and the copies had drifted: this block parsed EXPOSURE, PIXSCALE/SCALE and EQUINOX
        // into locals it then never read, while the shared parse never learned about them at all.
        var pedestal = hdu.Header.GetFloatValue("PEDESTAL", 0f);
        var bzero = (float)hdu.BZero;
        var bscale = (float)hdu.BScale;
        var imageMeta = ParseImageMetaFromHeader(hdu, channelCount);

        var minValue = (float)hdu.MinimumValue;
        var maxValue = (float)hdu.MaximumValue;
        bool needsMinMaxValRecalc = NeedsMinMaxRecalc(minValue, maxValue);
        if (needsMinMaxValRecalc)
        {
            maxValue = float.MinValue;
            minValue = float.MaxValue;
        }

        bool trivialScaling = bscale == 1f && bzero == 0f;
        var imgChannels = new float[channelCount][,];

        // Which channels were RENTED, so only those are handed back to the pool. The zero-copy
        // branch adopts FITS.Lib's own array, which we never rented -- returning it would seed the
        // pool with foreign arrays and blur who owns what.
        bool[]? rented = null;

        // Use GetChannel API from FITS.Lib 4.2 for per-channel access
        for (int c = 0; c < channelCount; c++)
        {
            var channelArray = imageData.GetChannel(c);

            if (trivialScaling && channelArray is float[,] floatChannel)
            {
                // Zero-copy: reuse float[,] from FITS.Lib directly
                imgChannels[c] = floatChannel;
            }
            else
            {
                // A rented array arrives dirty (the pool clears on neither Rent nor Return), which
                // is safe here only because ConvertChannel writes every pixel of the destination.
                imgChannels[c] = pooled ? Array2DPool<float>.Rent(height, width) : new float[height, width];
                if (pooled)
                {
                    (rented ??= new bool[channelCount])[c] = true;
                }
                switch (channelArray)
                {
                    case byte[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case short[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case int[,] src: ConvertChannel(src, imgChannels[c]); break;
                    case float[,] src: ConvertChannel(src, imgChannels[c]); break;
                    default:
                        ReturnRented(imgChannels, rented);
                        image = null;
                        return false;
                }
            }
        }

        if (needsMinMaxValRecalc)
        {
            RecalcMinMax(imgChannels, channelCount, ref minValue, ref maxValue);
        }

        // No min/max tracking here on purpose: RecalcMinMax below computes exactly the same two
        // values from the same planes, vectorised, under the same needsMinMaxValRecalc flag -- so
        // tracking them inline was a scalar duplicate of a pass that runs anyway, paid as a branch
        // and two MathF calls on every pixel of the hot conversion loop. The NaN semantics match:
        // the guard here skipped NaN and TensorPrimitives MinNumber/MaxNumber skip it too.
        //
        // Not a hypothetical path -- a real sub carries no DATAMIN/DATAMAX (measured on three ASI533
        // frames), so needsMinMaxValRecalc is TRUE for the files this reader exists to read.
        void ConvertChannel<T>(T[,] src, float[,] dst) where T : struct, INumberBase<T>
        {
            var dstSpan = dst.AsSpan2D();
            for (int h = 0; h < height; h++)
            {
                var row = dstSpan.GetRowSpan(h);
                for (int w = 0; w < width; w++)
                {
                    row[w] = bscale * float.CreateTruncating(src[h, w]) + bzero;
                }
            }
        }

        static void RecalcMinMax(float[][,] channels, int channelCount, ref float minValue, ref float maxValue)
        {
            for (int c = 0; c < channelCount; c++)
            {
                // The local PARAMETER, not the field: this is a static local function, so it has no
                // instance to ask for residency and its caller has already materialised the arrays.
                var channel = channels[c];
                var span = MemoryMarshal.CreateReadOnlySpan(ref channel[0, 0], channel.Length);
                // MinNumber/MaxNumber skip NaN values (IEEE 754 minNum/maxNum semantics)
                maxValue = MathF.Max(maxValue, TensorPrimitives.MaxNumber(span));
                minValue = MathF.Min(minValue, TensorPrimitives.MinNumber(span));
            }
        }

        image = rented is null
            ? new Image(imgChannels, bitDepth, maxValue, minValue, pedestal, imageMeta)
            : new Image(WrapPooledPlanes(imgChannels, rented, minValue, maxValue), bitDepth, pedestal, imageMeta);
        wcs = WCS.FromHeader(hdu.Header);
        return true;
    }

    public void WriteToFitsFile(string fileName, WCS? wcs = null)
        => WriteToFitsFile(fileName, wcs, extraHeaders: null);

    /// <summary>
    /// Overload that adds caller-supplied custom header records after the
    /// standard ImageMeta + WCS writes. Used by the stacking pipeline to
    /// stamp <c>STACK_N</c>, <c>REJ_RATE</c>, etc. on master output without
    /// expanding <see cref="ImageMeta"/> with stack-specific fields.
    /// </summary>
    /// <param name="extraHeaders">Maps FITS card name -&gt; (value, comment).
    /// Value type may be <see cref="int"/>, <see cref="long"/>,
    /// <see cref="float"/>, <see cref="double"/>, <see cref="bool"/>, or
    /// <see cref="string"/>; FITS.Lib's <c>Header.AddValue</c> overloads
    /// dispatch on type. Unsupported value types throw.</param>
    public void WriteToFitsFile(string fileName, WCS? wcs, IReadOnlyDictionary<string, (object Value, string Comment)>? extraHeaders)
    {
        var (channelCount, width, height) = Shape;
        using var fits = new Fits();
        Array arrayToWrite;
        int bzero;
        bool dataIsInt;
        switch (bitDepth)
        {
            case BitDepth.Int8:
                bzero = 0;
                dataIsInt = true;
                if (channelCount == 1)
                {
                    var byteArray = new byte[height, width];
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            byteArray[h, w] = (byte)Planes[0].Data[h, w];
                        }
                    }
                    arrayToWrite = byteArray;
                }
                else
                {
                    var byteChannels = new byte[channelCount][,];
                    for (var c = 0; c < channelCount; c++)
                    {
                        byteChannels[c] = new byte[height, width];
                        for (var h = 0; h < height; h++)
                        {
                            for (var w = 0; w < width; w++)
                            {
                                byteChannels[c][h, w] = (byte)Planes[c].Data[h, w];
                            }
                        }
                    }
                    arrayToWrite = byteChannels;
                }
                break;

            case BitDepth.Int16:
                bzero = 32768;
                dataIsInt = true;
                if (channelCount == 1)
                {
                    var shortArray = new short[height, width];
                    for (var h = 0; h < height; h++)
                    {
                        for (var w = 0; w < width; w++)
                        {
                            shortArray[h, w] = (short)(Planes[0].Data[h, w] - bzero);
                        }
                    }
                    arrayToWrite = shortArray;
                }
                else
                {
                    var shortChannels = new short[channelCount][,];
                    for (var c = 0; c < channelCount; c++)
                    {
                        shortChannels[c] = new short[height, width];
                        for (var h = 0; h < height; h++)
                        {
                            for (var w = 0; w < width; w++)
                            {
                                shortChannels[c][h, w] = (short)(Planes[c].Data[h, w] - bzero);
                            }
                        }
                    }
                    arrayToWrite = shortChannels;
                }
                break;

            case BitDepth.Float32:
                bzero = 0;
                dataIsInt = false;
                if (channelCount == 1)
                {
                    arrayToWrite = Planes[0].Data;
                }
                else
                {
                    // Jagged reference projection of the channel planes (no pixel copy); 
                    // the FITS factory wants the raw float[][,] shape for multi-channel data.
                    var planes = new float[channelCount][,];
                    for (var c = 0; c < channelCount; c++)
                    {
                        planes[c] = Planes[c].Data;
                    }
                    arrayToWrite = planes;
                }
                break;

            default:
                throw new NotSupportedException($"Bits per pixel {bitDepth} is not supported");
        }
        var basicHdu = FitsFactory.HDUFactory(arrayToWrite);
        basicHdu.Header.Bitpix = (int)bitDepth;
        AddHeaderValueIfHasValue("BZERO", bzero, "offset data range to that of unsigned short");
        AddHeaderValueIfHasValue("BSCALE", 1, "default scaling factor");
        AddHeaderValueIfHasValue("PEDESTAL", pedestal, "", isDataValue: true);
        AddHeaderValueIfHasValue("XBINNING", imageMeta.BinX, "");
        AddHeaderValueIfHasValue("YBINNING", imageMeta.BinY, "");
        AddHeaderValueIfHasValue("XPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("YPIXSZ", imageMeta.PixelSizeX, "");
        AddHeaderValueIfHasValue("DATE-OBS", FitsDate.GetFitsDateString(imageMeta.ExposureStartTime.UtcDateTime), "UT");
        AddHeaderValueIfHasValue("EXPTIME", imageMeta.ExposureDuration.TotalSeconds, "seconds");
        AddHeaderValueIfHasValue("IMAGETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("FRAMETYP", imageMeta.FrameType, "");
        AddHeaderValueIfHasValue("DATAMIN", MinValue, "");
        AddHeaderValueIfHasValue("DATAMAX", MaxValue, "");
        AddHeaderValueIfHasValue("INSTRUME", imageMeta.Instrument, "");
        AddHeaderValueIfHasValue("TELESCOP", imageMeta.Telescope, "");
        // OPTSYS is TianWen-defined (surveyed 2026-08-17: no optical-system-kind keyword exists in
        // SBFITSEXT, MaxIm DL, or N.I.N.A.; TELESCOP is only a name): the coarse kind behind
        // TELESCOP, so a reader can judge a field-radius trend without knowing the hardware by
        // name. Written only when it is a positive fact: an unknown telescope writes nothing
        // (never bake "(unclassified)" into a file forever), and a missing TELESCOP writes
        // nothing because at capture time an empty name may just be an unfilled profile, not
        // evidence of a bare lens (the report keeps that inference at read time).
        if (!string.IsNullOrWhiteSpace(imageMeta.Telescope)
            && OpticalSystems.Classify(imageMeta.Telescope) is not OpticalSystem.Unclassified and var opticalSystem)
        {
            AddHeaderValueIfHasValue("OPTSYS", opticalSystem.Label, "optical system kind, from TELESCOP");
        }
        AddHeaderValueIfHasValue("OBJECT", imageMeta.ObjectName, "");
        AddHeaderValueIfHasValue("ROWORDER", imageMeta.RowOrder, "");
        if (imageMeta.FocalLength > 0)
        {
            AddHeaderValueIfHasValue("FOCALLEN", imageMeta.FocalLength, "mm");
        }
        if (imageMeta.Aperture > 0)
        {
            AddHeaderValueIfHasValue("APTDIA", imageMeta.Aperture, "mm");
        }
        if (!double.IsNaN(imageMeta.DerivedApertureAreaCm2))
        {
            AddHeaderValueIfHasValue("APTAREA", imageMeta.DerivedApertureAreaCm2, "cm^2");
        }
        if (!double.IsNaN(imageMeta.DerivedFRatio))
        {
            AddHeaderValueIfHasValue("FOCRATIO", imageMeta.DerivedFRatio, "f-ratio");
        }
        if (!double.IsNaN(imageMeta.DerivedPixelScale))
        {
            AddHeaderValueIfHasValue("SCALE", imageMeta.DerivedPixelScale, "arcsec/px");
            AddHeaderValueIfHasValue("PIXSCALE", imageMeta.DerivedPixelScale, "arcsec/px");
        }
        if (imageMeta.FocusPos >= 0)
        {
            AddHeaderValueIfHasValue("FOCUSPOS", imageMeta.FocusPos, "steps");
            AddHeaderValueIfHasValue("FOCPOS", imageMeta.FocusPos, "steps");
        }
        // FILTER = full manufacturer name (NINA convention), FILTCLAS = coarse classification
        AddHeaderValueIfHasValue("FILTER", imageMeta.Filter.FilterNameForFits, "");
        AddHeaderValueIfHasValue("FILTCLAS", imageMeta.Filter.Name, "");
        AddHeaderValueIfHasValue("SENSOR", imageMeta.SensorModel, "");
        // Round-trip PIERSIDE in N.I.N.A.'s string convention so other tools
        // recognise it without a numeric-vs-string ambiguity.
        if (imageMeta.PierSide is Devices.PointingState.Normal or Devices.PointingState.ThroughThePole)
        {
            AddHeaderValueIfHasValue("PIERSIDE",
                imageMeta.PierSide == Devices.PointingState.Normal ? "East" : "West",
                "Mount side of pier at exposure time");
        }
        AddHeaderValueIfHasValue("CCD-TEMP", imageMeta.CCDTemperature, "Celsius");
        AddHeaderValueIfHasValue("SET-TEMP", imageMeta.SetCCDTemperature, "Celsius");
        if (imageMeta.Gain >= 0)
        {
            AddHeaderValueIfHasValue("GAIN", (int)imageMeta.Gain, "");
        }
        if (imageMeta.Offset >= 0)
        {
            AddHeaderValueIfHasValue("OFFSET", imageMeta.Offset, "camera offset");
        }
        AddHeaderValueIfHasValue("EGAIN", imageMeta.ElectronsPerADU, "e-/ADU");
        // SATURATE (astrometry.net / SExtractor / PixInsight convention): saturation level in the
        // same units as the stored pixel data. Round-trips ImageMeta.SensorFullScaleAdu; also lets
        // third-party tools reject saturated stars. DATAMAX above stays the OBSERVED frame peak --
        // the two are deliberately different keywords for deliberately different concepts.
        if (imageMeta.SensorFullScaleAdu is { } fullScaleAdu and > 0)
        {
            AddHeaderValueIfHasValue("SATURATE", fullScaleAdu, "[adu] saturation level (sensor full scale)");
        }
        // The MaxIm DL / N.I.N.A. spelling. TianWen wrote the Atik-legacy BAYOFFX/BAYOFFY until
        // 2026-08-17; the reader keeps that as a fallback, the writer does not.
        AddHeaderValueIfHasValue("XBAYROFF", imageMeta.BayerOffsetX, "");
        AddHeaderValueIfHasValue("YBAYROFF", imageMeta.BayerOffsetY, "");
        AddHeaderValueIfHasValue("SITELAT", imageMeta.Latitude, "degrees");
        AddHeaderValueIfHasValue("SITELONG", imageMeta.Longitude, "degrees");
        AddHeaderValueIfHasValue("SITEELEV", imageMeta.SiteElevation, "metres above mean sea level");
        if (imageMeta.ColourCalibration is { } wb)
        {
            AddHeaderValueIfHasValue("WBSOURCE", wb.Source, "How the white balance was derived");
            AddHeaderValueIfHasValue("WBRED", wb.R, "Red white-balance multiplier");
            AddHeaderValueIfHasValue("WBGREEN", wb.G, "Green white-balance multiplier");
            AddHeaderValueIfHasValue("WBBLUE", wb.B, "Blue white-balance multiplier");
        }
        if (imageMeta.Guiding is { } guiding)
        {
            AddHeaderValueIfHasValue("GUIDERMS", guiding.RmsTotal, "arcsec RMS guide error over this exposure");
            AddHeaderValueIfHasValue("GUIRMSRA", guiding.RmsRa, "arcsec RMS guide error in RA");
            AddHeaderValueIfHasValue("GUIRMSDE", guiding.RmsDec, "arcsec RMS guide error in Dec");
            AddHeaderValueIfHasValue("GUIDEPK", guiding.Peak, "arcsec largest excursion in this exposure");
            AddHeaderValueIfHasValue("GUIDEN", guiding.SampleCount, "guide samples behind these statistics");
        }
        if (!double.IsNaN(imageMeta.TargetRA) && !double.IsNaN(imageMeta.TargetDec))
        {
            AddHeaderValueIfHasValue("OBJCTRA", Astrometry.CoordinateUtils.HoursToHMS(imageMeta.TargetRA, ' ', minuteSeparator: ' '), "");
            AddHeaderValueIfHasValue("OBJCTDEC", Astrometry.CoordinateUtils.DegreesToDMS(imageMeta.TargetDec, degreeSign: ' ', arcMinuteSign: ' '), "");
            AddHeaderValueIfHasValue("RA", imageMeta.TargetRA * 15.0, "degrees");
            AddHeaderValueIfHasValue("DEC", imageMeta.TargetDec, "degrees");
        }
        AddHeaderValueIfHasValue("SWCREATE", imageMeta.SWCreator, "");
        // BAYERPAT / COLORTYP only make sense on a single-channel raw CFA
        // image where the pattern is still latent in the pixel layout.
        // A debayered RGB master (channelCount==3) has separate R/G/B
        // planes -- downstream tools that see BAYERPAT will think they
        // need to debayer again and produce double-debayered garbage.
        // Note: XBAYROFF / YBAYROFF above are written unconditionally; for
        // a debayered image they're effectively meaningless metadata but
        // not actively misleading (no tool re-debayers from offsets
        // alone). If we ever propagate the original CFA pattern through
        // the integration result for forensics, gate them on the same
        // channelCount==1 predicate.
        if (imageMeta.SensorType is SensorType.RGGB && channelCount == 1)
        {
            AddHeaderValueIfHasValue("BAYERPAT", "RGGB", "");
            AddHeaderValueIfHasValue("COLORTYP", "RGGB", "");
        }
        if (wcs is { } wcsValue)
        {
            wcsValue.WriteToHeader(basicHdu.Header);
        }

        // Caller-supplied extras. Dispatched per-type because nom.tam.fits's
        // Header.AddValue is overloaded rather than generic and won't accept
        // a boxed object directly.
        if (extraHeaders is not null)
        {
            foreach (var (key, (value, comment)) in extraHeaders)
            {
                switch (value)
                {
                    case int i: basicHdu.Header.AddValue(key, i, comment); break;
                    case long l: basicHdu.Header.AddValue(key, l, comment); break;
                    case float f: basicHdu.Header.AddValue(key, f, comment); break;
                    case double d: basicHdu.Header.AddValue(key, d, comment); break;
                    case bool b: basicHdu.Header.AddValue(key, b, comment); break;
                    case string s: basicHdu.Header.AddValue(key, s, comment); break;
                    default:
                        throw new ArgumentException(
                            $"Unsupported FITS header value type {value?.GetType().Name ?? "null"} for key '{key}'.",
                            nameof(extraHeaders));
                }
            }
        }

        fits.AddHDU(basicHdu);

        using var bufferedWriter = new BufferedFile(fileName, FileAccess.ReadWrite, FileShare.Read, 1000 * 2088);
        fits.Write(bufferedWriter);
        bufferedWriter.Flush();
        bufferedWriter.Close();

        void AddHeaderValueIfHasValue<T>(string key, T value, string comment = "", bool isDataValue = false)
        {
            var card = value switch
            {
                float f when !float.IsNaN(f) => new HeaderCard(key, f, comment),
                float f when isDataValue && dataIsInt => new HeaderCard(key, (int)f, comment),
                double d when !double.IsNaN(d) => new HeaderCard(key, d, comment),
                double d when isDataValue && dataIsInt => new HeaderCard(key, (int)d, comment),
                int i => new HeaderCard(key, i, comment),
                long l => new HeaderCard(key, l, comment),
                string s when !string.IsNullOrWhiteSpace(s) => new HeaderCard(key, s.Length <= 68 ? s : s[..68], comment),
                bool b => new HeaderCard(key, b, comment),
                FrameType ft => new HeaderCard(key, ft.ToFITSValue(), comment),
                RowOrder ro => new HeaderCard(key, ro.ToFITSValue(), comment),
                _ => null
            };

            if (card is not null)
            {
                basicHdu.Header.AddCard(card);
            }
        }
    }
}

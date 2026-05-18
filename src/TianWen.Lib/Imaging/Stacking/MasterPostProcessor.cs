using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Post-integration disk side-effects: MaxValue header fix-up, plate-solve
/// against an in-process catalog DB, focal-length backfill from the solved
/// pixel scale, and the master FITS + autocrop FITS writes. Pulled out of
/// <see cref="StackingPipeline"/> so the orchestrator stays focused on
/// scan/register/integrate. The autocrop variant always uses the supplied
/// intersection rectangle and shifts the WCS CRPIX so the cropped FITS
/// still maps to the same sky coordinates.
/// </summary>
internal sealed class MasterPostProcessor(ILogger logger, ICelestialObjectDB? catalogDb)
{
    /// <summary>
    /// Writes <paramref name="result"/>'s master FITS to <paramref name="masterPath"/>
    /// (with WCS if plate-solve succeeds), plus a sibling <c>_autocrop.fits</c>
    /// when <paramref name="autocropRect"/> is a proper sub-rectangle of the
    /// master. Returns the (possibly updated) <see cref="IntegrationResult"/>
    /// -- focal-length and MaxValue may have been backfilled on the master.
    /// </summary>
    public async Task<(IntegrationResult Result, WCS? SolvedWcs)> WriteMasterAsync(
        IntegrationResult result,
        string masterPath,
        WCS? searchHint,
        ImageDim? imageDim,
        ImageMeta refMeta,
        Rectangle autocropRect,
        IntegrationStrategyKind strategy,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var master = result.Master;

        // 0) Fix the master's MaxValue tag without rescaling pixels. The
        //    integrator inherits MaxValue from the source frames (65535)
        //    but its actual pixel data is already in [0, 1] -- the warp
        //    + debayer pipeline emits normalised floats. The metadata-
        //    vs-data mismatch silently breaks downstream consumers that
        //    key on MaxValue (most importantly Histogram(), which puts
        //    MaxValue=65535 into the "non-rescale" branch and produces
        //    a 59k-bin histogram for pixel data living entirely in
        //    bin 0). Wrap the same data arrays in a new Image record
        //    with MaxValue=1; no pixel mutation.
        if (master.MaxValue > 1.0f + float.Epsilon)
        {
            var data = new float[master.ChannelCount][,];
            for (var c = 0; c < master.ChannelCount; c++)
            {
                data[c] = master.GetChannelArray(c);
            }
            master = new Image(data, BitDepth.Float32, 1.0f, 0f, master.Pedestal, master.ImageMeta);
            result = result with { Master = master };
        }

        // Pre-compute the cropped master once -- reused for the autocrop
        // FITS at the end.
        IntegrationResult? croppedResult = null;
        if (autocropRect.Width > 0 && autocropRect.Height > 0 &&
            (autocropRect.Width < master.Width || autocropRect.Height < master.Height))
        {
            croppedResult = CropIntegrationResult(result, autocropRect);
        }

        // 1) Plate solve against the supplied catalog. The FITS-header
        //    WCS hint tells the solver where to look. When the caller
        //    didn't supply a catalog DB we skip the solve entirely (the
        //    master FITS gets written without WCS).
        WCS? solvedWcs = null;
        if (searchHint is { } hint && catalogDb is { } db)
        {
            try
            {
                var solver = new CatalogPlateSolver(db);
                var psResult = await solver.SolveImageAsync(master, imageDim: imageDim, searchOrigin: hint, cancellationToken: ct);
                if (psResult.Solution is { } w)
                {
                    solvedWcs = w;
                    logger.LogInformation("  [plateSolve] RA={RA:F4}h Dec={Dec:F4}° matched={Matched}/{Detected} ({Ms} ms)",
                        w.CenterRA, w.CenterDec, psResult.MatchedStars, psResult.DetectedStars, psResult.Elapsed.TotalMilliseconds);

                    // Standard formula: focalLen_mm = pixelSize_um * bin
                    // * 206.265 / pixelScale_arcsec. The source-frame
                    // FOCALLEN is whatever the capture software wrote;
                    // the plate-solve pixel scale is empirically what
                    // the optical train actually produced. Stamp the
                    // derived value on the master so the emitted FITS
                    // header carries a focal length consistent with
                    // the embedded WCS.
                    var pxScale = w.PixelScaleArcsec;
                    if (refMeta.PixelSizeX > 0 && refMeta.BinX > 0 && !double.IsNaN(pxScale) && pxScale > 0)
                    {
                        var effectivePxSize = refMeta.PixelSizeX * refMeta.BinX;
                        var derivedFL = CoordinateUtils.FocalLengthMm(effectivePxSize, pxScale);
                        if (!double.IsNaN(derivedFL))
                        {
                            var rounded = (int)Math.Round(derivedFL);
                            var headerFL = master.ImageMeta.FocalLength;
                            if (rounded > 0 && rounded != headerFL)
                            {
                                logger.LogInformation("  [focalLen] header={Header}mm -> solved={Solved}mm", headerFL, rounded);
                                master = WithUpdatedFocalLength(master, rounded);
                                result = result with { Master = master };
                                if (croppedResult is not null)
                                {
                                    croppedResult = croppedResult with { Master = WithUpdatedFocalLength(croppedResult.Master, rounded) };
                                }
                            }
                        }
                    }
                }
                else
                {
                    logger.LogInformation("  [plateSolve] no solution (matched={Matched}/{Detected})",
                        psResult.MatchedStars, psResult.DetectedStars);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("  [plateSolve] failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            }
        }
        else if (searchHint is null)
        {
            logger.LogInformation("  [plateSolve] skipped (no search hint)");
        }
        else
        {
            logger.LogInformation("  [plateSolve] skipped (no catalog DB supplied)");
        }

        // 2) Write the master FITS with WCS baked into the headers.
        IntegrationFitsWriter.Write(masterPath, result, solvedWcs, strategy);
        logger.LogInformation("  wrote {Path}{Wcs}", masterPath, solvedWcs is null ? "" : " (WCS embedded)");

        // 3) Autocrop FITS: same master cropped to the intersection AABB,
        //    WCS CRPIX shifted by the crop offset so plate-solve coords
        //    still map to the same sky position.
        if (croppedResult is not null)
        {
            try
            {
                WCS? croppedWcs = solvedWcs is { } w
                    ? w with { CRPix1 = w.CRPix1 - autocropRect.X, CRPix2 = w.CRPix2 - autocropRect.Y }
                    : null;
                var cropFitsPath = WithSuffix(masterPath, "_autocrop");
                IntegrationFitsWriter.Write(cropFitsPath, croppedResult, croppedWcs, strategy);
                logger.LogInformation("  wrote {Path} (crop {W}x{H})", cropFitsPath, autocropRect.Width, autocropRect.Height);
            }
            catch (Exception ex)
            {
                logger.LogWarning("  [autocrop] failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            }
        }

        logger.LogInformation("  [post] total {Ms} ms", sw.ElapsedMilliseconds);
        return (result, solvedWcs);
    }

    private static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, stem + suffix + ext);
    }

    private static IntegrationResult CropIntegrationResult(IntegrationResult full, Rectangle rect)
    {
        var croppedMaster = CropImage(full.Master, rect);
        var croppedRejection = CropImage(full.RejectionMap, rect);
        return full with { Master = croppedMaster, RejectionMap = croppedRejection };
    }

    private static Image WithUpdatedFocalLength(Image src, int focalLengthMm)
    {
        var data = new float[src.ChannelCount][,];
        for (var c = 0; c < src.ChannelCount; c++)
        {
            data[c] = src.GetChannelArray(c);
        }
        var meta = src.ImageMeta with { FocalLength = focalLengthMm };
        return new Image(data, src.BitDepth, src.MaxValue, src.MinValue, src.Pedestal, meta);
    }

    private static Image CropImage(Image src, Rectangle rect)
    {
        var x0 = Math.Max(0, rect.X);
        var y0 = Math.Max(0, rect.Y);
        var x1 = Math.Min(src.Width,  rect.Right);
        var y1 = Math.Min(src.Height, rect.Bottom);
        var cw = x1 - x0;
        var ch = y1 - y0;
        if (cw <= 0 || ch <= 0)
        {
            throw new ArgumentException(
                $"Crop rect {rect} produces empty image after clamping to {src.Width}x{src.Height}.", nameof(rect));
        }

        var channelCount = src.ChannelCount;
        var data = new float[channelCount][,];
        for (var c = 0; c < channelCount; c++)
        {
            var dst = new float[ch, cw];
            for (var y = 0; y < ch; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    dst[y, x] = src[c, y0 + y, x0 + x];
                }
            }
            data[c] = dst;
        }
        return new Image(data, BitDepth.Float32, src.MaxValue, src.MinValue, src.Pedestal, src.ImageMeta);
    }
}

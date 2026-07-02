using System;
using System.Collections.Immutable;
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
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Return value of <see cref="MasterPostProcessor.WriteMasterAsync"/>:
/// the (possibly updated) <see cref="IntegrationResult"/> after MaxValue
/// + focal-length backfill, the WCS produced by plate-solve (null when
/// no catalog DB was supplied or the solve failed), and the SPCC outcome of
/// the preview render (null when no preview was rendered or SPCC was skipped)
/// so the CLI can print the deterministic <c>[stack] ... SPCC=...</c> summary.
/// Named record rather than a tuple so callers can deconstruct or assert by
/// field name.
/// </summary>
internal readonly record struct MasterWriteResult(IntegrationResult Result, WCS? SolvedWcs, SpccDiagnostics? Spcc);

/// <summary>
/// Post-integration disk side-effects: MaxValue header fix-up, plate-solve
/// against an in-process catalog DB, focal-length backfill from the solved
/// pixel scale, and the master FITS + autocrop FITS writes. Pulled out of
/// <see cref="StackingPipeline"/> so the orchestrator stays focused on
/// scan/register/integrate. The autocrop variant always uses the supplied
/// intersection rectangle and shifts the WCS CRPIX so the cropped FITS
/// still maps to the same sky coordinates.
/// </summary>
internal sealed class MasterPostProcessor(ILogger logger, ICelestialObjectDB? catalogDb, SharpenPipeline? sharpenPipeline = null, IProgress<EnhanceProgress>? enhanceProgress = null)
{
    /// <summary>
    /// Writes <paramref name="result"/>'s master FITS to <paramref name="masterPath"/>
    /// (with WCS if plate-solve succeeds), plus a sibling <c>_autocrop.fits</c>
    /// when <paramref name="autocropRect"/> is a proper sub-rectangle of the
    /// master. When <paramref name="enhance"/> is set and a <see cref="SharpenPipeline"/>
    /// was supplied, also writes <c>_sharpened.fits</c> + (when autocrop is
    /// active) <c>_sharpened_autocrop.fits</c> sibling files; the raw masters
    /// are never replaced. Returns the (possibly updated)
    /// <see cref="IntegrationResult"/> -- focal-length and MaxValue may have
    /// been backfilled on the master.
    /// </summary>
    public async Task<MasterWriteResult> WriteMasterAsync(
        IntegrationResult result,
        string masterPath,
        WCS? searchHint,
        ImageDim? imageDim,
        ImageMeta refMeta,
        Rectangle autocropRect,
        IntegrationStrategyKind strategy,
        bool enhance,
        float enhanceBlend,
        bool splitPlates,
        EnhanceOptions enhanceOptions,
        bool renderPreviewPng,
        MaskedBoostOptions? previewBoost = null,
        CancellationToken ct = default)
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
        //
        //    Prefer the autocrop as the plate-solve input. Drizzle's
        //    per-channel coverage map leaves NaN cells around the canvas
        //    edge of the full master, and Image.Downsample (called by
        //    CatalogPlateSolver when pixel scale < 1.5"/px) propagates a
        //    single NaN to its entire 2x2 block, thickening the NaN ring
        //    and poisoning star detection. The autocrop is the
        //    intersection of all channels' non-NaN regions so it's safe
        //    by construction. Translate WCS back to the full canvas at
        //    the end by adding the crop origin to CRPIX.
        //
        //    Guard: only prefer autocrop when it covers >= 40% of the
        //    full canvas AND its smallest side spans >= 0.3 deg of sky.
        //    Below either threshold the cropped FOV may not overlap
        //    enough of the catalog query region (or simply lacks
        //    candidate stars) to converge. Fall back to the full master
        //    in that case -- the Downsample fix above makes that path
        //    correct for narrow NaN rings, and the more general future
        //    case (heavy crop on short-FOV scopes) still has a path.
        WCS? solvedWcs = null;
        Rectangle solveCrop = default;
        if (searchHint is { } hint && catalogDb is { } db)
        {
            try
            {
                // Idempotent re-entry: the CLI fires InitDBAsync at the
                // start of the stack run so Tycho-2 bulk decode overlaps
                // with scan/register/integrate. By the time we get here
                // it's usually already done (this await returns instantly
                // via the _isInitialized fast path); otherwise we block
                // just long enough for the background task to land. Also
                // observes any exception from the fire-and-forget kick-
                // off so init failures surface here instead of becoming
                // unobserved-task crashes.
                await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);

                var solver = new CatalogPlateSolver(db, logger);
                Image solveImage = master;
                ImageDim? solveImageDim = imageDim;
                if (croppedResult is not null && PreferAutocropForPlateSolve(autocropRect, master, imageDim))
                {
                    solveImage = croppedResult.Master;
                    solveCrop = autocropRect;
                    solveImageDim = imageDim is { } dim
                        ? new ImageDim(dim.PixelScale, autocropRect.Width, autocropRect.Height)
                        : null;
                    logger.LogInformation("  [plateSolve] using autocrop ({W}x{H}) as solver input",
                        autocropRect.Width, autocropRect.Height);
                }
                var psResult = await solver.SolveImageAsync(solveImage, imageDim: solveImageDim, searchOrigin: hint, cancellationToken: ct);
                if (psResult.Solution is { } w)
                {
                    // Shift CRPIX back to the full-canvas coordinate
                    // space when the solver ran on the autocrop. The
                    // autocrop's CRPIX is offset by (autocropRect.X,
                    // autocropRect.Y) from the master's pixel grid;
                    // adding the offset puts the reference pixel back
                    // where the full master expects it.
                    solvedWcs = solveCrop is { Width: > 0 }
                        ? w with { CRPix1 = w.CRPix1 + solveCrop.X, CRPix2 = w.CRPix2 + solveCrop.Y }
                        : w;
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

        // The preview PNG + the --split-plates TIFFs are display-side outputs that
        // both need ONE SPCC + bg-neut solve (MasterPreviewRenderer). Build it once
        // when either is requested. croppedWcs (autocrop CRPIX shifted by the crop
        // origin) is shared by the autocrop FITS write, the preview, and the plates.
        var renderer = (renderPreviewPng || splitPlates) ? new MasterPreviewRenderer(catalogDb, logger) : null;
        WCS? croppedWcs = solvedWcs is { } cw && croppedResult is not null
            ? cw with { CRPix1 = cw.CRPix1 - autocropRect.X, CRPix2 = cw.CRPix2 - autocropRect.Y }
            : null;
        SpccDiagnostics? spcc = null;

        // 2.5) AI enhancement: BlurX-first / SAS-shaped pipeline on the master ->
        //      _sharpened.fits (+ _sharpened_autocrop.fits). The raw masters are never
        //      overwritten. When enhancing, ONE SPCC solve is computed on the enhanced
        //      (gradient-corrected, with-stars) master -- matching the PixInsight OSC flow
        //      (gradient correction -> SPCC once, stars in) -- and that single white
        //      balance renders the preview PNG AND stretches the --split-plates stars /
        //      starless TIFFs, so all three share the one colour calibration.
        if (enhance && sharpenPipeline is not null)
        {
            spcc = await EnhanceAndWriteAsync(
                result, masterPath, solvedWcs, croppedWcs, strategy,
                croppedResult, autocropRect, enhanceBlend, splitPlates, enhanceOptions,
                refMeta, renderer, renderPreviewPng, previewBoost, ct);
        }
        else if (enhance && sharpenPipeline is null)
        {
            logger.LogWarning("  [enhance] requested but SharpenPipeline not registered; skipping");
        }

        // 3) Autocrop FITS: same master cropped to the intersection AABB,
        //    WCS CRPIX shifted by the crop offset so plate-solve coords
        //    still map to the same sky position.
        if (croppedResult is not null)
        {
            try
            {
                var cropFitsPath = WithSuffix(masterPath, "_autocrop");
                IntegrationFitsWriter.Write(cropFitsPath, croppedResult, croppedWcs, strategy);
                logger.LogInformation("  wrote {Path} (crop {W}x{H})", cropFitsPath, autocropRect.Width, autocropRect.Height);
            }
            catch (Exception ex)
            {
                logger.LogWarning("  [autocrop] failed: {Type}: {Msg}", ex.GetType().Name, ex.Message);
            }
        }

        // 3.5) Raw-master preview PNG -- only when NOT enhancing (the enhance path renders
        //      its own preview from the enhanced master's SPCC solve above). The raw master
        //      is its own stats source here (no gradient correction available pre-enhance).
        if (!enhance && renderPreviewPng && renderer is not null)
        {
            spcc = await RenderPreviewAsync(
                renderer, master, croppedResult?.Master, refMeta, solvedWcs, croppedWcs,
                masterPath, autocropRect, previewBoost, ct);
        }

        logger.LogInformation("  [post] total {Ms} ms", sw.ElapsedMilliseconds);
        return new MasterWriteResult(result, solvedWcs, spcc);
    }

    /// <summary>
    /// Gate for routing the plate-solve through the autocrop instead of
    /// the full master. Autocrop is the intersection of all channels'
    /// non-NaN regions, so it sidesteps NaN-propagation hazards in
    /// <see cref="Image.Downsample"/> and <see cref="Image.Background"/>.
    /// We only take that path when the cropped image still has enough
    /// sky to reasonably converge:
    /// <list type="bullet">
    ///   <item>Area &gt;= 40% of full canvas -- below this the crop is
    ///   "brutal" enough that we may not have enough overlap with the
    ///   catalog query window (which is sized to the full FOV).</item>
    ///   <item>Smallest cropped side spans &gt;= 0.3 deg of sky -- on a
    ///   narrow-FOV scope (e.g. C8 @ 2032mm) a 40% area crop could still
    ///   collapse to a sub-tenth-degree slice. Below ~0.3 deg the
    ///   Tycho-2 catalog within the crop has too few stars for the
    ///   brightness-rank match to be stable.</item>
    /// </list>
    /// Either threshold failing returns the full master as the plate-
    /// solve input. The Downsample NaN-aware fix makes that path work
    /// for typical narrow-ring drizzle masters; broader pathologies
    /// (heavy short-FOV crop) accept the catalog overlap risk.
    /// </summary>
    private static bool PreferAutocropForPlateSolve(Rectangle crop, Image master, ImageDim? imageDim)
    {
        const double MinAreaRatio = 0.40;
        const double MinSideDeg = 0.30;
        const double ArcSecPerDeg = 3600.0;

        if (crop.Width <= 0 || crop.Height <= 0) return false;

        var fullArea = (long)master.Width * master.Height;
        var cropArea = (long)crop.Width * crop.Height;
        if (fullArea <= 0 || (double)cropArea / fullArea < MinAreaRatio) return false;

        if (imageDim is { PixelScale: var pxScale and > 0 })
        {
            var minSideArcsec = pxScale * Math.Min(crop.Width, crop.Height);
            if (minSideArcsec / ArcSecPerDeg < MinSideDeg) return false;
        }

        return true;
    }

    private static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, stem + suffix + ext);
    }

    /// <summary>
    /// Runs the canonical AI enhancement pipeline against the master and
    /// writes the sharpened sibling FITS files. Cropping the enhanced master
    /// to <paramref name="autocropRect"/> reuses the same single forward pass
    /// for the autocrop variant; the raw master FITS (already on disk) is
    /// untouched. Then computes ONE SPCC + bg-neut solve on the enhanced
    /// (gradient-corrected, with-stars) master -- the PixInsight OSC order
    /// (gradient correction, then a single SPCC with stars in) -- and uses that
    /// one white balance to render the preview PNG and (with
    /// <paramref name="splitPlates"/>) stretch the stars / starless TIFFs, so
    /// all three share the calibration and the plates Screen-blend back to the
    /// preview. Failures log + return without throwing so a misbehaving model
    /// never breaks the canonical stacking output.
    /// </summary>
    private async Task<SpccDiagnostics?> EnhanceAndWriteAsync(
        IntegrationResult master,
        string masterPath,
        WCS? solvedWcs,
        WCS? croppedWcs,
        IntegrationStrategyKind strategy,
        IntegrationResult? croppedResult,
        Rectangle autocropRect,
        float enhanceBlend,
        bool splitPlates,
        EnhanceOptions enhanceOptions,
        ImageMeta refMeta,
        MasterPreviewRenderer? renderer,
        bool renderPreviewPng,
        MaskedBoostOptions? previewBoost,
        CancellationToken ct)
    {
        Debug.Assert(sharpenPipeline is not null, "EnhanceAndWriteAsync called without SharpenPipeline -- guard upstream");
        var sw = Stopwatch.StartNew();

        // Reclaim the integration working set before enhancing. The input-frame
        // arrays (held by the InRam sink) are dead by now but linger on the LOH.
        // The AI enhancers run on DirectML, and on an INTEGRATED GPU (e.g. the
        // Snapdragon Adreno on win-arm64) system RAM IS VRAM -- a bloated managed
        // heap starves GPU allocations and triggers a device-removed / TDR
        // (887A0007). This bites hardest with the RC-Astro backend: it's a separate
        // process that re-hydrates its OWN copy of the image + ONNX/GPU staging on
        // top of our heap. A one-shot compacting collect decommits the dead frames
        // back to the OS so the unified memory is available. Gated to the enhance
        // path, so the normal stack loop never pays for it.
        var heapBeforeGB = GC.GetTotalMemory(forceFullCollection: false) / 1e9;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        logger.LogDebug("  [enhance] reclaimed heap before enhance: {Before:F2} GB -> {After:F2} GB",
            heapBeforeGB, GC.GetTotalMemory(forceFullCollection: false) / 1e9);

        try
        {
            // Linear-in / linear-out, with the per-step Blend exposed via
            // --enhance-blend. GhsStretch / dual-stretch are deliberately NOT
            // included -- a stacked master stays in linear photon-space so
            // downstream PixInsight / Affinity / tianwen-render workflows apply
            // their own stretch. Two shapes:
            //  - BlurX-first (RC-Astro present): full-image deblur tightens stars
            //    AND nebula, so NO stellar-sharpen step; stars get SCNR, the
            //    starless plate gets denoise. Matches the PixInsight OSC flow.
            //  - SAS-shaped (no RC deblurrer): remove stars, sharpen the stars
            //    plate, deconvolve + denoise the starless plate.
            var blend = Math.Clamp(enhanceBlend, 0f, 1f);
            var steps = sharpenPipeline.SupportsDeblur
                ? ImmutableArray.Create<SharpenStep>(
                    new DeblurStep(Blend: blend),
                    new GradientCorrectionStep(),
                    new RemoveStarsStep(),
                    new DenoiseStarlessStep(Blend: blend),
                    new ScnrStarsStep(ScnrMode.Average),
                    new RecombineStep())
                : ImmutableArray.Create<SharpenStep>(
                    new GradientCorrectionStep(),
                    new RemoveStarsStep(),
                    new SharpenStarsStep(Blend: blend),
                    new DeconvolveStarlessStep(Blend: blend),
                    new DenoiseStarlessStep(Blend: blend),
                    new RecombineStep());
            // --split-plates keeps the stars / starless lineage so the SAME pass
            // feeds the per-plate TIFF export; otherwise discard intermediates.
            var request = new SharpenRequest(master.Master, steps,
                KeepIntermediates: splitPlates ? SharpenIntermediates.StarsAndStarlessLineage : SharpenIntermediates.None);
            var sharpenResult = await sharpenPipeline.ProcessAsync(request, enhanceOptions, enhanceProgress, ct);
            if (sharpenResult.Final is not { } enhancedMaster)
            {
                logger.LogWarning("  [enhance] SharpenPipeline returned no Final image; skipping write");
                return null;
            }

            // Reuse the original IntegrationResult shell (FrameCount, RejectionMap,
            // MeanRejectionRate) so IntegrationFitsWriter keeps the same provenance
            // headers; just swap Master for the enhanced pixels.
            var sharpenedPath = WithSuffix(masterPath, "_sharpened");
            IntegrationFitsWriter.Write(sharpenedPath, master with { Master = enhancedMaster }, solvedWcs, strategy);
            logger.LogInformation("  wrote {Path} (enhance blend={Blend:F2}, {Ms} ms)", sharpenedPath, blend, sw.ElapsedMilliseconds);

            Image? enhancedCropped = null;
            if (croppedResult is not null)
            {
                enhancedCropped = CropImage(enhancedMaster, autocropRect);
                var sharpenedCropPath = WithSuffix(masterPath, "_sharpened_autocrop");
                IntegrationFitsWriter.Write(sharpenedCropPath, croppedResult with { Master = enhancedCropped }, croppedWcs, strategy);
                logger.LogInformation("  wrote {Path} (crop {W}x{H})", sharpenedCropPath, autocropRect.Width, autocropRect.Height);
            }

            // ONE SPCC + bg-neut solve on the ENHANCED master (gradient-corrected, with
            // stars) -- the PixInsight OSC order: gradient correction, then a single SPCC
            // with the stars in. That single white balance renders the preview PNG AND
            // stretches the --split-plates stars / starless TIFFs, so all three share the
            // calibration and the plates Screen-blend back to the preview. The stars-only
            // + denoised-starless plates are the kept lineage (StarsAndStarlessLineage
            // above) -- NO second AI pass.
            SpccDiagnostics? spcc = null;
            if (renderer is not null && (renderPreviewPng || splitPlates))
            {
                var solveImg = enhancedCropped ?? enhancedMaster;
                var solveWcs = enhancedCropped is not null ? croppedWcs : solvedWcs;
                var pngPath = renderPreviewPng
                    ? (enhancedCropped is not null
                        ? Path.ChangeExtension(WithSuffix(masterPath, "_autocrop"), ".png")
                        : Path.ChangeExtension(masterPath, ".png"))
                    : string.Empty;   // solve-only: the renderer skips the PNG write on an empty path
                var render = await renderer.RenderAsync(
                    solveImg, refMeta, solveWcs, statsSource: solveImg, pngPath, statsWcs: solveWcs,
                    maskedBoost: previewBoost, ct: ct);
                spcc = render.Spcc;

                if (splitPlates)
                {
                    // Each plate self-stretches from its own pixels and shares ONLY the
                    // master's one SPCC white balance (render.WhiteBalance) -- the
                    // PixInsight order: WB once, then a per-plate stretch. Sharing the
                    // master's full uniforms (render.Uniforms) would graft the master's
                    // bg-neut onto a plate whose background differs and tint it.
                    var stars = sharpenResult.SharpenedStars ?? sharpenResult.StarsOnly;
                    var starless = sharpenResult.DenoisedStarless ?? sharpenResult.DeconvolvedStarless ?? sharpenResult.Starless;
                    var doCrop = croppedResult is not null;
                    if (stars is not null)
                    {
                        await RenderPlateTiffAsync(renderer, stars, render.WhiteBalance, WithSuffix(masterPath, "_stars"), doCrop, autocropRect, ct);
                    }
                    if (starless is not null)
                    {
                        await RenderPlateTiffAsync(renderer, starless, render.WhiteBalance, WithSuffix(masterPath, "_starless"), doCrop, autocropRect, ct);
                    }
                }
            }

            enhancedCropped?.Release();
            enhancedMaster.Release();
            if (splitPlates)
            {
                sharpenResult.Starless?.Release();
                sharpenResult.StarsOnly?.Release();
                sharpenResult.SharpenedStars?.Release();
                sharpenResult.DeconvolvedStarless?.Release();
                sharpenResult.DenoisedStarless?.Release();
            }
            return spcc;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("  [enhance] cancelled after {Ms} ms", sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("  [enhance] failed after {Ms} ms: {Type}: {Msg}", sw.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Renders the preview PNG from the RAW master -- always the autocrop (NaN-ring-free)
    /// unless coverage is full and there is no <c>_autocrop.fits</c>, in which case the
    /// bare master PNG is it. The render image IS its own stats source (raw stats + raw
    /// render): this is the only solve combination that is colour-neutral for BOTH the
    /// near-zero-background drizzle masters and the high-pedestal non-drizzle masters.
    /// (The split plates are rendered separately from the enhanced master in
    /// <see cref="EnhanceAndWriteAsync"/>.) Returns the SPCC diagnostics for the CLI
    /// summary.
    /// </summary>
    private async Task<SpccDiagnostics?> RenderPreviewAsync(
        MasterPreviewRenderer renderer,
        Image fullMaster, Image? cropMaster, ImageMeta sensorMeta,
        WCS? fullWcs, WCS? cropWcs, string masterPath, Rectangle autocropRect,
        MaskedBoostOptions? previewBoost, CancellationToken ct)
    {
        var previewImg = cropMaster ?? fullMaster;
        var previewWcs = cropMaster is not null ? cropWcs : fullWcs;
        var pngPath = cropMaster is not null
            ? Path.ChangeExtension(WithSuffix(masterPath, "_autocrop"), ".png")
            : Path.ChangeExtension(masterPath, ".png");

        var render = await renderer.RenderAsync(
            previewImg, sensorMeta, previewWcs, statsSource: previewImg, pngPath, statsWcs: previewWcs,
            maskedBoost: previewBoost, ct: ct);
        return render.Spcc;
    }

    /// <summary>Crop the plate to the autocrop AABB (so it matches the preview's stats
    /// region) then write its stretched float TIFF -- the plate self-stretches from its own
    /// pixels and shares only <paramref name="sharedWb"/> (the master's SPCC white balance).
    /// The input <paramref name="plate"/> is owned by the caller; only the crop copy is
    /// released here.</summary>
    private async Task RenderPlateTiffAsync(
        MasterPreviewRenderer renderer, Image plate, (float R, float G, float B)? sharedWb,
        string fitsBasePath, bool doCrop, Rectangle autocropRect, CancellationToken ct)
    {
        var src = doCrop ? CropImage(plate, autocropRect) : plate;
        try
        {
            await renderer.RenderStretchedPlateTiffAsync(src, sharedWb, Path.ChangeExtension(fitsBasePath, ".tif"), ct);
        }
        finally
        {
            if (doCrop) src.Release(); // CropImage allocated a copy; the original plate is released by the caller
        }
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

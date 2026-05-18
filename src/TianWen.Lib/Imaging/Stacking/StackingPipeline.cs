using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Library-side end-to-end stacking orchestrator. Walks a folder of raw
/// FITS lights + calibration frames, builds bias/dark/flat masters,
/// registers + integrates each light group, plate-solves the master, and
/// writes one <c>master_&lt;group&gt;.fits</c> (+ an autocrop variant) per
/// group with WCS embedded.
///
/// <para>What the pipeline does NOT do (deliberate layering -- these need
/// UI.Abstractions for SPCC + stretch math):</para>
/// <list type="bullet">
///   <item>White balance (SPCC or sky-bg fallback).</item>
///   <item>Background neutralisation gain solve.</item>
///   <item>PNG preview render with stretch.</item>
/// </list>
/// <para>Callers (CLI, manual test, future TUI) get one
/// <see cref="GroupResult"/> per integrated group via the streaming
/// <see cref="IAsyncEnumerable{T}"/> and can run their own display
/// pipeline on the emitted master.</para>
/// </summary>
/// <param name="options">Per-run inputs.</param>
/// <param name="logger">Receives human-readable progress + diagnostic
/// lines (the same lines the manual test used to mirror to
/// <c>stack-run.log</c>).</param>
/// <param name="catalogDb">Optional celestial-object DB for plate-solving
/// the integrated master. The caller owns the DB lifecycle (tests share
/// one process-wide; the CLI initialises once at startup). When null,
/// plate-solve is skipped and the FITS is written without a WCS.</param>
/// <param name="progress">Optional structured progress sink. The pipeline
/// emits a tick per phase transition + once per integrated frame /
/// strip via the strategy's own progress callback.</param>
public sealed class StackingPipeline(
    StackingOptions options,
    ILogger logger,
    ICelestialObjectDB? catalogDb = null,
    IProgress<StackingProgress>? progress = null)
{
    /// <summary>Ladder of quadTolerance values to try per frame, ascending.
    /// First-match wins. The lower rungs (0.008, 0.02, 0.05) are tuned for the
    /// all-stars quad path where fingerprints are dense and small drift only
    /// nudges Dist1/ratios fractionally. The top-K path (see
    /// <see cref="StackingOptions.QuadStars"/>) has 20x fewer quads and a
    /// much sparser signature space, so cross-flip frames typically match
    /// at qt=0.1-0.2. The 0.5 ceiling is the runaway guard: false-positive
    /// cross-object pairs are still rejected by the affine validator +
    /// RANSAC min-inlier=4 even at this tolerance.</summary>
    private static readonly float[] QuadTolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    /// <summary>Min stars for a stable quad-invariant fit. Matches the
    /// matcher's internal minStars/4=6 quad-correspondence floor with
    /// headroom.</summary>
    private const int MinStarsForMatch = 24;

    /// <summary>
    /// Picks a pixel rejector for the integration step based on frame
    /// count. Defaults from the manual test against the SoL dataset:
    /// LFC for small N (best per-iteration quality, ~8x slower than
    /// sigma at large N), Winsorized for medium, asymmetric SigmaClip
    /// (low=3, high=5) for large N (speed wins; high-kappa keeps stars).
    /// </summary>
    public static IPixelRejector? BuildRejector(int frameCount) => frameCount switch
    {
        < 5  => null,
        < 30 => new LinearFitClipRejector(LowSigma: 3f, HighSigma: 3f, MaxIterations: 5),
        < 60 => new WinsorizedSigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5),
        _    => new SigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5),
    };

    /// <summary>
    /// Run the pipeline, yielding one <see cref="GroupResult"/> per
    /// light group as it finishes. Groups stream in
    /// <see cref="LightGroupKey"/> order; an empty enumerable means no
    /// light groups passed the include/exclude filter (or no lights were
    /// found at all). A group can yield a result with non-empty
    /// <see cref="GroupResult.SkipReason"/> if it failed to register two
    /// or more frames or had no usable reference.
    /// </summary>
    public async IAsyncEnumerable<GroupResult> RunAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var outputDir = options.OutputDir;
        var mastersDir = Path.Combine(outputDir, "masters");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(mastersDir);
        // Wipe stale per-group output FITS from a previous run, but ONLY
        // files this writer produced (SWCREATE header check). Leaves
        // unrelated FITS a user may have parked in outputDir untouched.
        // The masters/ calibration cache is preserved here too -- cal
        // masters are pure functions of their inputs and expensive to
        // rebuild, so we keep them across runs.
        var wipedCount = 0;
        var skippedCount = 0;
        foreach (var f in Directory.EnumerateFiles(outputDir, "*.fits"))
        {
            if (IntegrationFitsWriter.IsTianWenMaster(f))
            {
                try { File.Delete(f); wipedCount++; }
                catch (IOException ex) { logger.LogWarning("  [wipe] failed to delete {Path}: {Msg}", f, ex.Message); }
            }
            else
            {
                skippedCount++;
            }
        }
        if (wipedCount > 0 || skippedCount > 0)
        {
            logger.LogInformation("[wipe] removed {Wiped} stale master(s); kept {Skipped} unrelated FITS in {Dir}",
                wipedCount, skippedCount, outputDir);
        }
        // Stale _staging from a previous run that died mid-group can
        // balloon to multiple GB per group and fill the disk on re-run.
        var stagingRoot = Path.Combine(outputDir, "_staging");
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch { /* best-effort cleanup; per-group code surfaces if it still fails */ }
        }

        logger.LogInformation("[start] data={DataRoot} out={OutputDir}", options.DataRoot, outputDir);

        // -----------------------------------------------------------------
        // 1) Enumerate ALL FITS recursively + group by frame type
        // -----------------------------------------------------------------
        progress?.Report(new StackingProgress(StackingPhase.Scanning, "", 0, 0));
        var sw = Stopwatch.StartNew();
        var source = new FitsFolderFrameSource(options.DataRoot, recursive: true);
        var allFrames = new List<FrameInfo>();
        var outputDirNormalised = Path.GetFullPath(outputDir);
        await foreach (var frame in source.EnumerateAsync(ct))
        {
            // Skip anything under outputDir -- masters and previous-run
            // outputs would otherwise be ingested as fresh lights.
            // Path.GetFullPath normalises separators / case so the
            // StartsWith check is reliable on Windows.
            if (Path.GetFullPath(frame.Path).StartsWith(outputDirNormalised, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            allFrames.Add(frame);
        }
        logger.LogInformation("[scan] {Count} frames in {ElapsedMs} ms", allFrames.Count, sw.ElapsedMilliseconds);

        var byType = allFrames.GroupBy(f => f.FrameType).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (type, frames) in byType)
        {
            logger.LogInformation("  {Type}: {Count} frames", type, frames.Count);
        }

        // -----------------------------------------------------------------
        // 2) Build calibration masters per group
        // -----------------------------------------------------------------
        progress?.Report(new StackingProgress(StackingPhase.BuildingMasters, "", 0, 0));
        sw.Restart();
        var biasMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Bias), MasterFrameBuilder.BuildBiasMasterAsync, mastersDir, ct);
        var darkMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Dark), MasterFrameBuilder.BuildDarkMasterAsync, mastersDir, ct);
        var flatMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Flat), MasterFrameBuilder.BuildFlatMasterAsync, mastersDir, ct);
        logger.LogInformation("[masters] {Bias} bias, {Dark} dark, {Flat} flat ready in {ElapsedMs} ms",
            biasMasters.Count, darkMasters.Count, flatMasters.Count, sw.ElapsedMilliseconds);

        // -----------------------------------------------------------------
        // 3) For each lights group, run the integration pipeline
        // -----------------------------------------------------------------
        if (!byType.TryGetValue(FrameType.Light, out var lights) || lights.Count == 0)
        {
            logger.LogInformation("[lights] none found; nothing to integrate");
            yield break;
        }

        // Light grouping uses LightGroupKey = (calibration signature + OBJECT
        // header). NINA writes every target's lights into one LIGHT/ folder,
        // so a 288-frame session can mix two targets imaged in the same
        // night. Frames of different targets look at different sky and
        // never register against each other -- they must end up in
        // separate groups.
        var lightGroups = lights.GroupBy(LightGroupKey.FromFrame).ToList();
        logger.LogInformation("[lights] {Count} lights in {Groups} group(s)", lights.Count, lightGroups.Count);

        if (options.GroupExclude.Length > 0)
        {
            var beforeCount = lightGroups.Count;
            lightGroups = lightGroups.Where(g => !g.Key.Slug().Contains(options.GroupExclude, StringComparison.OrdinalIgnoreCase)).ToList();
            logger.LogInformation("[filter] {Before} group(s) -> {After} after excluding '{Exclude}'",
                beforeCount, lightGroups.Count, options.GroupExclude);
        }
        if (options.GroupFilter.Length > 0)
        {
            var beforeCount = lightGroups.Count;
            lightGroups = lightGroups.Where(g => g.Key.Slug().Contains(options.GroupFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            logger.LogInformation("[filter] {Before} group(s) -> {After} after filter '{Filter}'",
                beforeCount, lightGroups.Count, options.GroupFilter);
        }

        // Expand each LightGroupKey-keyed group into one or more
        // (key, slug, frames) sub-groups. The default path (no pier-side
        // split) yields a single sub-group per LightGroupKey using the
        // canonical slug. With SplitByPierSide=true, each LightGroupKey
        // explodes into up to three sub-groups (East / West / Unknown),
        // each with its own slug suffix and frame list.
        var subGroups = new List<(LightGroupKey Key, string Slug, List<FrameInfo> Frames)>();
        foreach (var lightGroup in lightGroups)
        {
            var baseSlug = lightGroup.Key.Slug();
            if (!options.SplitByPierSide)
            {
                subGroups.Add((lightGroup.Key, baseSlug, lightGroup.ToList()));
                continue;
            }
            // Partition by pier side. Frames with PointingState.Unknown go
            // into their own bucket rather than silently merging with East --
            // a flipped capture without PIERSIDE in the header would
            // otherwise pollute the East master.
            foreach (var pierGroup in lightGroup.GroupBy(f => f.Meta.PierSide))
            {
                var pierTag = pierGroup.Key switch
                {
                    Devices.PointingState.Normal => "pierE",
                    Devices.PointingState.ThroughThePole => "pierW",
                    _ => "pierUnknown",
                };
                subGroups.Add((lightGroup.Key, $"{baseSlug}_{pierTag}", pierGroup.ToList()));
            }
        }
        if (options.SplitByPierSide)
        {
            logger.LogInformation("[lights] pier-side split: {Groups} -> {SubGroups} sub-group(s)",
                lightGroups.Count, subGroups.Count);
        }

        foreach (var (key, slug, frames) in subGroups)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProcessLightGroupAsync(
                key, slug, frames, darkMasters, flatMasters, outputDir, ct);
            yield return result;
        }

        logger.LogInformation("[end]");
    }

    // =====================================================================
    // Per-group orchestration
    // =====================================================================

    private async Task<GroupResult> ProcessLightGroupAsync(
        LightGroupKey key,
        string slug,
        List<FrameInfo> lightList,
        List<(MasterGroupKey Key, Image Master)> darkMasters,
        List<(MasterGroupKey Key, Image Master)> flatMasters,
        string outputDir,
        CancellationToken ct)
    {
        // slug carries any pier-side / future sub-group suffix on top of
        // key.Slug() -- it's the canonical name for filenames + logs in this
        // method. Tied to a single group, so we capture it once up top.
        var calKey = key.CalibrationKey;
        var groupSw = Stopwatch.StartNew();
        logger.LogInformation("=== Light group: {Slug} ({Count} frames) ===", slug, lightList.Count);

        // Calibration path: bias is intentionally NOT passed to the
        // Calibrator. The master dark was built from raw darks (no bias
        // pre-subtraction), so its bias signal is already baked in --
        // subtracting both bias AND dark would double-subtract the bias
        // pedestal. Matched-exposure stacking works cleanly with
        // light - dark - flat alone.
        var (dark, darkKey) = MatchMaster(darkMasters, calKey);
        var (flat, flatKey) = MatchMaster(flatMasters, calKey);
        logger.LogInformation("  dark master: {Dark}", darkKey is null ? "NONE" : darkKey.Slug());
        logger.LogInformation("  flat master: {Flat}", flatKey is null ? "NONE" : flatKey.Slug());
        // Build hot-pixel mask from the dark master (one-time cost per
        // group, ~tens of ms even on full-frame). Pixels above
        // sigma * 1.4826 * MAD over the channel median get NaN'd in the
        // calibrated light frames so downstream integration ignores them.
        // Disabled when no dark is matched (no dark = no hot-pixel
        // detection possible) or when HotPixelSigma <= 0.
        BitMatrix[]? badPixelMask = null;
        if (dark is not null && options.HotPixelSigma > 0f)
        {
            badPixelMask = BadPixelDetection.BuildMaskFromDark(dark, options.HotPixelSigma);
            var maskedCount = BadPixelDetection.CountMaskedPixels(badPixelMask, dark.Width, dark.Height);
            logger.LogInformation("  hot-pixel mask: {Count} px flagged at {Sigma:F1} sigma",
                maskedCount, options.HotPixelSigma);
        }
        var calibrator = new Calibrator(Bias: null, Dark: dark, Flat: flat, Pedestal: 0f, BadPixelMask: badPixelMask);

        // 3a. Pick reference (highest star count). We bypass
        // Registrator.PickReferenceAsync because it operates on the raw
        // FrameInfo without debayer awareness.
        progress?.Report(new StackingProgress(StackingPhase.Registering, slug, 0, lightList.Count));
        var sw = Stopwatch.StartNew();
        var frameStarCounts = new List<(FrameInfo Frame, int StarCount)>(lightList.Count);
        FrameInfo? reference = null;
        var bestCount = -1;
        foreach (var lf in lightList)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await lf.LoadFullAsync(ct);
            var calibrated = calibrator.Apply(raw);
            var debayered = await calibrated.DebayerAsync(options.CentroidDebayerAlg, cancellationToken: ct);
            var stars = await debayered.FindStarsAsync(channel: 0, snrMin: options.SnrMin, minStars: options.MinStars, cancellationToken: ct);
            frameStarCounts.Add((lf, stars.Count));
            if (stars.Count > bestCount)
            {
                bestCount = stars.Count;
                reference = lf;
            }
        }
        if (reference is null)
        {
            logger.LogWarning("  [skip] no reference frame could be picked");
            return new GroupResult(slug, lightList.Count, 0, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: "no reference frame could be picked");
        }
        logger.LogInformation("  reference: {File} ({Stars} stars, {ElapsedMs} ms)",
            Path.GetFileName(reference.Path),
            frameStarCounts.First(s => s.Frame.Path == reference.Path).StarCount,
            sw.ElapsedMilliseconds);

        // Pre-load + calibrate + debayer reference once; detect ref stars
        // once and wrap in SortedStarList so the per-frame matcher reuses
        // the cached quad list.
        var referenceRaw = await reference.LoadFullAsync(ct);
        // Grab the FITS header WCS as a plate-solve search hint. N.I.N.A.
        // captures usually stamp approximate RA/DEC keywords; we pass
        // these to CatalogPlateSolver so it knows where to look.
        WCS? searchHint = null;
        if (!Image.TryReadFitsFile(reference.Path, out _, out searchHint))
        {
            logger.LogWarning("  [warn] couldn't reread ref FITS for WCS hint: {Path}", reference.Path);
        }
        var referenceDebayered = await calibrator.Apply(referenceRaw).DebayerAsync(options.CentroidDebayerAlg, cancellationToken: ct);
        var referenceStars = await referenceDebayered.FindStarsAsync(channel: 0, snrMin: options.SnrMin, minStars: options.MinStars, cancellationToken: ct);
        var referenceSorted = new SortedStarList(referenceStars);
        var referenceQuads = await referenceSorted.FindQuadsAsync(maxStars: options.QuadStars, ct);

        // Per-group staging dir. Cleaned up by the chosen strategy.
        var stagingDir = Path.Combine(outputDir, "_staging", slug);
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        // 3b. Per-light: calibrate + debayer + register against
        // pre-debayered reference + warp 3-channel RGB to ref grid.
        var calibratedFrameBytes = (long)referenceRaw.Width * referenceRaw.Height * sizeof(float);
        var calibratedCache = new FrameCache(
            lightList.Count,
            FrameCache.DecideCacheCap(lightList.Count, calibratedFrameBytes));
        var matched = new List<(FrameInfo Light, Matrix3x2 Transform)>();
        var skipCount = 0;
        sw.Restart();
        foreach (var lightInfo in lightList)
        {
            ct.ThrowIfCancellationRequested();
            var lightRaw = await lightInfo.LoadFullAsync(ct);
            var calibrated = calibrator.Apply(lightRaw);
            var debayered = await calibrated.DebayerAsync(options.CentroidDebayerAlg, cancellationToken: ct);
            var name = Path.GetFileNameWithoutExtension(lightInfo.Path);

            Matrix3x2? transform;
            if (string.Equals(lightInfo.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
            {
                transform = Matrix3x2.Identity;
            }
            else
            {
                var stars = await debayered.FindStarsAsync(channel: 0, snrMin: options.SnrMin, minStars: options.MinStars, cancellationToken: ct);
                if (stars.Count < MinStarsForMatch)
                {
                    transform = null;
                    logger.LogInformation("  [{Name}] stars={Stars} -> SKIP (too few stars)", name, stars.Count);
                }
                else
                {
                    using var lightSorted = new SortedStarList(stars);
                    _ = await lightSorted.FindQuadsAsync(maxStars: options.QuadStars, ct);
                    var (solution, tolUsed, _) = await TryMatchAsync(lightSorted, referenceSorted, options.QuadStars);
                    transform = solution;
                    if (transform is null)
                    {
                        logger.LogInformation("  [{Name}] stars={Stars} -> SKIP (no quad fit at any tolerance)", name, stars.Count);
                    }
                    else
                    {
                        logger.LogInformation("  [{Name}] stars={Stars} -> MATCH qt={Tol:F3}", name, stars.Count, tolUsed);
                    }
                }
            }
            if (transform is null) { skipCount++; continue; }

            calibratedCache.Set(matched.Count, calibrated);
            matched.Add((lightInfo, transform.Value));
            progress?.Report(new StackingProgress(StackingPhase.Registering, slug, matched.Count + skipCount, lightList.Count));
        }
        logger.LogInformation("  registered {Matched}/{Attempted} frames (skipped {Skipped}) in {ElapsedMs} ms",
            matched.Count, lightList.Count, skipCount, sw.ElapsedMilliseconds);

        if (matched.Count < 2)
        {
            logger.LogWarning("  [skip] fewer than 2 matched frames; integration would be meaningless");
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
            return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: "fewer than 2 matched frames");
        }

        // BayerDrizzle is opt-in only (--strategy BayerDrizzle). Gate up
        // front so we fail fast with a clear reason rather than producing
        // a NaN-riddled master at low frame count or on a Mono / Color
        // sensor where the per-pixel Bayer dispatch is meaningless. Both
        // checks would otherwise sneak through into RunAsync and produce
        // either a useless master (low N) or a wrong-channel-assignment
        // master (non-RGGB).
        if (options.ForcedStrategy is IntegrationStrategyKind.BayerDrizzle)
        {
            var drizzleOpts = options.DrizzleOptions ?? new DrizzleOptions();
            if (referenceRaw.ImageMeta.SensorType != SensorType.RGGB)
            {
                logger.LogWarning("  [skip] BayerDrizzle requires SensorType.RGGB (got {Sensor})",
                    referenceRaw.ImageMeta.SensorType);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed,
                    SkipReason: $"BayerDrizzle requires SensorType.RGGB (got {referenceRaw.ImageMeta.SensorType})");
            }
            if (matched.Count < drizzleOpts.MinFrameCount)
            {
                logger.LogWarning("  [skip] BayerDrizzle needs >= {Min} matched frames (got {Got}); drizzle coverage would be too sparse",
                    drizzleOpts.MinFrameCount, matched.Count);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed,
                    SkipReason: $"BayerDrizzle requires >= {drizzleOpts.MinFrameCount} matched frames (got {matched.Count})");
            }
        }

        // Compute the union bounding box of all matched frames' source
        // footprints in reference space + per-frame canvas-space AABBs +
        // intersection rectangle for stretch stats.
        var transforms = matched.ConvertAll(m => m.Transform);
        var (canvasShift, outOriginX, outOriginY, outWidth, outHeight) =
            CanvasGeometry.ComputeUnionCanvas(transforms, referenceDebayered.Width, referenceDebayered.Height);
        logger.LogInformation("  [canvas] union bbox = {W}x{H} (origin {X},{Y} in ref space)",
            outWidth, outHeight, outOriginX, outOriginY);

        var (frameFootprints, statsRect) = CanvasGeometry.ComputeFootprintsAndStatsRect(
            transforms, canvasShift, referenceDebayered.Width, referenceDebayered.Height, outWidth, outHeight);

        // Pass B: producer that re-loads each matched frame and warps
        // into the BB canvas, yielding one Image at a time. Cache hot
        // path: pass A stashed each matched frame's calibrated image.
        async IAsyncEnumerable<Image> WarpedFramesProducer(
            [EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < matched.Count; i++)
            {
                var (lightInfo, transformOrig) = matched[i];
                token.ThrowIfCancellationRequested();
                Image calibrated;
                if (calibratedCache.TryGet(i, out var cached))
                {
                    calibrated = cached;
                }
                else
                {
                    var lightRaw = await lightInfo.LoadFullAsync(token);
                    calibrated = calibrator.Apply(lightRaw);
                }
                var debayered = await calibrated.DebayerAsync(options.StackDebayerAlg, cancellationToken: token);
                var shifted = transformOrig * canvasShift;
                var warped = await debayered.WarpToReferenceGridAsync(shifted, outWidth, outHeight, token);
                yield return warped;
            }
        }

        // Drizzle producer: yields the calibrated 1-channel raw CFA frame +
        // composed source->canvas affine. NO debayer, NO warp -- DrizzleStrategy
        // forward-projects each Bayer sample onto the output grid itself.
        // Only built when --strategy BayerDrizzle is selected; the strategy
        // pulls from this and ignores WarpedFrames.
        async IAsyncEnumerable<RawBayerFrame> RawBayerFramesProducer(
            [EnumeratorCancellation] CancellationToken token)
        {
            for (var i = 0; i < matched.Count; i++)
            {
                var (lightInfo, transformOrig) = matched[i];
                token.ThrowIfCancellationRequested();
                Image calibrated;
                if (calibratedCache.TryGet(i, out var cached))
                {
                    calibrated = cached;
                }
                else
                {
                    var lightRaw = await lightInfo.LoadFullAsync(token);
                    calibrated = calibrator.Apply(lightRaw);
                }
                var shifted = transformOrig * canvasShift;
                yield return new RawBayerFrame(calibrated, shifted);
            }
        }

        // Snapshot host + pick strategy. Snapshot factory probes free
        // RAM + disk; the selector wants those for its budget gate.
        var probe = IntegrationProbe.Snapshot(
            frameCount: matched.Count,
            frameWidth: referenceDebayered.Width,
            frameHeight: referenceDebayered.Height,
            channelCount: 3,
            canvasWidth: outWidth,
            canvasHeight: outHeight,
            stagingDir: stagingDir,
            stagingDiskKind: DiskKind.Ssd);
        var selection = IntegrationStrategySelector.Pick(probe, preferred: options.ForcedStrategy);
        logger.LogInformation("  [strategy] picked {Kind} -- {Notes}", selection.Chosen.Kind, selection.Notes);
        logger.LogInformation("  [sink] {Sink} (canvas {GB:F2} GB)", selection.Sink, probe.CanvasBytes / 1e9);
        var sinkFactory = SinkFactories.Create(selection.Sink, stagingDir);

        var rejector = BuildRejector(matched.Count);
        logger.LogInformation("  rejector: {Rejector}", rejector?.GetType().Name ?? "<none>");

        var rawSources = new List<RawLightSource>(matched.Count);
        foreach (var (lightInfo, transformOrig) in matched)
        {
            rawSources.Add(new RawLightSource(Path: lightInfo.Path, TransformToCanvas: transformOrig * canvasShift));
        }

        // Forward strategy progress into the StackingProgress channel.
        var integrationProgress = progress is null
            ? null
            : new Progress<IntegrationProgress>(p => progress.Report(
                new StackingProgress(StackingPhase.Integrating, slug, p.CompletedItems, p.TotalItems, p)));

        var isDrizzle = selection.Chosen.Kind == IntegrationStrategyKind.BayerDrizzle;
        var job = new IntegrationJob(
            WarpedFrames: WarpedFramesProducer,
            ExpectedFrameCount: matched.Count,
            Options: new IntegrationOptions(Rejector: rejector),
            StagingDir: stagingDir,
            StatsRect: statsRect,
            FrameFootprints: frameFootprints,
            RawLightSources: rawSources,
            Calibrator: calibrator,
            DebayerAlgorithm: options.StackDebayerAlg,
            CanvasWidth: outWidth,
            CanvasHeight: outHeight,
            Progress: integrationProgress,
            MasterSinkFactory: sinkFactory,
            RawBayerFrames: isDrizzle ? RawBayerFramesProducer : null,
            DrizzleOptions: isDrizzle ? (options.DrizzleOptions ?? new DrizzleOptions()) : null);

        sw.Restart();
        IntegrationResult intResult;
        try
        {
            intResult = await selection.Chosen.RunAsync(job, ct);
        }
        catch (NotImplementedException ex)
        {
            logger.LogWarning("  [strategy] {Kind} threw NotImplementedException: {Msg}", selection.Chosen.Kind, ex.Message);
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
            return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: $"strategy {selection.Chosen.Kind} not implemented");
        }
        logger.LogInformation("  integrated in {ElapsedMs} ms (frames={Frames}, rejections={Rej}, rate={Rate:P2})",
            sw.ElapsedMilliseconds, intResult.FrameCount, intResult.TotalRejections, intResult.MeanRejectionRate);

        // 3c. Plate-solve the master + write FITS (+ autocrop). No
        // SPCC / bg-neut / PNG render: those are display-side, handled
        // by the caller against the emitted master.
        progress?.Report(new StackingProgress(StackingPhase.PostProcessing, slug, 0, 0));
        // Drizzle masters land under master_<slug>_drizzle.fits so a user
        // A/B-ing drizzle vs the default on the same dataset doesn't
        // silently overwrite. Other strategies share the canonical
        // master_<slug>.fits name -- their differences (memory layout,
        // staging, rejection kernel) are invisible in the output FITS
        // data itself, so a strategy-per-filename split would just add
        // noise. The strategy IS recorded in the SWCREATE+STRATEGY
        // headers regardless of strategy, so provenance stays queryable.
        var strategySuffix = selection.Chosen.Kind == IntegrationStrategyKind.BayerDrizzle
            ? "_drizzle"
            : "";
        var masterPath = Path.Combine(outputDir, $"master_{slug}{strategySuffix}.fits");
        var refImageDim = referenceRaw.GetImageDim();
        var postProcessor = new MasterPostProcessor(logger, catalogDb);
        var (writtenResult, solvedWcs) = await postProcessor.WriteMasterAsync(
            intResult, masterPath, searchHint, refImageDim, referenceRaw.ImageMeta, statsRect, selection.Chosen.Kind, ct);
        if (intResult.TotalRejections > 0)
        {
            logger.LogInformation("  wrote {Path}", IntegrationFitsWriter.RejectionPathFor(masterPath));
        }
        var previewPath = Path.ChangeExtension(masterPath, ".png");
        return new GroupResult(
            slug,
            FramesAttempted: lightList.Count,
            FramesMatched: matched.Count,
            Result: writtenResult,
            MasterFitsPath: masterPath,
            PreviewPngPath: previewPath,
            Elapsed: groupSw.Elapsed);
    }

    // =====================================================================
    // Helpers (moved from StackingEndToEndManualTest verbatim minus log
    // chatter; behaviour-identical)
    // =====================================================================

    private async Task<List<(MasterGroupKey Key, Image Master)>> BuildMastersAsync(
        List<FrameInfo>? frames,
        Func<IReadOnlyList<FrameInfo>, CancellationToken, Task<Image>> builder,
        string mastersDir,
        CancellationToken ct)
    {
        var masters = new List<(MasterGroupKey, Image)>();
        if (frames is null || frames.Count == 0) return masters;

        foreach (var group in frames.GroupBy(MasterGroupKey.FromFrame))
        {
            var key = group.Key;
            var list = group.ToList();
            if (list.Count < 2) continue;

            var masterPath = Path.Combine(mastersDir, $"master_{key.Slug()}.fits");

            // Cache hit: master from a previous run. Bias/dark/flat
            // masters are pure functions of their inputs + builder
            // settings, so if the file exists we trust it. To force
            // refresh, delete outputDir/masters.
            if (File.Exists(masterPath) && Image.TryReadFitsFile(masterPath, out var cached) && cached is not null)
            {
                masters.Add((key, cached));
                logger.LogInformation("  cached {File} ({Count} input frames)", Path.GetFileName(masterPath), list.Count);
                continue;
            }

            var master = await builder(list, ct);
            masters.Add((key, master));
            master.WriteToFitsFile(masterPath);
            logger.LogInformation("  built {File} ({Count} input frames)", Path.GetFileName(masterPath), list.Count);
        }
        return masters;
    }

    private static async Task<(Matrix3x2? Solution, float QuadTolerance, float RmsResidualPx)> TryMatchAsync(
        SortedStarList light, SortedStarList reference, int maxStars)
    {
        foreach (var tol in QuadTolerances)
        {
            // FindFitAsync internally memoizes FindQuadsAsync (per maxStars
            // key), so re-trying with a looser tolerance only re-runs the
            // match pass, not the quad build.
            var (solution, rmsPx) = await light.FindOffsetAndRotationWithRmsAsync(reference, minimumCount: 6, quadTolerance: tol, maxStars: maxStars);
            if (solution is not null) return (solution, tol, rmsPx);
        }
        return (null, float.NaN, float.NaN);
    }

    /// <summary>Find the best master for a light group: exact key match
    /// preferred, else match on (SensorType, ChannelCount, Width,
    /// Height) with closest Exposure/Temp.</summary>
    private static (Image? Master, MasterGroupKey? Key) MatchMaster(List<(MasterGroupKey Key, Image Master)> masters, MasterGroupKey lightKey)
    {
        if (masters.Count == 0) return (null, null);
        var compatible = masters
            .Where(m => m.Key.SensorType == lightKey.SensorType
                     && m.Key.ChannelCount == lightKey.ChannelCount
                     && m.Key.Width == lightKey.Width
                     && m.Key.Height == lightKey.Height)
            .ToList();
        if (compatible.Count == 0) return (null, null);

        var pick = compatible
            .OrderBy(m =>
            {
                var dTemp = lightKey.TemperatureC is { } lt && m.Key.TemperatureC is { } mt ? Math.Abs(lt - mt) : 100;
                var dExposure = Math.Abs((m.Key.Exposure - lightKey.Exposure).TotalSeconds);
                return dTemp * 10.0 + dExposure;
            })
            .First();
        return (pick.Master, pick.Key);
    }

}

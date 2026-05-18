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
using TianWen.Lib.Astrometry.PlateSolve;
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
        // Wipe any stale per-group output FITS from a previous run; the
        // masters/ cache is preserved (cal masters are pure functions of
        // their inputs, expensive to rebuild).
        foreach (var f in Directory.EnumerateFiles(outputDir, "*.fits")) File.Delete(f);
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

        foreach (var lightGroup in lightGroups)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProcessLightGroupAsync(
                lightGroup.Key, lightGroup.ToList(), darkMasters, flatMasters, outputDir, ct);
            yield return result;
        }

        logger.LogInformation("[end]");
    }

    // =====================================================================
    // Per-group orchestration
    // =====================================================================

    private async Task<GroupResult> ProcessLightGroupAsync(
        LightGroupKey key,
        List<FrameInfo> lightList,
        List<(MasterGroupKey Key, Image Master)> darkMasters,
        List<(MasterGroupKey Key, Image Master)> flatMasters,
        string outputDir,
        CancellationToken ct)
    {
        var calKey = key.CalibrationKey;
        var groupSw = Stopwatch.StartNew();
        logger.LogInformation("=== Light group: {Slug} ({Count} frames) ===", key.Slug(), lightList.Count);

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
        var calibrator = new Calibrator(Bias: null, Dark: dark, Flat: flat, Pedestal: 0f);

        // 3a. Pick reference (highest star count). We bypass
        // Registrator.PickReferenceAsync because it operates on the raw
        // FrameInfo without debayer awareness.
        progress?.Report(new StackingProgress(StackingPhase.Registering, key.Slug(), 0, lightList.Count));
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
            return new GroupResult(key.Slug(), lightList.Count, 0, Result: null, MasterFitsPath: null,
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
        var stagingDir = Path.Combine(outputDir, "_staging", key.Slug());
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
            progress?.Report(new StackingProgress(StackingPhase.Registering, key.Slug(), matched.Count + skipCount, lightList.Count));
        }
        logger.LogInformation("  registered {Matched}/{Attempted} frames (skipped {Skipped}) in {ElapsedMs} ms",
            matched.Count, lightList.Count, skipCount, sw.ElapsedMilliseconds);

        if (matched.Count < 2)
        {
            logger.LogWarning("  [skip] fewer than 2 matched frames; integration would be meaningless");
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
            return new GroupResult(key.Slug(), lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: "fewer than 2 matched frames");
        }

        // Compute the union bounding box of all matched frames' source
        // footprints in reference space.
        var (canvasShift, outOriginX, outOriginY, outWidth, outHeight) =
            ComputeUnionCanvas(matched, referenceDebayered.Width, referenceDebayered.Height);
        logger.LogInformation("  [canvas] union bbox = {W}x{H} (origin {X},{Y} in ref space)",
            outWidth, outHeight, outOriginX, outOriginY);

        var (frameFootprints, statsRect) = ComputeFootprintsAndStatsRect(
            matched, canvasShift, referenceDebayered.Width, referenceDebayered.Height, outWidth, outHeight);

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
                new StackingProgress(StackingPhase.Integrating, key.Slug(), p.CompletedItems, p.TotalItems, p)));

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
            MasterSinkFactory: sinkFactory);

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
            return new GroupResult(key.Slug(), lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: $"strategy {selection.Chosen.Kind} not implemented");
        }
        logger.LogInformation("  integrated in {ElapsedMs} ms (frames={Frames}, rejections={Rej}, rate={Rate:P2})",
            sw.ElapsedMilliseconds, intResult.FrameCount, intResult.TotalRejections, intResult.MeanRejectionRate);

        // 3c. Plate-solve the master + write FITS (+ autocrop). No
        // SPCC / bg-neut / PNG render: those are display-side, handled
        // by the caller against the emitted master.
        progress?.Report(new StackingProgress(StackingPhase.PostProcessing, key.Slug(), 0, 0));
        var masterPath = Path.Combine(outputDir, $"master_{key.Slug()}.fits");
        var refImageDim = referenceRaw.GetImageDim();
        var (writtenResult, solvedWcs) = await PlateSolveAndWriteAsync(
            intResult, masterPath, searchHint, refImageDim, referenceRaw.ImageMeta, statsRect, ct);
        if (intResult.TotalRejections > 0)
        {
            logger.LogInformation("  wrote {Path}", IntegrationFitsWriter.RejectionPathFor(masterPath));
        }
        var previewPath = Path.ChangeExtension(masterPath, ".png");
        return new GroupResult(
            key.Slug(),
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

    // ---------------------------------------------------------------------
    // Canvas geometry (union BB + intersection AABB + per-frame footprints)
    // ---------------------------------------------------------------------

    private static (Matrix3x2 CanvasShift, int OriginX, int OriginY, int Width, int Height) ComputeUnionCanvas(
        List<(FrameInfo Light, Matrix3x2 Transform)> matched, int refW, int refH)
    {
        float minX = 0f, minY = 0f;
        float maxX = refW, maxY = refH;
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            Vector2.Zero,
            new Vector2(refW, 0f),
            new Vector2(0f, refH),
            new Vector2(refW, refH),
        };
        foreach (var (_, mt) in matched)
        {
            for (var i = 0; i < 4; i++)
            {
                var p = Vector2.Transform(corners[i], mt);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
        var outOriginX = (int)MathF.Floor(minX);
        var outOriginY = (int)MathF.Floor(minY);
        var outWidth = (int)MathF.Ceiling(maxX) - outOriginX;
        var outHeight = (int)MathF.Ceiling(maxY) - outOriginY;
        var canvasShift = Matrix3x2.CreateTranslation(-outOriginX, -outOriginY);
        return (canvasShift, outOriginX, outOriginY, outWidth, outHeight);
    }

    private static (List<Rectangle> FrameFootprints, Rectangle StatsRect) ComputeFootprintsAndStatsRect(
        List<(FrameInfo Light, Matrix3x2 Transform)> matched,
        Matrix3x2 canvasShift,
        int srcW, int srcH, int outWidth, int outHeight)
    {
        var intersectionPoly = new List<Vector2>
        {
            new(0f,        0f),
            new(outWidth,  0f),
            new(outWidth,  outHeight),
            new(0f,        outHeight),
        };
        // Heap-allocate quad buffer once outside the loop (CA2014).
        var quad = new Vector2[4];
        var frameFootprints = new List<Rectangle>(matched.Count);
        foreach (var (_, transformOrig) in matched)
        {
            var t = transformOrig * canvasShift;
            quad[0] = Vector2.Transform(new Vector2(0f,   0f),   t);
            quad[1] = Vector2.Transform(new Vector2(srcW, 0f),   t);
            quad[2] = Vector2.Transform(new Vector2(srcW, srcH), t);
            quad[3] = Vector2.Transform(new Vector2(0f,   srcH), t);
            float fxMin = quad[0].X, fxMax = quad[0].X;
            float fyMin = quad[0].Y, fyMax = quad[0].Y;
            for (var i = 1; i < 4; i++)
            {
                if (quad[i].X < fxMin) fxMin = quad[i].X;
                if (quad[i].X > fxMax) fxMax = quad[i].X;
                if (quad[i].Y < fyMin) fyMin = quad[i].Y;
                if (quad[i].Y > fyMax) fyMax = quad[i].Y;
            }
            var ffX = Math.Max(0, (int)MathF.Floor(fxMin));
            var ffY = Math.Max(0, (int)MathF.Floor(fyMin));
            var ffR = Math.Min(outWidth, (int)MathF.Ceiling(fxMax));
            var ffB = Math.Min(outHeight, (int)MathF.Ceiling(fyMax));
            frameFootprints.Add(new Rectangle(ffX, ffY, Math.Max(0, ffR - ffX), Math.Max(0, ffB - ffY)));

            EnsureCwInCanvas(quad);
            intersectionPoly = ClipConvex(intersectionPoly, quad);
            if (intersectionPoly.Count == 0) break;
        }
        while (frameFootprints.Count < matched.Count)
        {
            frameFootprints.Add(new Rectangle(0, 0, outWidth, outHeight));
        }

        Rectangle statsRect;
        if (intersectionPoly.Count == 0)
        {
            statsRect = Rectangle.Empty;
        }
        else
        {
            float xMin = float.PositiveInfinity, yMin = float.PositiveInfinity;
            float xMax = float.NegativeInfinity, yMax = float.NegativeInfinity;
            foreach (var v in intersectionPoly)
            {
                if (v.X < xMin) xMin = v.X;
                if (v.X > xMax) xMax = v.X;
                if (v.Y < yMin) yMin = v.Y;
                if (v.Y > yMax) yMax = v.Y;
            }
            var rx = (int)MathF.Ceiling(xMin);
            var ry = (int)MathF.Ceiling(yMin);
            var rw = (int)MathF.Floor(xMax) - rx;
            var rh = (int)MathF.Floor(yMax) - ry;
            statsRect = new Rectangle(rx, ry, Math.Max(0, rw), Math.Max(0, rh));
        }
        return (frameFootprints, statsRect);
    }

    /// <summary>
    /// Reverses <paramref name="quad"/> in place if its winding is CCW in
    /// canvas-y-down axes, so downstream <see cref="ClipConvex"/>, which
    /// expects CW-in-canvas, gets a consistently oriented quad. A
    /// 180-degree-rotated frame (post-meridian-flip) flips winding to
    /// CCW-in-canvas and needs reversing.
    /// </summary>
    private static void EnsureCwInCanvas(Span<Vector2> quad)
    {
        double area = 0;
        for (var i = 0; i < quad.Length; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % quad.Length];
            area += (double)(b.X - a.X) * (b.Y + a.Y);
        }
        if (area > 0)
        {
            quad.Reverse();
        }
    }

    private static List<Vector2> ClipConvex(List<Vector2> subject, ReadOnlySpan<Vector2> clip)
    {
        var output = new List<Vector2>(subject);
        var input = new List<Vector2>(subject.Count + 2);
        for (var i = 0; i < clip.Length; i++)
        {
            if (output.Count == 0) return output;
            input.Clear();
            input.AddRange(output);
            output.Clear();
            var a = clip[i];
            var b = clip[(i + 1) % clip.Length];
            var edgeDx = b.X - a.X;
            var edgeDy = b.Y - a.Y;
            for (var j = 0; j < input.Count; j++)
            {
                var curr = input[j];
                var prev = input[(j - 1 + input.Count) % input.Count];
                var currSide = edgeDx * (curr.Y - a.Y) - edgeDy * (curr.X - a.X);
                var prevSide = edgeDx * (prev.Y - a.Y) - edgeDy * (prev.X - a.X);
                var currIn = currSide >= 0;
                var prevIn = prevSide >= 0;
                if (currIn)
                {
                    if (!prevIn) output.Add(IntersectSegment(prev, curr, a, b));
                    output.Add(curr);
                }
                else if (prevIn)
                {
                    output.Add(IntersectSegment(prev, curr, a, b));
                }
            }
        }
        return output;
    }

    private static Vector2 IntersectSegment(Vector2 p1, Vector2 p2, Vector2 a, Vector2 b)
    {
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        var ex = b.X - a.X;
        var ey = b.Y - a.Y;
        var denom = dx * ey - dy * ex;
        var t = ((a.X - p1.X) * ey - (a.Y - p1.Y) * ex) / denom;
        return new Vector2(p1.X + t * dx, p1.Y + t * dy);
    }

    // ---------------------------------------------------------------------
    // Plate-solve + write master FITS (and autocrop variant)
    // ---------------------------------------------------------------------

    private async Task<(IntegrationResult Result, WCS? SolvedWcs)> PlateSolveAndWriteAsync(
        IntegrationResult result,
        string masterPath,
        WCS? searchHint,
        ImageDim? imageDim,
        ImageMeta refMeta,
        Rectangle autocropRect,
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
        IntegrationFitsWriter.Write(masterPath, result, solvedWcs);
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
                IntegrationFitsWriter.Write(cropFitsPath, croppedResult, croppedWcs);
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

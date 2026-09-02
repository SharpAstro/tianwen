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
using TianWen.Lib.IO;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Stat;

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
/// <param name="enhanceProgress">Optional per-step AI-enhance progress sink,
/// forwarded to the SharpenPipeline during the <c>--enhance</c> pass so the long
/// deblur / denoise steps aren't a silent terminal.</param>
public sealed class StackingPipeline(
    StackingOptions options,
    ILogger logger,
    ICelestialObjectDB? catalogDb = null,
    IProgress<StackingProgress>? progress = null,
    Enhancement.SharpenPipeline? sharpenPipeline = null,
    IProgress<Enhancement.EnhanceProgress>? enhanceProgress = null,
    Enhancement.IStarRemover? starRemover = null)
{
    /// <summary>
    /// Picks a pixel rejector for the integration step based on frame
    /// count. Defaults from the manual test against the SoL dataset:
    /// LFC for small N (best per-iteration quality, ~8x slower than
    /// sigma at large N), Winsorized for medium, asymmetric SigmaClip
    /// (low=3, high=5) for large N (speed wins; high-kappa keeps stars).
    ///
    /// <remarks>
    /// <para>The KIND is chosen by frame count and the THRESHOLDS are a separate
    /// decision, which is why the overrides substitute into whichever kind the count
    /// picked rather than selecting a kind of their own. The count is about how many
    /// samples the estimator has to work with; the sigma pair is about what the caller
    /// is trying to throw away.</para>
    /// <para>The defaults are deliberately asymmetric the STAR-KEEPING way, and a
    /// comet layer wants the opposite. Comet-aligned, a star touches any given canvas
    /// cell in a handful of frames out of N, so it is a textbook high outlier -- and
    /// <c>HighSigma: 5</c> exists precisely to let a real star through. Lowering the
    /// high side is what makes rejection a second line of defence behind
    /// <c>--remove-stars</c> instead of working against it.</para>
    /// </remarks>
    /// </summary>
    /// <param name="frameCount">Matched frames; picks the rejector kind.</param>
    /// <param name="lowSigma">Overrides the low (dark-outlier) threshold. Null keeps the per-kind default.</param>
    /// <param name="highSigma">Overrides the high (bright-outlier) threshold. Null keeps the per-kind default.</param>
    public static IPixelRejector? BuildRejector(int frameCount, float? lowSigma = null, float? highSigma = null) => frameCount switch
    {
        < 5  => null,
        < 30 => new LinearFitClipRejector(LowSigma: lowSigma ?? 3f, HighSigma: highSigma ?? 3f, MaxIterations: 5),
        < 60 => new WinsorizedSigmaClipRejector(LowSigma: lowSigma ?? 3f, HighSigma: highSigma ?? 5f, MaxIterations: 5),
        _    => new SigmaClipRejector(LowSigma: lowSigma ?? 3f, HighSigma: highSigma ?? 5f, MaxIterations: 5),
    };

    /// <summary>
    /// Remove stars from ONE frame, splitting a Bayer mosaic into its four photosite planes first
    /// and running the remover on each.
    ///
    /// <remarks>
    /// <para>A star in a CFA mosaic is a CHECKERBOARD, not a point spread function: neighbouring
    /// pixels are different colours, and a remover trained on ordinary astronomical images is asked
    /// to read them as adjacent samples of one signal. Measured on a real calibrated 60 s sub over
    /// 419 stars, residuals in units of the frame's own noise: whole-mosaic leaves red with a
    /// +15.94 sigma tail while green digs -6.35 sigma holes -- red-positive, green-negative, which
    /// is MAGENTA, and is exactly the coloured streaking that showed up in the comet layer. Split
    /// into planes, the same frame gives R/G/B tails of 5.73 / 5.67 / 4.81 and holes of
    /// -3.71 / -3.61 / -3.68: the channel asymmetry is gone.</para>
    /// <para>Each plane is enhanced SEPARATELY as a single-channel image, which is how the
    /// measurement was taken; handing the remover one four-channel image would invite it to read the
    /// planes as colour plus alpha. Full RGB demosaicing first scores slightly better again (tails
    /// 4.17 / 3.93 / 3.83) but yields a three-channel plate, which cannot feed Bayer drizzle --
    /// splitting keeps the raw CFA, so it buys most of the gain and forces no downstream choice.
    /// White-balancing first was measured and does NOTHING (identical to two decimals), so the
    /// interleaving is the mechanism and the colour balance is not.</para>
    /// <para>Cost is four half-resolution calls against one full-resolution call: about 12 s versus
    /// 8 s per frame on this box, so roughly 50% slower rather than faster.</para>
    /// </remarks>
    /// </summary>
    private static async ValueTask<Image> RemoveStarsFromFrameAsync(
        Image frame, StarRemovalMode mode, Enhancement.IStarRemover starRemover, ILogger logger, CancellationToken ct)
    {
        // Whole-mosaic is the default and is what a frame carrying an extended target wants; see
        // StarRemovalMode for the trade and the numbers behind it. Anything that is not a Bayer CFA
        // (mono, or already-debayered colour) has no interleaving to undo either way. Note the
        // SensorType.RGGB test admits EVERY Bayer pattern -- it names the CFA, and GRBG / GBRG / BGGR
        // carry their rotation in BayerOffsetX/Y -- so this is not a restriction to one camera family.
        if (mode is not StarRemovalMode.SplitCfa
            || frame.ChannelCount != 1
            || frame.ImageMeta.SensorType is not SensorType.RGGB)
        {
            return await starRemover.EnhanceAsync(frame, ct);
        }

        // OWNERSHIP: this method BORROWS frame -- it never releases it, and never returns it. The
        // caller owns frame (and releases it in its own finally) and owns whatever comes back. That
        // is the same contract IStarRemover.EnhanceAsync has, so the mosaic branch above can simply
        // forward. Getting it wrong here is silent: releasing frame would hand the same buffer back
        // to the camera twice, and returning frame would make the caller release one image twice.
        var split = frame.SplitBayerChannels();
        var cleaned = new Image[4];
        Image merged;
        try
        {
            for (var c = 0; c < 4; c++)
            {
                // AsSingleChannel is a borrowed VIEW onto split (shared array, no buffer), so it is
                // not released; split owns those arrays and is released below.
                cleaned[c] = await starRemover.EnhanceAsync(split.AsSingleChannel(c), ct);
            }

            // MergeBayerChannels allocates the mosaic and COPIES into it, so the cleaned planes are
            // dead the moment it returns -- which is what makes releasing them in the finally safe.
            var quad = new Image(
                [cleaned[0].GetChannelArray(0), cleaned[1].GetChannelArray(0), cleaned[2].GetChannelArray(0), cleaned[3].GetChannelArray(0)],
                BitDepth.Float32, frame.MaxValue, frame.MinValue, frame.Pedestal, frame.ImageMeta);
            merged = quad.MergeBayerChannels();
        }
        finally
        {
            split.Release();
            foreach (var plane in cleaned)
            {
                plane?.Release();
            }
        }

        if (merged.Width == frame.Width && merged.Height == frame.Height)
        {
            return merged;
        }

        // SplitBayerChannels floor-divides, so an ODD width or height loses its last row / column and
        // the merge returns smaller. Real frames ARE odd -- this sensor is 4164 x 2795. The geometry
        // cannot change, because every per-frame transform in the manifest was solved against the
        // original raster. So build a FULL-SIZE result seeded from the calibrated frame and overwrite
        // the region that was actually processed; the surviving edge keeps its calibrated pixels,
        // leaving stars in at most one row and one column at the frame boundary, which the footprint
        // autocrop trims off a dithered session. A fabricated fill would be worse: it invents data
        // where the sensor has some. Seeded by COPY rather than by handing back frame, because frame
        // belongs to the caller.
        var padded = Image.CreateChannelData(1, frame.Height, frame.Width);
        var dst = padded[0];
        var edge = frame.GetChannelArray(0);
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                dst[y, x] = edge[y, x];
            }
        }
        var core = merged.GetChannelArray(0);
        for (var y = 0; y < merged.Height; y++)
        {
            for (var x = 0; x < merged.Width; x++)
            {
                dst[y, x] = core[y, x];
            }
        }
        logger.LogDebug("  [starless] odd raster {W}x{H}: kept {DX} column(s), {DY} row(s) of calibrated edge",
            frame.Width, frame.Height, frame.Width - merged.Width, frame.Height - merged.Height);
        merged.Release();
        return new Image(padded, BitDepth.Float32, frame.MaxValue, frame.MinValue, frame.Pedestal, frame.ImageMeta);
    }


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
        foreach (var f in FileEnumeration.EnumerateFiles(outputDir, ".fits", recursive: false))
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

        // Construct the tracker AFTER the wipe + staging cleanup so the
        // baseline disk reading reflects the run's actual starting point
        // (not the pre-cleanup state including stale outputs that we just
        // deleted). RAM baseline is "right before we open any FITS files",
        // which is the most useful reference for "how much did the pipeline
        // consume" questions.
        var hostTracker = new HostSnapshotTracker(outputDir);
        logger.LogInformation("[start] data={DataRoot} out={OutputDir}", options.DataRoot, outputDir);
        hostTracker.Log(logger, "start");

        // -----------------------------------------------------------------
        // 1) Enumerate ALL FITS recursively + group by frame type
        // -----------------------------------------------------------------
        progress?.Report(new StackingProgress(StackingPhase.Scanning, "", 0, 0));
        var sw = Stopwatch.StartNew();
        var source = new FitsFolderFrameSource(options.DataRoot, recursive: true);
        var allFrames = new List<FrameInfo>();
        var outputDirNormalised = Path.GetFullPath(outputDir);
        var integrationSkipped = 0;
        var rejectionMapSkipped = 0;
        var integrationKept = 0;
        var masterSkipped = 0;
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
            // Rejection maps carry STACK_N too but are per-pixel
            // rejection-fraction images, not sky data -- always drop them
            // regardless of IncludeIntegrations. Filename suffix is the
            // canonical IntegrationFitsWriter marker; check both bare and
            // .gz variants since FitsFolderFrameSource accepts both.
            if (frame.Path.EndsWith(IntegrationFitsWriter.RejectionMapSuffix, StringComparison.OrdinalIgnoreCase) ||
                frame.Path.EndsWith(IntegrationFitsWriter.RejectionMapSuffix + ".gz", StringComparison.OrdinalIgnoreCase))
            {
                rejectionMapSkipped++;
                continue;
            }
            // A foreign MASTER calibration frame (IMAGETYP=MASTERDARK / MASTERFLAT /
            // MASTERBIAS) now parses to its underlying FrameType (Dark / Flat / Bias)
            // so the dataset builder can ingest it directly -- but the stacker builds
            // masters ONLY from raw subs whose provenance it controls (the "never
            // ingest foreign masters" invariant). Feeding a pre-integrated master into
            // BuildMastersAsync would fold a whole master in as if it were one raw
            // frame. Skip masters here to keep the stacker raw-only (before master
            // parsing was added these were inert FrameType.None frames anyway).
            if (frame.Meta.IsMaster)
            {
                masterSkipped++;
                continue;
            }
            // Two markers identify a TianWen-produced FITS that must not be
            // re-ingested as a fresh light:
            //   * STACK_N > 0 -- a stacking master (the rejection branch above
            //     already filtered rejection maps, so this is an integrated
            //     master).
            //   * A TianWen SWCREATE -- ANY of our derived products. An AI
            //     sharpen / enhance output inherits the master's SWCREATE but
            //     carries NO STACK_N and an IMAGETYP=Light copied from the
            //     original subs, so the STACK_N check alone misses it -- which
            //     is exactly how processed outputs in an adjacent output-*/ dir
            //     get partitioned into ghost MasterGroupKey buckets and silently
            //     re-stacked.
            // Default policy is to drop both. IncludeIntegrations opts in for
            // two-stage mosaic stacking where each panel is integrated
            // separately, then the panel masters are re-stacked.
            if (frame.StackedFrameCount > 0 || IntegrationFitsWriter.IsTianWenProduct(frame.Meta.SWCreator, frame.Meta.SWModifier))
            {
                if (options.IncludeIntegrations)
                {
                    integrationKept++;
                }
                else
                {
                    integrationSkipped++;
                    continue;
                }
            }
            allFrames.Add(frame);
        }
        if (rejectionMapSkipped > 0)
        {
            logger.LogInformation("[scan] ignored {Count} rejection map(s)", rejectionMapSkipped);
        }
        if (masterSkipped > 0)
        {
            logger.LogInformation("[scan] ignored {Count} foreign master frame(s) (IMAGETYP=MASTER*); stacker builds masters from raw subs only", masterSkipped);
        }
        if (integrationSkipped > 0)
        {
            logger.LogInformation("[scan] ignored {Count} TianWen product(s) (STACK_N or TianWen SWCREATE); pass --include-integrations to keep them",
                integrationSkipped);
        }
        if (integrationKept > 0)
        {
            logger.LogInformation("[scan] keeping {Count} integration(s) as input (IncludeIntegrations=true)",
                integrationKept);
        }
        logger.LogInformation("[scan] {Count} frames in {ElapsedMs} ms", allFrames.Count, sw.ElapsedMilliseconds);
        // Surface the scan summary on the progress channel (a CLI / TUI floors
        // its console at Warning, so the LogInformation lines above are file-only
        // -- and a silently re-ingested product is exactly the footgun this run
        // just guarded against, so it must be visible).
        progress?.Report(new StackingProgress(
            StackingPhase.Scanning, "", 0, 0,
            Scan: new ScanSummary(allFrames.Count, integrationSkipped, rejectionMapSkipped, integrationKept)));
        hostTracker.Log(logger, "scan");

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
        // A dark-flat master is built exactly like a dark (median, pedestal retained); its job is
        // to be the flat's pedestal below. It deliberately keeps its bias in, so subtracting it
        // whole removes offset + the thermal signal the flat accumulated in one step (the DSS
        // model reaches the same flat algebraically via bias-subtracted stages).
        var darkFlatMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.DarkFlat), MasterFrameBuilder.BuildDarkMasterAsync, mastersDir, ct);

        // Flats are built AFTER bias + dark-flats so each can have its own pedestal removed before
        // it is normalised (a raw flat is offset + signal, and normalising that divides the offset
        // in, so the master under-corrects by offset/(offset+signal): about 2% on a real ASI533
        // frame). The suffix is load-bearing, not cosmetic. This cache trusts any file it finds, so
        // without a new name every existing masters/ directory would keep serving the uncalibrated
        // flat it cached before this existed; and the suffix encodes the pedestal-candidate KINDS
        // ("_bs" bias-only keeps every existing cache valid, "_dfs" dark-flats only, "_ps" both) so
        // dark-flats appearing in an archive cannot silently leave a bias-pedestalled flat in
        // service. Within one kind-landscape the match is deterministic; to force a refresh,
        // delete outputDir/masters (unchanged contract).
        var flatFrames = byType.GetValueOrDefault(FrameType.Flat);
        // Darks at a flat-like exposure are dark-flats whatever their label (N.I.N.A. writes
        // flat-matched sets as IMAGETYP=DARK), so they join the pedestal pool behind the same
        // hard ratio gate the resolver uses; a real light-dark never passes it. Computed against
        // the DISTINCT flat exposures so the suffix below can treat them as the dark-flat kind.
        var flatExposures = (flatFrames ?? []).Select(f => MasterGroupKey.FromFrame(f).Exposure).Distinct().ToList();
        var pedestalDarkMasters = darkMasters
            .Where(m => flatExposures.Any(fe => CalibrationResolver.FlatPedestalExposureCompatible(m.Key.Exposure, fe)))
            .ToList();
        var flatMasters = await BuildMastersAsync(
            flatFrames,
            async (list, token) =>
            {
                // Matched against the FLAT's own key, not a light's: the offset being removed is
                // the one this flat was recorded with. One candidate pool of biases, dark-flats
                // AND exposure-gated darks, because MatchMaster's exposure term is the physics of
                // the choice: the pedestal error a candidate leaves behind is the thermal signal
                // over |t_cand - t_flat|, so an exposure-matched dark-flat (gap ~0) beats a bias
                // (gap = t_flat), while a lone mismatched dark-flat loses to a good bias instead
                // of injecting thermal + glow the flat never accumulated. Across an all-bias pool
                // the term is a constant (~0 s exposures), so temperature decides, as it always
                // did. The gated darks are re-gated against THIS group's exposure (the pool gate
                // above used any flat group's).
                var flatKey = MasterGroupKey.FromFrame(list[0]);
                var candidates = new List<(MasterGroupKey Key, Image Master)>(biasMasters.Count + darkFlatMasters.Count + pedestalDarkMasters.Count);
                candidates.AddRange(biasMasters);
                candidates.AddRange(darkFlatMasters);
                candidates.AddRange(pedestalDarkMasters.Where(m => CalibrationResolver.FlatPedestalExposureCompatible(m.Key.Exposure, flatKey.Exposure)));
                var (pedestal, pedestalKey) = MatchMaster(
                    candidates, flatKey, MasterMatchKind.FlatPedestal, list[0].Meta.ExposureStartTime);
                logger.LogInformation("  flat pedestal: {Pedestal}", pedestalKey?.Slug() ?? "NONE");
                return await MasterFrameBuilder.BuildFlatMasterAsync(list, pedestal, token);
            },
            mastersDir, ct, pathSuffix: (biasMasters.Count > 0, darkFlatMasters.Count > 0 || pedestalDarkMasters.Count > 0) switch
            {
                (true, true) => "_ps",
                (true, false) => "_bs",
                (false, true) => "_dfs",
                _ => "",
            });
        logger.LogInformation("[masters] {Bias} bias, {Dark} dark, {DarkFlat} dark-flat, {Flat} flat ready in {ElapsedMs} ms",
            biasMasters.Count, darkMasters.Count, darkFlatMasters.Count, flatMasters.Count, sw.ElapsedMilliseconds);
        hostTracker.Log(logger, "masters");

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

        // Drop tiny sub-groups silently. These are almost always ghosts from
        // MasterGroupKey drift -- a single frame's CCDTemperature rounding
        // to -4C instead of -5C, or an offset value that drifted mid-session,
        // partitions an otherwise-uniform observation into a "real" group
        // (most of the frames) plus a handful of 1-2 frame stragglers. Each
        // straggler then trickles through registration, fails the "matched
        // >= 2" check, and emits a SKIPPED warning per group -- pure log
        // noise. Pre-filtering at scan time means one summary instead of
        // N warnings. Threshold of 4 lines up with the smallest viable
        // integration count; below it kappa-sigma rejection has nothing
        // to clip against and the result is statistically meaningless
        // anyway. Real 4+ frame sub-groups still process normally.
        const int MinSubGroupFramesToProcess = 4;
        var tinySubGroups = subGroups.Where(g => g.Frames.Count < MinSubGroupFramesToProcess).ToList();
        if (tinySubGroups.Count > 0)
        {
            var totalDropped = tinySubGroups.Sum(g => g.Frames.Count);
            logger.LogInformation(
                "[lights] dropped {Count} ghost sub-group(s) below MinSubGroupFrames={Min} ({Frames} frames total, likely header-drift artifacts)",
                tinySubGroups.Count, MinSubGroupFramesToProcess, totalDropped);
            // Per-ghost diagnostic: surface every field of the
            // MasterGroupKey since the slug strips the ones that usually
            // drift (Offset, FilterName, exact TemperatureC, dimensions).
            // One Debug-level line per ghost so the file logger captures it
            // for post-mortem but the console (Warning min) stays quiet.
            foreach (var ghost in tinySubGroups)
            {
                var k = ghost.Key.CalibrationKey;
                var sample = ghost.Frames[0];
                logger.LogDebug(
                    "[lights/ghost] {Slug} ({Frames} fr): temp={Temp}C filter={Filter} offset={Offset} gain={Gain} dim={W}x{H}x{Ch} sensor={Sensor} sample={Path}",
                    ghost.Slug, ghost.Frames.Count,
                    k.TemperatureC?.ToString() ?? "n/a", k.FilterIdentity.Length > 0 ? k.FilterIdentity : "(empty)",
                    k.Offset, k.Gain, k.Width, k.Height, k.ChannelCount, k.SensorType,
                    System.IO.Path.GetFileName(sample.Path));
            }
            subGroups = subGroups.Where(g => g.Frames.Count >= MinSubGroupFramesToProcess).ToList();
        }

        foreach (var (key, slug, frames) in subGroups)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ProcessLightGroupAsync(
                key, slug, frames, darkMasters, flatMasters, biasMasters, outputDir, hostTracker, ct);
            yield return result;
        }

        hostTracker.Log(logger, "end");
        ReportOutstandingChannelBuffers(logger);
        logger.LogInformation("[end]");
    }

    /// <summary>
    /// End-of-run buffer-ownership check (P2 of <c>docs/plans/frame-lifecycle.md</c>). DEBUG only;
    /// the call itself is compiled out of Release.
    /// </summary>
    /// <remarks>
    /// <para>This is the hook the plan exists for. Both tile-pipelined strategies read their raw
    /// frame and never released it, which was free while file loads owned their arrays and became a
    /// real leak the instant the read was pooled -- and nothing said so, in the type system or
    /// anywhere else, until someone watched process memory. Nothing should still be outstanding once
    /// the last group has been written, so anything here names the producer that forgot.</para>
    /// <para>No forced collection: a dropped frame is reported as outstanding whether or not the
    /// collector has got to it yet, so the cheap sweep answers the same question as the blocking one
    /// and a stack run does not pay a Gen2 pause to ask.</para>
    /// </remarks>
    [Conditional("DEBUG")]
    private static void ReportOutstandingChannelBuffers(ILogger logger)
    {
        var report = ChannelBufferLeakTracker.Report();
        if (report.LiveCount == 0 && report.LeakCount == 0)
        {
            return;
        }

        // The pool's own accounting beside it, because the two answer halves of one question: the
        // tracker says how many arrays were lent and never handed back, the pool says how much it is
        // holding and how much it refused. A shortfall in the first with the second at its ceiling is
        // a different diagnosis from a shortfall on an empty pool.
        logger.LogWarning(
            "[end] channel buffers were not released: {Report} | pool retained {RetainedMiB} MiB, budget evictions {Evictions}",
            report.Describe(),
            Array2DPool<float>.RetainedBytes >> 20,
            Array2DPool<float>.BudgetEvictionCount);
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
        List<(MasterGroupKey Key, Image Master)> biasMasters,
        string outputDir,
        HostSnapshotTracker hostTracker,
        CancellationToken ct)
    {
        // slug carries any pier-side / future sub-group suffix on top of
        // key.Slug() -- it's the canonical name for filenames + logs in this
        // method. Tied to a single group, so we capture it once up top.
        var calKey = key.CalibrationKey;
        var groupSw = Stopwatch.StartNew();
        // Per-stage wall time with Items AND Pixels (StageTimings, the type the dataset registrar
        // already records into), so throughput is stored on the GroupResult rather than re-derived
        // from log lines -- the log-derived version of the dataset baseline got a stage wrong by
        // normalising per input frame, which is exactly what stored denominators prevent.
        var timings = new StageTimings();
        logger.LogInformation("=== Light group: {Slug} ({Count} frames) ===", slug, lightList.Count);

        // Calibration path. WITH a dark, bias is deliberately NOT passed: the master dark was built
        // from raw darks with no bias pre-subtraction, so the bias signal is already baked into it
        // and subtracting both would remove the pedestal twice. light - dark - flat is complete.
        //
        // WITHOUT a dark, the bias has to stand in, because otherwise NOTHING removes the pedestal
        // and the flat then divides a frame that still carries it. That is not merely a missing
        // step, it is the wrong ORDER: (signal + pedestal) / flat imprints the flat's inverse shape
        // onto what should be a constant offset. Measured on a SVBONY SV605CC set whose 30 s lights
        // have no dark at any temperature -- an 804 ADU pedestal divided by a flat spanning
        // 0.950-1.019 spreads to 789-846 ADU, a 57 ADU gradient across a frame whose real sky signal
        // is 948 ADU. Six percent, shaped exactly like inverse vignetting, so it reads as light
        // pollution and a later gradient correction would happily 'fix' it.
        var groupDate = lightList[0].Meta.ExposureStartTime;
        var (dark, darkKey) = MatchMaster(darkMasters, calKey, MasterMatchKind.Dark, groupDate, options.RequireGainMatch);
        var (flat, flatKey) = MatchMaster(flatMasters, calKey, MasterMatchKind.Flat, groupDate);
        var (bias, biasKey) = dark is null
            ? MatchMaster(biasMasters, calKey, MasterMatchKind.Bias, groupDate, options.RequireGainMatch)
            : (null, null);
        logger.LogInformation("  dark master: {Dark}", darkKey is null ? "NONE" : darkKey.Slug());
        logger.LogInformation("  flat master: {Flat}", flatKey is null ? "NONE" : flatKey.Slug());
        if (dark is null)
        {
            logger.LogInformation("  bias master: {Bias} (no dark matched, so the bias carries the pedestal)",
                biasKey is null ? "NONE" : biasKey.Slug());
            if (biasKey is null && flat is not null)
            {
                logger.LogWarning(
                    "  no dark AND no bias matched, but a flat did: the flat will divide an "
                    + "un-pedestal-corrected frame and imprint its inverse shape as a gradient");
            }
        }
        var calibrator = new Calibrator(Bias: bias, Dark: dark, Flat: flat, Pedestal: 0f);
        // Build hot-pixel mask from the dark master only when drizzle is
        // forced -- mask consumption lives entirely in DrizzleStrategy
        // because applying it upstream (in Calibrator) would NaN-poison
        // the registration pass: Debayer spreads NaN through its kernel,
        // FindStars sees the NaN-bordered holes as degenerate geometry,
        // and StarQuadList trips on coincident-point divisions. Drizzle
        // is also the only strategy that benefits -- the standard path's
        // MeanCombiner sigma-clips hot-pixel outliers across N frames
        // already, so the mask is a net loss there. One-time cost per
        // group; ~tens of ms even on full-frame.
        // Build the per-channel hot-pixel mask whenever a dark + sigma are
        // available. Strategy selection happens later and an auto-picked
        // drizzle would otherwise miss the mask entirely (the previous
        // ForcedStrategy==BayerDrizzle gate was a bug: TilePipelinedDrizzle
        // and auto-picked drizzle both consume the mask but neither path
        // satisfied the gate, so they ran with NO hot-pixel rejection and
        // visible hot-pixel clusters survived into the master). Non-drizzle
        // strategies ignore IntegrationJob.BadPixelMask anyway -- their
        // MeanCombiner sigma-clip handles outliers across N frames -- so
        // unconditional construction costs ~tens of ms and is free for
        // them.
        BitMatrix[]? badPixelMask = null;
        if (dark is not null && options.HotPixelSigma > 0f)
        {
            badPixelMask = BadPixelDetection.BuildMaskFromDark(dark, options.HotPixelSigma, logger);
            var maskedCount = BadPixelDetection.CountMaskedPixels(badPixelMask, dark.Width, dark.Height);
            logger.LogInformation("  hot-pixel mask: {Count} px flagged at sigma={Sigma:F1}",
                maskedCount, options.HotPixelSigma);
        }
        // The session-derived bad-pixel accumulator (task #22): counts per-sensor-pixel outlier
        // persistence across the lights during the measure pass, so BuildMask (after registration,
        // when the transforms can prove the session moved) can flag the defects the dark never
        // showed -- the measured A/B left 35 of 52 residual clusters standing on dark-derived
        // masking alone, six of them byte-identical to the unmasked run. Only built when drizzle is
        // in play (only the drizzle strategies consume IntegrationJob.BadPixelMask; the standard
        // path's sigma-clip handles outliers across frames) and masking is enabled at all. The
        // per-frame outlier sigma shares options.HotPixelSigma with the dark detector, so one knob
        // (and zero) governs both producers.
        var drizzleMinFrames = options.DrizzleOptions?.MinFrameCount ?? DrizzleStrategy.AutoSelectMinFrameCount;
        var drizzlePlausible = key.CalibrationKey.SensorType == SensorType.RGGB
            && (options.ForcedStrategy is IntegrationStrategyKind.BayerDrizzle or IntegrationStrategyKind.TilePipelinedDrizzle
                || (!options.DisableBayerDrizzle && lightList.Count >= drizzleMinFrames));
        var badPixelAccumulator = drizzlePlausible && options.HotPixelSigma > 0f
            ? new BadPixelAccumulator(options.HotPixelSigma)
            : null;

        // 3a. Pick reference by composite PSF quality.
        //
        // Score = StarCount / (max(HFD, 1) * (1 + 4 * Ellipticity)).
        // Picks the frame with the most stars, weighted down by broad
        // PSF (HFD) and elongation (ecc). Rewards sharp-round-many-stars
        // simultaneously. A bloated frame with 10000 stars loses to a
        // sharp frame with 5000 stars whenever the HFD difference is
        // >2x; an elongated frame is penalised regardless of count
        // (factor 5 at ecc=1, factor 3 at ecc=0.5). Pre-refactor logic
        // picked by star count alone, which let low-altitude bloated
        // early frames win on dense fields even when their PSF was 30%
        // broader -- bad reference for the rest of the session to
        // register against.
        progress?.Report(new StackingProgress(StackingPhase.Registering, slug, 0, lightList.Count));
        var measureStart = StageTimings.Start();
        long measuredPixels = 0;
        var frameCandidates = new List<(FrameInfo Frame, FrameMetrics Metrics, float Score, StarList Stars)>(lightList.Count);
        foreach (var lf in lightList)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await lf.LoadFullAsync(ct);
            measuredPixels += (long)raw.Width * raw.Height;
            var calibrated = calibrator.Apply(raw);
            // BEFORE DetectAsync, whose internal debayer can rescale the calibrated frame in
            // place: the accumulator must see the same values the integration producers' own
            // Apply emits. (The outlier test is scale-invariant per frame, but seeing the
            // producer's values is the invariant worth stating.)
            badPixelAccumulator?.Accumulate(calibrated);
            // Shared detect site (FrameRegistration.DetectAsync): pre-debayer luminance, which this
            // path used to reach by detecting on channel 0 of the VNG-debayered frame. That is the
            // interpolated RED plane, and measured on a real session its top-K detections could not
            // produce even 20 mutual matches between consecutive subs where the mono route
            // reproduced at 92%.
            var (stars, _) = await FrameRegistration.DetectAsync(
                calibrated, options.CentroidDebayerAlg, options.SnrMin, options.MinStars, ct);
            var metrics = FrameRegistration.MetricsFrom(stars);
            // The star list is RETAINED (the dataset registrar's model): registration is centroid
            // work, so keeping it makes the register pass below pixel-free instead of reloading,
            // re-calibrating and re-detecting every frame this loop just measured. Centroids only,
            // tens of KB per frame -- the registrar retains them for whole 300-sub sessions.
            frameCandidates.Add((lf, metrics, FrameRegistration.ReferenceScore(metrics), stars));
        }
        timings.Record(StageNames.Measure, measureStart, lightList.Count, measuredPixels);

        // A manifest DRIVES this run rather than informing it: frame list, reference and every star
        // transform come from the earlier run, and the register pass below is skipped entirely. That
        // is what makes a starless layer possible -- StarXTerminator leaves no point sources, so a
        // starless plate has no quads and cannot be star-registered at any tolerance.
        //
        // Frames are matched by a digest of their DATA section, except that a DERIVED plate has
        // different pixels by construction, so it carries SRCDGST naming the frame it came from. A
        // raw re-run matches on its own digest and needs no such card.
        StackManifest? manifest = null;
        Dictionary<string, ManifestFrame>? manifestByDigest = null;
        Dictionary<DateTime, ManifestFrame>? manifestByEpoch = null;
        if (options.ManifestPath is { Length: > 0 } manifestPath)
        {
            manifest = await StackManifest.TryReadAsync(manifestPath, ct);
            if (manifest is null)
            {
                logger.LogWarning("  [manifest] {Path} could not be read; this group registers normally", manifestPath);
            }
            else
            {
                if (!string.Equals(manifest.Slug, slug, StringComparison.Ordinal))
                {
                    // Not fatal: a manifest is per-master, and re-stacking the same frames under a
                    // different grouping is legitimate. Worth saying out loud, because the alternative
                    // reading is that the wrong file was passed.
                    logger.LogWarning(
                        "  [manifest] slug is \"{ManifestSlug}\" but this group is \"{Slug}\"; using it anyway",
                        manifest.Slug, slug);
                }
                manifestByDigest = manifest.MatchedByDigest();
                manifestByEpoch = BuildEpochIndex(manifest, logger);
                logger.LogInformation(
                    "  [manifest] {Path}: {Matched} selectable of {Total} frames, reference {Ref}, epoch fallback {Epoch}",
                    manifestPath, manifestByDigest.Count, manifest.Frames.Length,
                    Path.GetFileNameWithoutExtension(manifest.ReferencePath),
                    manifestByEpoch is null ? "unavailable (duplicate epochs)" : "available");
            }
        }
        // Digest every candidate ONCE, here, so both the reference lookup and the per-frame
        // transform lookup below read the same map.
        Dictionary<string, FrameInfo>? candidateByDigest = null;
        if (manifest is not null)
        {
            candidateByDigest = new Dictionary<string, FrameInfo>(frameCandidates.Count, StringComparer.Ordinal);
            var digested = new string[frameCandidates.Count];
            Parallel.For(0, frameCandidates.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                digested[i] = FrameProvenance.SourceDigestOf(frameCandidates[i].Frame.Path);
            });
            for (var i = 0; i < frameCandidates.Count; i++)
            {
                if (digested[i].Length > 0)
                {
                    candidateByDigest[digested[i]] = frameCandidates[i].Frame;
                }
            }
        }

        // Reference selection: explicit ReferenceFrameHint wins (substring
        // match on path, first hit), otherwise composite-quality score.
        // The hint is a debug knob for isolating Bayer-drizzle artifacts
        // that correlate with reference choice -- pinning to a frame near
        // the temporal MIDDLE of the session keeps per-frame rotation
        // residuals symmetric around zero so per-channel drizzle coverage
        // stays balanced.
        FrameInfo? reference = null;
        // A manifest's reference OUTRANKS both the hint and the score, and a miss is fatal rather
        // than a fallback. Silently picking a different reference is the exact failure the manifest
        // exists to prevent: a different canvas origin and orientation, two layers that do not
        // overlay, and a screen combine that is meaningless rather than merely inconsistent. That
        // is invisible in every per-frame log line, so it has to stop the run.
        if (manifest is not null && candidateByDigest is not null)
        {
            if (!candidateByDigest.TryGetValue(manifest.ReferenceDigest, out var manifestRef))
            {
                var reason =
                    $"manifest reference {Path.GetFileName(manifest.ReferencePath)} " +
                    $"(digest {manifest.ReferenceDigest[..Math.Min(12, manifest.ReferenceDigest.Length)]}) " +
                    $"is not among this run's {frameCandidates.Count} frames";
                logger.LogError("  [manifest] {Reason}", reason);
                return new GroupResult(slug, lightList.Count, 0, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: reason,
                    Stages: timings.Snapshot());
            }
            reference = manifestRef;
            logger.LogInformation("  [manifest] reference pinned to {File}", Path.GetFileName(reference.Path));
        }
        if (reference is null && !string.IsNullOrEmpty(options.ReferenceFrameHint))
        {
            var hint = options.ReferenceFrameHint;
            var match = frameCandidates.FirstOrDefault(c =>
                c.Frame.Path.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match.Frame is not null)
            {
                reference = match.Frame;
                logger.LogInformation("  [refHint] pinning reference to {File} (hint=\"{Hint}\")",
                    Path.GetFileName(reference.Path), hint);
            }
            else
            {
                logger.LogWarning("  [refHint] no candidate path matches \"{Hint}\"; falling back to score-based pick", hint);
            }
        }
        if (reference is null)
        {
            var bestScore = float.NegativeInfinity;
            foreach (var c in frameCandidates)
            {
                if (c.Score > bestScore) { bestScore = c.Score; reference = c.Frame; }
            }
        }
        if (reference is null)
        {
            logger.LogWarning("  [skip] no reference frame could be picked");
            return new GroupResult(slug, lightList.Count, 0, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: "no reference frame could be picked",
                Stages: timings.Snapshot());
        }
        var refCandidate = frameCandidates.First(s => s.Frame.Path == reference.Path);
        logger.LogInformation("  reference: {File} (stars={Stars} hfd={Hfd:F2} ecc={Ecc:F3} score={Score:F1}, {ElapsedMs} ms)",
            Path.GetFileName(reference.Path),
            refCandidate.Metrics.StarCount,
            refCandidate.Metrics.MedianHfd,
            refCandidate.Metrics.MedianEllipticity,
            refCandidate.Score,
            (long)Stopwatch.GetElapsedTime(measureStart).TotalMilliseconds);

        // The reference raw stays loaded once: its meta + dims feed the drizzle gate, the canvas
        // and the post-processor. Registration itself no longer reads it -- the register phase
        // works entirely from the star lists the measure pass retained (the reference's included),
        // which is what deleted this method's second detect-per-frame pass. Reference prep charges
        // to the register stage, mirroring the registrar (its register stage opens with the
        // reference quad build).
        var registerStart = StageTimings.Start();
        var referenceRaw = await reference.LoadFullAsync(ct);
        // Grab the FITS header WCS as a plate-solve search hint. N.I.N.A.
        // captures usually stamp approximate RA/DEC keywords; we pass
        // these to CatalogPlateSolver so it knows where to look.
        WCS? searchHint = null;
        if (!Image.TryReadFitsFile(reference.Path, out _, out searchHint))
        {
            logger.LogWarning("  [warn] couldn't reread ref FITS for WCS hint: {Path}", reference.Path);
        }
        // Comet mode: the canvas rate either came from the caller or is derived HERE, before any
        // frame is registered, because the compose below needs it per frame. Deriving it needs the
        // reference frame's own WCS, which is why this is the one place in the pipeline that
        // plate-solves anything other than the finished master -- registration itself is
        // frame-to-frame star matching and never needed to know where the sky was.
        //
        // An explicit rate wins over a designation, so --comet-rate stays the offline answer and the
        // override for an ephemeris that turns out to be wrong.
        var cometRate = options.CometRatePxPerHour;
        // The full fit, kept alongside the bare rate because the STAR layer needs the one thing a
        // rate cannot carry: where the body actually IS. A manual --comet-rate has no anchor by
        // construction (a rate is a difference, and the flag states only the difference), so that
        // path can register on the comet but cannot mask it out of the companion layer.
        CometRate? cometFit = null;
        if (cometRate is null && options.CometDesignation is not null)
        {
            cometFit = await TryResolveCometRateAsync(lightList, referenceRaw, searchHint, ct);
            cometRate = cometFit?.PxPerHour;
            if (cometRate is null)
            {
                // REFUSE, never fall back to a star-aligned stack. The caller asked for the body to
                // be registered; a star-aligned master is a different product that happens to land
                // at the same path, carry the same slug and look entirely plausible. Nothing
                // downstream can tell the two apart -- the header records the strategy, not what the
                // registration was ON -- so the fallback turned a failed ephemeris lookup into a
                // silently wrong deliverable. That is the same failure class as an AI enhancer
                // handed out-of-distribution input: a confident answer to a question nobody asked.
                //
                // Found on C/2025 R2 (SWAN), where the designation was being sent to Horizons in its
                // compact form and rejected. The run went on to spend its whole integration
                // producing a duplicate of a master that already existed.
                var wanted = string.IsNullOrWhiteSpace(options.CometDesignation)
                    ? referenceRaw.ImageMeta.ObjectName
                    : options.CometDesignation;
                var reason = $"comet registration was requested for \"{wanted}\" but no rate could be derived; "
                    + "pass --comet-rate dx,dy to supply one, or drop --comet to stack star-aligned on purpose";
                logger.LogWarning("  [skip] {Reason}", reason);
                return new GroupResult(slug, lightList.Count, 0, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: reason,
                    Stages: timings.Snapshot());
            }
        }

        // No registrar at all under a manifest. Not merely unnecessary: for a starless layer the
        // reference itself has no stars, so building reference quads from its list is meaningless
        // work on an empty list.
        using var registerLoop = manifest is null
            ? await RegisterLoop.CreateAsync(refCandidate.Stars, options.QuadStars, ct)
            : null;
        // State the fingerprint set on SUCCESS, not only when registration collapses. Both counts
        // were already computed here and thrown away, and their absence is what makes the
        // still-pending port of the dataset builder's two registration fixes (detect pre-debayer
        // rather than on an interpolated colour plane; cap QuadStars at the bright end) unmeasurable
        // on this path: the effect shows up as a change in reference stars and quads BEFORE it shows
        // up as a change in how many frames register, and a run that registers everything both ways
        // would otherwise look identical. Mirrors SessionRegistrar's line of the same shape so the
        // two paths' logs can be read side by side.
        if (registerLoop is not null)
        {
            logger.LogInformation("  reference {File} stars={Stars} quads={Quads} (top {Cap})",
                Path.GetFileName(reference.Path), registerLoop.ReferenceStarCount, registerLoop.ReferenceQuadCount, options.QuadStars);
        }
        // Reference-frame metrics so the matched tuple gets a real
        // FrameMetrics even for the reference (which never goes through
        // the register loop). Used by the post-registration quality
        // filter -- without it the reference would be a (0,0,0) outlier
        // and always survive even if it's actually the worst.
        var referenceMetrics = refCandidate.Metrics;

        // Per-group staging dir. Cleaned up by the chosen strategy.
        var stagingDir = Path.Combine(outputDir, "_staging", slug);
        // Diagnostic per-frame dumps (--save-calibrated / --save-normalized). Built once and
        // handed to the strategies; null when neither flag is set, so the off path allocates
        // nothing and every existing run is byte-identical.
        var intermediates = options.SaveCalibrated || options.SaveNormalized
            ? new IntermediateFrameWriter(stagingDir, options.SaveCalibrated, options.SaveNormalized, logger)
            : null;

        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        Directory.CreateDirectory(stagingDir);

        // 3b. Per-light: register the RETAINED star lists against the reference through the shared
        // RegisterLoop (min-stars floor -> quad-form -> tolerance ladder -> rigid refine, plus the
        // census + skip counters -- the same loop body the dataset registrar runs). No pixel is
        // read in this phase any more; the warp producer loads frames when integration streams.
        // The skip split by CAUSE matters because a bare tally cannot separate a DETECTION problem
        // (few quads anywhere) from a PURITY one (plenty of quads on both sides that still do not
        // correspond), and those have opposite fixes.
        // Starless rides ALONGSIDE the original rather than replacing it: the comet layer wants the
        // star-removed plate and the star layer wants its stars, and one run now builds both.
        var matched = new List<(FrameInfo Light, FrameInfo? Starless, Matrix3x2 Transform, Matrix3x2 StarTransform, FrameMetrics Metrics)>();
        // Every candidate's fate and its STAR solution, for the manifest. Parallel to `matched` only
        // for the frames that survive; a skipped frame is recorded here and absent there, which is the
        // whole point (see StackManifest: "considered and rejected" and "never offered" differ).
        var manifestFates = new List<(FrameInfo Frame, FrameFate Fate, Matrix3x2? Star)>(frameCandidates.Count);
        var skipCount = 0;
        foreach (var candidate in frameCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(candidate.Frame.Path);

            Matrix3x2? transform;
            var frameMetrics = candidate.Metrics;
            if (string.Equals(candidate.Frame.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
            {
                transform = Matrix3x2.Identity;
                frameMetrics = referenceMetrics;
                registerLoop?.AddReference(referenceMetrics);
                manifestFates.Add((candidate.Frame, FrameFate.Matched, Matrix3x2.Identity));
            }
            else if (manifestByDigest is not null)
            {
                // Selection AND transform both come from the manifest. A frame it does not list as
                // matched is dropped here rather than registered on its own: the layers must agree on
                // depth, and a frame the star layer rejected for bad stars is precisely the one that
                // must not quietly contribute to the comet layer.
                var digest = FrameProvenance.SourceDigestOf(candidate.Frame.Path);
                ManifestFrame? entry = null;
                if (digest.Length > 0)
                {
                    _ = manifestByDigest.TryGetValue(digest, out entry);
                }
                // A DERIVED plate has different pixels by construction, so it can only be matched by
                // provenance. SRCDGST states it outright and is preferred; failing that, the exposure
                // epoch is a join that costs nothing, because sxt preserves DATE-OBS and the manifest
                // already records it. The uniqueness of those epochs is checked ONCE when the map is
                // built -- a duplicated epoch would silently hand one frame another's transform, so
                // the map is simply not offered in that case.
                entry ??= manifestByEpoch is null
                    ? null
                    : manifestByEpoch.TryGetValue(candidate.Frame.Meta.ExposureStartTime.UtcDateTime, out var byEpoch) ? byEpoch : null;
                if (entry?.AsMatrix() is { } fromManifest)
                {
                    transform = fromManifest;
                    manifestFates.Add((candidate.Frame, FrameFate.Matched, fromManifest));
                }
                else
                {
                    transform = null;
                    manifestFates.Add((candidate.Frame, FrameFate.SkippedQualityReject, null));
                    logger.LogInformation("  [{Name}] -> SKIP (not a matched frame in the manifest)", name);
                }
            }
            else
            {
                var attempt = await registerLoop!.RegisterAsync(candidate.Stars, candidate.Metrics, ct);
                transform = attempt.Transform;
                manifestFates.Add((candidate.Frame, attempt.Skip switch
                {
                    RegisterLoop.SkipCause.TooFewStars => FrameFate.SkippedTooFewStars,
                    RegisterLoop.SkipCause.NoQuadFit => FrameFate.SkippedNoQuadFit,
                    _ => FrameFate.Matched,
                }, attempt.Transform));
                switch (attempt.Skip)
                {
                    case RegisterLoop.SkipCause.TooFewStars:
                        logger.LogInformation("  [{Name}] stars={Stars} -> SKIP (too few stars)",
                            name, candidate.Stars.Count);
                        break;
                    case RegisterLoop.SkipCause.NoQuadFit:
                        logger.LogInformation(
                            "  [{Name}] stars={Stars} quads={Quads} vs reference quads={RefQuads} -> SKIP (no quad fit at any tolerance)",
                            name, candidate.Stars.Count, attempt.LightQuads, registerLoop.ReferenceQuadCount);
                        break;
                    default:
                        logger.LogInformation(
                            "  [{Name}] stars={Stars} quads={Quads} hfd={Hfd:F2} fwhm={Fwhm:F2} ecc={Ecc:F3} -> MATCH qt={Tol:F3} refine: rot={Rot:F3}° s={Scale:F5} t=({Tx:F2},{Ty:F2}) rms={Rms:F2}px from {RefMatched} pairs",
                            name, candidate.Stars.Count, attempt.LightQuads,
                            frameMetrics.MedianHfd, frameMetrics.MedianFwhm, frameMetrics.MedianEllipticity,
                            attempt.QuadTolerance, attempt.RefineRotationDeg, attempt.RefineScale,
                            attempt.RefineTx, attempt.RefineTy, attempt.RefineRmsPx, attempt.RefineMatchedPairs);
                        break;
                }
            }
            // Comet tracking: re-reference this frame onto the MOVING target. The star solution put it
            // on the reference's star grid; subtracting the target's drift since the reference epoch
            // pins the target instead. Composed AFTER the star solution so it acts in canvas space,
            // which is the only basis where the target's motion is separable from dither and field
            // rotation. The reference frame needs no special case: its dt is zero.
            //
            // The star solution is KEPT rather than overwritten, because the companion star layer is
            // that same registration without the compose. Re-deriving it later by composing the
            // inverse translation would be exact arithmetic and still wrong as a design: it would put
            // the definition of the compose in two places, and the second copy only breaks when the
            // first one changes.
            var starOnly = transform;
            if (cometRate is { } cometDrift && transform is { } starSolution)
            {
                transform = CometCompose.ToCometGrid(
                    starSolution, cometDrift, CometCompose.DriftHours(candidate.Frame.Meta, reference.Meta));
            }

            if (transform is null) { skipCount++; continue; }

            matched.Add((candidate.Frame, null, transform.Value, starOnly ?? transform.Value, frameMetrics));
            progress?.Report(new StackingProgress(StackingPhase.Registering, slug, matched.Count + skipCount, lightList.Count));
        }
        // One census, reused by the summary line here and the collapse warning below -- two
        // renderings of the same numbers is how one of them ends up stale (the registrar learned
        // that with its min/max quad range, which is inside the census now).
        var registrationCensus = registerLoop is null
            ? "n/a (manifest)"
            : RegistrationCensus.Describe(registerLoop.MeasureCensus());
        if (registerLoop is null)
        {
            logger.LogInformation(
                "  adopted {Matched}/{Attempted} transforms from the manifest (skipped {Skipped} not listed as matched) in {ElapsedMs} ms",
                matched.Count, lightList.Count, skipCount,
                (long)Stopwatch.GetElapsedTime(registerStart).TotalMilliseconds);
        }
        else
        {
            logger.LogInformation(
                "  registered {Matched}/{Attempted} frames (skipped {Skipped}: {TooFew} too-few-stars, {NoFit} no-quad-fit) in {ElapsedMs} ms; census {Census}",
                matched.Count, lightList.Count, skipCount, registerLoop.SkippedTooFewStars, registerLoop.SkippedNoQuadFit,
                (long)Stopwatch.GetElapsedTime(registerStart).TotalMilliseconds, registrationCensus);
        }
        // Items = every frame attempted (a failed match still cost its quad-form + tolerance
        // ladder). Pixels are ZERO now, same as the registrar's register stage: this phase works
        // from the retained star lists, and the reload + re-detect pass whose pixel throughput used
        // to be recorded here is the double-detect the collapse removed (task #21). Pinned by the
        // synthetic pipeline test.
        timings.Record(StageNames.Register, registerStart, lightList.Count);
        hostTracker.Log(logger, $"register/{slug}");

        // Post-registration quality filter. Off by default; enable via
        // StackingOptions.QualityRejectSigma. Drops frames whose median
        // HFD or ellipticity exceeds the session's median + sigma * MAD
        // threshold, capped at the worst 20% by severity (the 80% keep
        // floor in FrameQualityFilter). One log line per dropped frame
        // so the audit is per-frame, not just a count.
        if (options.QualityRejectSigma is { } qSigma && qSigma > 0f && matched.Count >= 4)
        {
            var metricsArr = new FrameMetrics[matched.Count];
            for (var i = 0; i < matched.Count; i++) metricsArr[i] = matched[i].Metrics;
            var filterResult = FrameQualityFilter.Filter(metricsArr, qSigma);
            if (filterResult.KeptCount < matched.Count)
            {
                if (filterResult.FloorTriggered)
                {
                    logger.LogInformation(
                        "  [quality] sigma={Sigma:F2} -- FLOOR triggered: MAD threshold would over-cut, capped to worst {N}/{Total} by severity",
                        qSigma, matched.Count - filterResult.KeptCount, matched.Count);
                }
                else
                {
                    logger.LogInformation(
                        "  [quality] sigma={Sigma:F2}: rejecting {N}/{Total} frames",
                        qSigma, matched.Count - filterResult.KeptCount, matched.Count);
                }
                // No calibrated-frame cache rides alongside matched any more, which retires a whole
                // failure class by construction: the index-keyed cache used to need rebuilding here
                // in lockstep with the filtered list, and skipping that paired new matched[K+] with
                // OLD cache[K+]'s pixels -- the wrong calibrated frame under the right transform,
                // visible as chromatic speckle on SoL pier-side drizzle masters. The producers load
                // frames themselves when integration streams, indexed off the final matched list.
                var filtered = new List<(FrameInfo Light, FrameInfo? Starless, Matrix3x2 Transform, Matrix3x2 StarTransform, FrameMetrics Metrics)>(filterResult.KeptCount);
                for (var i = 0; i < matched.Count; i++)
                {
                    var reason = filterResult.Reasons[i];
                    if (reason == FrameRejectReason.Kept)
                    {
                        filtered.Add(matched[i]);
                    }
                    else
                    {
                        var m = matched[i].Metrics;
                        var rejName = Path.GetFileNameWithoutExtension(matched[i].Light.Path);
                        logger.LogInformation(
                            "  [quality] reject {Name} reason={Reason} hfd={Hfd:F2} ecc={Ecc:F3} stars={Stars}",
                            rejName, reason, m.MedianHfd, m.MedianEllipticity, m.StarCount);
                        // The manifest must say REJECTED rather than matched, or the other layer
                        // silently includes a frame this one threw away and the two differ in depth.
                        for (var f = 0; f < manifestFates.Count; f++)
                        {
                            if (ReferenceEquals(manifestFates[f].Frame, matched[i].Light))
                            {
                                manifestFates[f] = (manifestFates[f].Frame, FrameFate.SkippedQualityReject, manifestFates[f].Star);
                                break;
                            }
                        }
                    }
                }
                matched = filtered;
            }
            else
            {
                logger.LogInformation("  [quality] sigma={Sigma:F2}: no frames rejected", qSigma);
            }
        }

        if (matched.Count < 2)
        {
            // WARNING level is what a run's log actually shows, so it has to be self-diagnosing:
            // the census separates a detection problem (few quads anywhere) from a purity one
            // (plenty of quads on both sides that do not correspond), and those have opposite
            // fixes. Same shape as SessionRegistrar's collapse warning, so the two paths' logs
            // read side by side.
            if (registerLoop is null)
            {
                logger.LogWarning(
                    "  [skip] fewer than 2 matched frames; integration would be meaningless. reference {RefFile}, and the manifest selected too few of this run's frames -- check it describes these frames",
                    Path.GetFileName(reference.Path));
            }
            else
            {
                logger.LogWarning(
                    "  [skip] fewer than 2 matched frames; integration would be meaningless. reference {RefFile} stars={RefStars} quads={RefQuads}, skipped {TooFew} too-few-stars + {NoFit} no-quad-fit. census {Census}",
                    Path.GetFileName(reference.Path), registerLoop.ReferenceStarCount, registerLoop.ReferenceQuadCount,
                    registerLoop.SkippedTooFewStars, registerLoop.SkippedNoQuadFit, registrationCensus);
            }
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
            return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: "fewer than 2 matched frames",
                Stages: timings.Snapshot());
        }

        // Per-frame star removal, the comet LAYER (artifact 3 of docs/plans/comet-integration.md).
        //
        // <para>Placed HERE, between registration and integration, because that is the only point
        // where both halves are true: the frames have already been registered WITH their stars (a
        // starless plate has no quads and cannot be registered at all), and nothing has integrated
        // yet. Removing the stars per frame before integrating is what keeps the trails out, rather
        // than trying to reject them statistically afterwards.</para>
        //
        // <para><b>sxt must see CALIBRATED pixels.</b> It is a network trained on astronomical
        // images, and a raw frame carries a pedestal, dark current, hot pixels and vignetting -- hot
        // pixels look like faint stars, and vignetting moves the local background it separates star
        // from sky against. So each frame is calibrated here and the starless result is written out
        // ALREADY CALIBRATED, and integration is then handed a no-op calibrator. Calibrating twice
        // is the failure this avoids, and it would be silent: bias and dark subtracted twice, flat
        // divided twice, and a master that merely looks a bit wrong.</para>
        var integrationCalibrator = calibrator;
        if (options.RemoveStarsPerFrame)
        {
            if (starRemover is null)
            {
                var reason = "--remove-stars needs an IStarRemover; register one (AddRcAstroAi() or AddTianWenAi()) in the composition root";
                logger.LogError("  [starless] {Reason}", reason);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed, SkipReason: reason, Stages: timings.Snapshot());
            }

            // Beside the calibration-master cache, NOT under _staging: staging is wiped at the start
            // of every group, and these plates are the one intermediate worth keeping across runs into
            // the same output directory (see the reuse below). Each carries SRCDGST + STARMODE, which
            // is what makes reuse safe, and a TianWen SWCREATE, which is what keeps the scan from ever
            // ingesting one as a light.
            var starlessDir = Path.Combine(outputDir, "starless", slug);
            Directory.CreateDirectory(starlessDir);
            var starlessStart = StageTimings.Start();
            long starlessPixels = 0;
            var reusedPlates = 0;
            for (var i = 0; i < matched.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (light, _, transform, starTransform, frameMetrics) = matched[i];
                var starlessPath = Path.Combine(starlessDir, Path.GetFileNameWithoutExtension(light.Path) + "_starless.fits");
                var sourceDigest = FrameProvenance.SourceDigestOf(light.Path);

                // Reuse a plate an earlier run into this output directory already made of THIS frame in
                // THIS mode. Star removal is deterministic in its inputs and costs ~7 s a frame, so
                // iterating on anything downstream of it (the rejector, the star layer, the composite)
                // would otherwise pay ten minutes per attempt for pixels that cannot come out different.
                // Identity is the digest, never the filename: the plate carries SRCDGST for exactly this.
                // A plate with no mode card predates the card and was made in the default mode.
                if (File.Exists(starlessPath)
                    && FrameProvenance.TryReadSourceDigest(starlessPath) == sourceDigest
                    && (FrameProvenance.TryReadCard(starlessPath, StarRemovalModeKeyword) ?? nameof(StarRemovalMode.Mosaic))
                        == options.StarRemovalMode.ToString())
                {
                    reusedPlates++;
                    matched[i] = (light, light with { Path = starlessPath }, transform, starTransform, frameMetrics);
                    continue;
                }

                var calibrated = RawLightDecoder.DecodeCalibrate(new RawLightSource(light.Path, transform), calibrator, nameof(StackingPipeline), intermediates);
                Image starless;
                try
                {
                    // sxt must see [0, 1] pixels, not ADU. A star remover normalises internally and
                    // CLIPS what is already above its range, so handing it a 16-bit calibrated frame
                    // (sky background ~3900 ADU) returns a plate that is uniformly 1.0 -- every pixel
                    // white, no error, no warning. Measured on a real sub: ADU in (min 1796 / med 3928
                    // / max 65535) came back min = med = max = 1, while the same frame divided by
                    // 65535 came back min 0.028 / med 0.055 / max 0.14, which is a correct starless
                    // plate (background preserved, saturated peaks gone with the stars).
                    //
                    // The divisor is the CONTAINER full scale, not the frame's own peak: every frame
                    // in the group must be divided by the same number or the integration sums
                    // inconsistently scaled data. See ScaleToFullScaleInPlace.
                    var fullScale = light.BitDepth.UnsignedFullScale is { } scale ? (float)scale : calibrated.MaxValue;
                    starless = await RemoveStarsFromFrameAsync(
                        calibrated.ScaleToFullScaleInPlace(fullScale), options.StarRemovalMode, starRemover, logger, ct);
                }
                finally
                {
                    // Apply consumed the raw and handed us ownership; the enhancer returns a new
                    // plate, so the calibrated input is ours to release either way.
                    calibrated.Release();
                }

                starlessPixels += (long)starless.Width * starless.Height;
                starless.WriteToFitsFile(starlessPath, null, new Dictionary<string, (object Value, string Comment)>
                {
                    // Provenance, so the plate is not mistakable for a light if it outlives the run,
                    // and so a manifest-driven consumer can match it back without a filename rule.
                    [FrameProvenance.SourceDigestKeyword] = (sourceDigest, "Data digest of the frame this was derived from"),
                    ["STARLESS"] = (true, "Stars removed per-frame before integration"),
                    [StarRemovalModeKeyword] = (options.StarRemovalMode.ToString(), "How the CFA frame was shaped for the star remover"),
                    // Ours, so the scan's provenance skip drops it if it ever lands beside the lights;
                    // the star remover otherwise hands back the capture software's own SWCREATE.
                    ["SWCREATE"] = (IntegrationFitsWriter.SoftwareCreator, "Software that created this plate"),
                    // The plate is [0, 1] by construction (above), so DECLARE that rather than leave
                    // the integrator to infer a scale from the plate's own observed peak. A starless
                    // plate's peak is far below full scale -- the brightest thing in the frame was a
                    // star and the stars are gone -- so an inferred scale would divide the master by
                    // ~0.14 and land it 7x brighter than the star-aligned master it has to be
                    // screen-combined with. SATURATE is what Image.TryReadFitsFile parses back into
                    // ImageMeta.SensorFullScaleAdu, hence into UnitScaleDivisor.
                    ["SATURATE"] = (1.0, "Full scale of these pixels; the plate is unit-referred"),
                });
                starless.Release();

                // Keep the original. Replacing it here is what used to make --remove-stars unusable for a
                // two-layer run: every later consumer, the star layer included, saw only starless plates.
                matched[i] = (light, light with { Path = starlessPath }, transform, starTransform, frameMetrics);
                if ((i + 1) % 15 == 0 || i + 1 == matched.Count)
                {
                    logger.LogInformation("  [starless] {Done}/{Total}", i + 1, matched.Count);
                }
            }
            timings.Record(StageNames.Measure, starlessStart, matched.Count, starlessPixels);
            // Everything downstream reads ALREADY-CALIBRATED pixels from here on.
            integrationCalibrator = new Calibrator(null, null, null);
            logger.LogInformation(
                "  [starless] {Count} frames in {Dir} ({Reused} reused from an earlier run) in {ElapsedMs} ms; integration calibration disabled",
                matched.Count, starlessDir, reusedPlates, (long)Stopwatch.GetElapsedTime(starlessStart).TotalMilliseconds);
        }

        // BayerDrizzle is opt-in only (--strategy BayerDrizzle). Gate up
        // front so we fail fast with a clear reason rather than producing
        // a NaN-riddled master at low frame count or on a Mono / Color
        // sensor where the per-pixel Bayer dispatch is meaningless. Both
        // checks would otherwise sneak through into RunAsync and produce
        // either a useless master (low N) or a wrong-channel-assignment
        // master (non-RGGB).
        // Both drizzle variants share the same algorithmic preconditions
        // (RGGB sensor for Bayer dispatch + enough matched frames for
        // robust R/B coverage); only memory layout differs. Gate them
        // identically.
        if (options.ForcedStrategy is IntegrationStrategyKind.BayerDrizzle
            or IntegrationStrategyKind.TilePipelinedDrizzle)
        {
            var drizzleOpts = options.DrizzleOptions ?? new DrizzleOptions();
            var kindName = options.ForcedStrategy.Value;
            if (referenceRaw.ImageMeta.SensorType != SensorType.RGGB)
            {
                logger.LogWarning("  [skip] {Kind} requires SensorType.RGGB (got {Sensor})",
                    kindName, referenceRaw.ImageMeta.SensorType);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed,
                    SkipReason: $"{kindName} requires SensorType.RGGB (got {referenceRaw.ImageMeta.SensorType})",
                    Stages: timings.Snapshot());
            }
            if (matched.Count < drizzleOpts.MinFrameCount)
            {
                logger.LogWarning("  [skip] {Kind} needs >= {Min} matched frames (got {Got}); drizzle coverage would be too sparse",
                    kindName, drizzleOpts.MinFrameCount, matched.Count);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                    PreviewPngPath: null, Elapsed: groupSw.Elapsed,
                    SkipReason: $"{kindName} requires >= {drizzleOpts.MinFrameCount} matched frames (got {matched.Count})",
                    Stages: timings.Snapshot());
            }
        }

        // Compute the union bounding box of all matched frames' source
        // footprints in reference space + per-frame canvas-space AABBs +
        // intersection rectangle for stretch stats.
        var transforms = matched.ConvertAll(m => m.Transform);

        // The session-derived map joins the dark-derived one as a UNION when it can be built
        // (enough frames, the transforms prove real dither/drift, flagged fraction sane): the two
        // flag nearly DISJOINT real populations (see BadPixelAccumulator.UnionInto), so preferring
        // either alone discards a measured good. BuildMask refuses with a logged reason otherwise
        // and the dark mask stands alone.
        if (badPixelAccumulator?.BuildMask(transforms, logger) is { } registrationMask)
        {
            if (badPixelMask is { Length: > 0 } darkMask
                && dark is not null && dark.Width == referenceRaw.Width && dark.Height == referenceRaw.Height)
            {
                var (shared, regOnly, darkOnly) = BadPixelAccumulator.Overlap(
                    registrationMask[0], darkMask[0], referenceRaw.Width, referenceRaw.Height);
                logger.LogInformation(
                    "  bad-pixel masks: {Shared} px shared, {RegOnly} registration-only, {DarkOnly} dark-only; masking their union",
                    shared, regOnly, darkOnly);
                BadPixelAccumulator.UnionInto(registrationMask[0], darkMask[0], referenceRaw.Width, referenceRaw.Height);
            }
            badPixelMask = registrationMask;
        }

        // One layer's worth of integrate-and-write. A comet run emits TWO masters, and they are
        // combinable only if they agree on the reference frame, the canvas origin, the debayer, the
        // rejector and the frame set -- so both come from this one function, differing only in the
        // four arguments. Two separate runs can differ in every one of those, which is why this is
        // not simply "run the tool twice".
        string? layerSkipReason = null;
        async Task<(string MasterPath, MasterWriteResult Post, int MaskedPixels,
                    IntegrationResult Integration, Matrix3x2 CanvasShift,
                    Rectangle StatsRect, IntegrationStrategyKind Strategy)?> IntegrateLayerAsync(
            IReadOnlyList<Matrix3x2> layerTransforms,
            CometMask? layerMask,
            CometModel? layerModel,
            bool useStarless,
            string layerSuffix,
            AlignmentProvenance layerAlignment,
            CancellationToken token)
        {
            // Which plate this layer consumes, and therefore which calibrator. The starless plates
            // were calibrated before star removal, so calibrating them again would subtract bias and
            // dark twice and divide by the flat twice; the originals still need the real thing. With
            // no --remove-stars the two are the same object and this is a no-op distinction.
            var layerCalibrator = useStarless ? integrationCalibrator : calibrator;
            var maskedFramePixels = 0;
            double[]? modelScaleSum = null;
            var modelScaleCount = 0;
            var (canvasShift, outOriginX, outOriginY, outWidth, outHeight) =
                CanvasGeometry.ComputeUnionCanvas(layerTransforms, referenceRaw.Width, referenceRaw.Height);
            logger.LogInformation("  [canvas] union bbox = {W}x{H} (origin {X},{Y} in ref space)",
                outWidth, outHeight, outOriginX, outOriginY);

            var (frameFootprints, statsRect) = CanvasGeometry.ComputeFootprintsAndStatsRect(
                layerTransforms, canvasShift, referenceRaw.Width, referenceRaw.Height, outWidth, outHeight);

            // One preparation for BOTH producers: load, calibrate for this layer, and take the body
            // out of the raw CFA frame (subtract it, or blank it) before anything debayers or warps.
            // Doing it on the raw mosaic is what lets one implementation serve both integration
            // paths: the standard path warps afterwards, the drizzle path forward-projects the same
            // CFA samples, and both skip a NaN without depositing weight, so coverage needs nothing
            // added to either. This was two copies of the same block once, and a fix to one of them
            // was a fix to half the strategies.
            var bodyOnGrid = cometFit is { } gridFit ? CometCompose.BodyOnGrid(gridFit, reference.Meta) : default;
            // One amplitude PER CHANNEL, per frame (see below); allocated once here and refilled per
            // frame, since the producers hand out one frame at a time.
            var amplitudes = new float[layerModel?.ChannelCount ?? 0];
            var coreAmplitudes = new float[layerModel?.ChannelCount ?? 0];
            async ValueTask<(Image Calibrated, Matrix3x2 TransformOrig)> PrepareFrameAsync(int fi, CancellationToken token)
            {
                var lightInfo = useStarless && matched[fi].Starless is { } sl ? sl : matched[fi].Light;
                var transformOrig = layerTransforms[fi];
                token.ThrowIfCancellationRequested();
                var lightRaw = await lightInfo.LoadFullAsync(token);
                // integrationCalibrator, NOT calibrator: under --remove-stars these frames are the
                // starless plates, already calibrated before star removal. Calibrating again would
                // subtract bias and dark twice and divide by the flat twice, silently.
                var calibrated = layerCalibrator.Apply(lightRaw);
                // Subtracting the body beats excluding it and takes precedence. The mask throws away
                // every frame the comet is anywhere near, which is affordable only when the body moves
                // much further than its coma is wide -- 10P moves 45 px in 3.5 h and masking it is
                // arithmetically impossible. See CometModel.
                // The body's light is centred on the frame's MID-exposure, so that is the instant its
                // position is evaluated at; the compose itself is indifferent (see CometCompose).
                var midExposure = lightInfo.Meta.ExposureStartTime + lightInfo.Meta.ExposureDuration / 2;
                if (layerModel is { } model && cometRate is { } modelRate && cometFit is not null)
                {
                    // source -> the COMET-ALIGNED reference grid, the one basis where the body does
                    // not move, so the model needs no per-frame position of its own.
                    var toCometGrid = CometCompose.ToCometGrid(
                        transformOrig, modelRate, CometCompose.DriftHours(lightInfo.Meta, reference.Meta));
                    var cfa = calibrated.ImageMeta.SensorType.GetBayerPatternMatrix(
                        calibrated.ImageMeta.BayerOffsetX, calibrated.ImageMeta.BayerOffsetY);
                    // One amplitude PER CHANNEL: the comet layer normalised each channel to its own
                    // sky, so the model's channels are in different units and a pooled amplitude
                    // paints a colour cast along the track (SWAN: red -0.84 sigma, blue +0.36).
                    if (model.FitScales(calibrated, toCometGrid, bodyOnGrid, cfa, amplitudes))
                    {
                        // The nucleus takes its own amplitude: it is as sharp as this frame's seeing
                        // and the coma's amplitude does not know that. A no-op without a spliced core.
                        model.FitCoreScales(calibrated, toCometGrid, bodyOnGrid, cfa, amplitudes, coreAmplitudes);
                        maskedFramePixels += model.SubtractFrom(calibrated, toCometGrid, bodyOnGrid, cfa, amplitudes, coreAmplitudes);
                        modelScaleSum ??= new double[model.ChannelCount];
                        for (var c = 0; c < amplitudes.Length; c++)
                        {
                            modelScaleSum[c] += amplitudes[c];
                        }
                        modelScaleCount++;
                    }
                }
                else if (layerMask is { } cometMask)
                {
                    // source -> REFERENCE, never source -> canvas. The canvas shift differs per layer
                    // (each layer's union bounding box comes from its own transforms), and the mask
                    // must not depend on which layer is being built.
                    var srcToRef = transformOrig;
                    if (cometMask.SourcePositionAt(srcToRef, midExposure) is { } centre)
                    {
                        maskedFramePixels += CometMask.Punch(calibrated, centre, cometMask.SourceRadius(srcToRef));
                    }
                }
                // Skipped under --remove-stars: integrationCalibrator is a no-op there and these
                // frames are the starless plates, which are already on disk.
                if (!options.RemoveStarsPerFrame)
                {
                    intermediates?.SaveCalibrated(calibrated, lightInfo.Path);
                }
                return (calibrated, transformOrig);
            }

            // Producer that loads each matched frame and warps into the BB canvas, yielding one Image
            // at a time -- the ONE place this group's pixels are read after the measure pass. Loads
            // per frame, deliberately uncached: the register phase no longer reads pixels, so the load
            // that used to fill the pipeline-level cache is gone and per-frame totals are unchanged;
            // the staged strategies keep their own internal FrameCache for their multi-pass needs.
            async IAsyncEnumerable<Image> WarpedFramesProducer(
                [EnumeratorCancellation] CancellationToken token)
            {
                for (var fi = 0; fi < matched.Count; fi++)
                {
                    var (calibrated, transformOrig) = await PrepareFrameAsync(fi, token);
                    // The shared debayer + warp step (FrameRegistration.WarpToCanvasAsync) -- the same
                    // three lines the dataset registrar runs, so the two paths cannot drift here.
                    var (warped, _) = await FrameRegistration.WarpToCanvasAsync(
                        calibrated, transformOrig, canvasShift, options.StackDebayerAlg, outWidth, outHeight, token);
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
                for (var fi = 0; fi < matched.Count; fi++)
                {
                    var (calibrated, transformOrig) = await PrepareFrameAsync(fi, token);
                    yield return new RawBayerFrame(calibrated, transformOrig * canvasShift);
                }
            }

            // Snapshot host + pick strategy. Snapshot factory probes free
            // RAM + disk; the selector wants those for its budget gate.
            // SensorType is pulled from the group key (the canonical scan-time
            // value), not from the reference frame's meta -- they agree by
            // construction since grouping keys on SensorType, but the group
            // key is the source of truth for the whole group's invariants.
            // Drizzle strategies key CanRun off this in their Evaluate.
            var probe = IntegrationProbe.Snapshot(
                frameCount: matched.Count,
                frameWidth: referenceRaw.Width,
                frameHeight: referenceRaw.Height,
                channelCount: 3,
                canvasWidth: outWidth,
                canvasHeight: outHeight,
                stagingDir: stagingDir,
                sensorType: key.CalibrationKey.SensorType,
                stagingDiskKind: DiskKind.Ssd);
            // Build the strategy pool. Two reasons to deviate from the default:
            //   1) --no-bayer-drizzle: filter both drizzle variants out so
            //      auto-pick falls back to the standard path.
            //   2) --drizzle-min-frames N (N != 60): replace the default
            //      drizzle instances with ones constructed against the
            //      user-overridden minimum, so the auto-pick gate matches
            //      what the user asked for. Without this, --drizzle-min-frames
            //      would only affect the pre-strategy gate (which fires
            //      ONLY on --strategy=BayerDrizzle/TilePipelinedDrizzle),
            //      leaving the auto-pick path still using the hardcoded 60.
            // ForcedStrategy still wins either way (the override bypasses
            // CanRun and the pool entirely), so a user who passes both
            // --no-bayer-drizzle and --strategy=BayerDrizzle gets drizzle.
            IEnumerable<IIntegrationStrategy>? pool = null;
            if (options.DisableBayerDrizzle)
            {
                pool = IntegrationStrategySelector.DefaultStrategies()
                    .Where(s => s.Kind is not IntegrationStrategyKind.BayerDrizzle
                            and not IntegrationStrategyKind.TilePipelinedDrizzle)
                    .ToArray();
            }
            else if (drizzleMinFrames != DrizzleStrategy.AutoSelectMinFrameCount)
            {
                pool = IntegrationStrategySelector.DefaultStrategies()
                    .Select(s => s.Kind switch
                    {
                        IntegrationStrategyKind.BayerDrizzle => (IIntegrationStrategy)new DrizzleStrategy(minFrameCount: drizzleMinFrames),
                        IntegrationStrategyKind.TilePipelinedDrizzle => new TilePipelinedDrizzleStrategy(minFrameCount: drizzleMinFrames),
                        _ => s,
                    })
                    .ToArray();
            }
            // A masked layer needs a strategy that (a) consumes the producers above and (b) normalises
            // per frame. Four kinds fail one or the other, for two quite different reasons, and both
            // failures are silent:
            //
            //   TilePipelined, TilePipelinedDrizzle -- bypass the producers entirely, re-loading,
            //   calibrating and warping each raw light themselves per tile from RawLightSources. The
            //   mask is simply never applied and the comet integrates straight back into the layer
            //   built to exclude it.
            //
            //   BayerDrizzle, TilePipelinedDrizzle -- no per-frame normalisation (neither touches
            //   Normalizer or Integrator). Ordinarily that costs nothing, because every interior pixel
            //   averages the same frames, so a session-long sky trend is one constant across the whole
            //   master. A MASK breaks that premise: it removes a different, time-contiguous slice of
            //   frames at each pixel along the track, which turns the temporal trend into spatial
            //   structure exactly where the layer is supposed to be cleanest.
            //
            // Measured on C/2025 R2, whose sky rose 504 ADU (1.6%) monotonically as the field set. The
            // masked drizzle layer removed a clean coma profile across the track (2.89 sigma at the
            // centreline, zero by 70 px) while along the track the removal ran +4.50 sigma at the
            // late-session end and -1.08 sigma at the early-session end -- negative meaning the mask
            // made the layer BRIGHTER there. A comet residual cannot do that; it sweeps every track
            // position equally and must be flat.
            var preferredStrategy = options.ForcedStrategy;
            if (layerMask is not null && layerModel is null)
            {
                pool = (pool ?? IntegrationStrategySelector.DefaultStrategies())
                    .Where(s => s.Kind is not IntegrationStrategyKind.TilePipelined
                            and not IntegrationStrategyKind.TilePipelinedDrizzle
                            and not IntegrationStrategyKind.BayerDrizzle)
                    .ToArray();
                if (preferredStrategy is IntegrationStrategyKind.TilePipelined
                    or IntegrationStrategyKind.TilePipelinedDrizzle
                    or IntegrationStrategyKind.BayerDrizzle)
                {
                    logger.LogWarning(
                        "  [comet] --strategy {Kind} cannot build a masked layer correctly "
                            + "(it either bypasses the mask or does not normalise per frame); "
                            + "letting the selector pick for this layer",
                        preferredStrategy);
                    preferredStrategy = null;
                }
            }
            var selection = IntegrationStrategySelector.Pick(probe, preferred: preferredStrategy, pool: pool);
            logger.LogInformation("  [strategy] picked {Kind} -- {Notes}", selection.Chosen.Kind, selection.Notes);
            logger.LogInformation("  [sink] {Sink} (canvas {GB:F2} GB)", selection.Sink, probe.CanvasBytes / 1e9);
            var sinkFactory = SinkFactories.Create(selection.Sink, stagingDir);

            var rejector = BuildRejector(matched.Count, options.RejectLowSigma, options.RejectHighSigma);
            // Log the thresholds, not just the kind: an A/B over the sigma pair is otherwise
            // indistinguishable in the log from an A/B over anything else in the run.
            logger.LogInformation("  rejector: {Rejector}{Sigmas}",
                rejector?.GetType().Name ?? "<none>",
                rejector switch
                {
                    SigmaClipRejector s => $" (low {s.LowSigma}, high {s.HighSigma})",
                    WinsorizedSigmaClipRejector w => $" (low {w.LowSigma}, high {w.HighSigma})",
                    LinearFitClipRejector l => $" (low {l.LowSigma}, high {l.HighSigma})",
                    _ => "",
                });

            var rawSources = new List<RawLightSource>(matched.Count);
            for (var fi = 0; fi < matched.Count; fi++)
            {
                rawSources.Add(new RawLightSource(
                    Path: (useStarless && matched[fi].Starless is { } sl ? sl : matched[fi].Light).Path,
                TransformToCanvas: layerTransforms[fi] * canvasShift));
            }

            // Forward strategy progress into the StackingProgress channel.
            var integrationProgress = progress is null
                ? null
                : new Progress<IntegrationProgress>(p => progress.Report(
                    new StackingProgress(StackingPhase.Integrating, slug, p.CompletedItems, p.TotalItems, p)));

            // Drizzle dispatch: BayerDrizzle (streaming, full-canvas accumulator)
            // and TilePipelinedDrizzle (strip-pipelined accumulator) both run
            // the drizzle algorithm and need DrizzleOptions + the bad-pixel
            // mask. They differ in producer plumbing: streaming uses
            // RawBayerFrames (one-shot, frame-at-a-time), tile-pipelined uses
            // RawLightSources (multi-pass per strip from cached calibrated
            // bayer). The bool `isDrizzle` gates BOTH; the producer pick
            // happens inside that branch.
            var isStreamingDrizzle = selection.Chosen.Kind == IntegrationStrategyKind.BayerDrizzle;
            var isTiledDrizzle = selection.Chosen.Kind == IntegrationStrategyKind.TilePipelinedDrizzle;
            var isDrizzle = isStreamingDrizzle || isTiledDrizzle;
            var job = new IntegrationJob(
                WarpedFrames: WarpedFramesProducer,
                ExpectedFrameCount: matched.Count,
                Options: new IntegrationOptions(Rejector: rejector),
                StagingDir: stagingDir,
                StatsRect: statsRect,
                FrameFootprints: frameFootprints,
                RawLightSources: rawSources,
                Calibrator: layerCalibrator,
                DebayerAlgorithm: options.StackDebayerAlg,
                CanvasWidth: outWidth,
                CanvasHeight: outHeight,
                Progress: integrationProgress,
                MasterSinkFactory: sinkFactory,
                Intermediates: intermediates,
                RawBayerFrames: isStreamingDrizzle ? RawBayerFramesProducer : null,
                DrizzleOptions: isDrizzle ? (options.DrizzleOptions ?? new DrizzleOptions()) : null,
                BadPixelMask: isDrizzle ? badPixelMask : null);

            // index i in Integrator's frame list is matched[i] here, so a normalized dump can be
            // named after its light rather than numbered.
            intermediates?.SetFrameOrder([.. matched.Select(m => m.Item1.Path)]);

            var integrateStart = StageTimings.Start();
            IntegrationResult intResult;
            try
            {
                intResult = await selection.Chosen.RunAsync(job, ct);
            }
            catch (NotImplementedException ex)
            {
                logger.LogWarning("  [strategy] {Kind} threw NotImplementedException: {Msg}", selection.Chosen.Kind, ex.Message);
                try { Directory.Delete(stagingDir, recursive: true); } catch { /* hygiene */ }
                layerSkipReason = $"strategy {selection.Chosen.Kind} not implemented";
                return null;
            }
            // Same canvas-pixel accounting as the registrar's integrate stage. Warp has no stage of its
            // own on this path, deliberately: the strategies stream the warp inside their own pass (the
            // producer yields warped frames), so its cost is inseparable from integration here, whereas
            // the registrar warps eagerly to scratch FITS and legitimately times it apart.
            timings.Record(StageNames.Integrate, integrateStart, matched.Count, (long)matched.Count * outWidth * outHeight);
            logger.LogInformation("  integrated in {ElapsedMs} ms (frames={Frames}, rejections={Rej}, rate={Rate:P2})",
                (long)Stopwatch.GetElapsedTime(integrateStart).TotalMilliseconds, intResult.FrameCount, intResult.TotalRejections, intResult.MeanRejectionRate);
            hostTracker.Log(logger, $"integrate/{slug}");

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
            // Both drizzle variants emit byte-equivalent output (same kernel,
            // same final divide), so they share the _drizzle infix. Other
            // strategies share the canonical master_<slug>.fits name -- their
            // differences in memory layout / staging / rejection kernel are
            // invisible in the output FITS bytes.
            var strategySuffix = selection.Chosen.Kind is IntegrationStrategyKind.BayerDrizzle
                or IntegrationStrategyKind.TilePipelinedDrizzle
                ? "_drizzle"
                : "";
            var masterPath = Path.Combine(outputDir, $"master_{slug}{layerSuffix}{strategySuffix}.fits");
            var refImageDim = referenceRaw.GetImageDim();
            var postProcessor = new MasterPostProcessor(logger, catalogDb, options.Enhance ? sharpenPipeline : null, enhanceProgress);
            var postStart = StageTimings.Start();
            var postResult = await postProcessor.WriteMasterAsync(
                intResult, masterPath, searchHint, refImageDim, referenceRaw.ImageMeta, statsRect, selection.Chosen.Kind,
                enhance: options.Enhance, enhanceBlend: options.EnhanceBlend, splitPlates: options.SplitPlates,
                enhanceOptions: options.EnhanceOptions ?? Enhancement.EnhanceOptions.Default,
                outputs: options.RenderOutputs, previewBoost: options.PreviewBoost,
                ultraHdrPeakNits: options.UltraHdrPeakNits,
                inheritedWhiteBalance: options.InheritedWhiteBalance,
                // Stamped on every master, sidereal included: absence would otherwise mean either
                // "star-aligned" or "written before the card existed".
                alignment: layerAlignment,
                ct: ct);
            timings.Record(StageNames.Post, postStart, 1, (long)outWidth * outHeight);
            if (intResult.TotalRejections > 0)
            {
                logger.LogInformation("  wrote {Path}", IntegrationFitsWriter.RejectionPathFor(masterPath));
            }

            if (modelScaleCount > 0 && modelScaleSum is { } sums)
            {
                logger.LogInformation(
                    "  [comet] model subtracted from {N}/{Total} frames, mean amplitude per channel {Scales}",
                    modelScaleCount, matched.Count,
                    string.Join("/", Array.ConvertAll(sums, s => (s / modelScaleCount).ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            }
            return (masterPath, postResult, maskedFramePixels, intResult, canvasShift, statsRect, selection.Chosen.Kind);
        }

        // The finished image of a comet run: the star layer plus the body, added back once. Written
        // through the same post-processor as every other master, so it plate-solves (it has stars),
        // gets its own SPCC (it has stars AND the comet, which neither layer alone offers) and renders
        // the same way. Units: the model is in the COMET layer's pixel units and the star layer is in
        // its own; both are normalised so each channel's sky median sits at the integrator's target,
        // so the ratio of the two skies is the gain between them -- 1.0 when both layers took the same
        // strategy, and the right number when one drizzled (no normalisation, sky at 0.0145) and the
        // other did not. Measured rather than assumed, because the day the two strategies differ is
        // the day an assumed 1.0 adds a comet 34x too faint and nobody notices.
        async Task<string?> WriteCompositeAsync(
            IntegrationResult starLayer, Image cometMaster, CometModel bodyModel, Vector2 bodyOnStarCanvas,
            Rectangle layerStatsRect, IntegrationStrategyKind layerStrategy, AlignmentProvenance cometAlignment,
            CancellationToken token)
        {
            var compositeStart = StageTimings.Start();
            var starMaster = starLayer.Master;
            if (starMaster.ChannelCount != bodyModel.ChannelCount)
            {
                logger.LogWarning(
                    "  [comet] composite skipped: the star layer has {Star} channels and the body model {Model}",
                    starMaster.ChannelCount, bodyModel.ChannelCount);
                return null;
            }
            var gains = new float[starMaster.ChannelCount];
            for (var c = 0; c < gains.Length; c++)
            {
                var starSky = SkyMedian(starMaster, c);
                var cometSky = SkyMedian(cometMaster, c);
                gains[c] = starSky > 0f && cometSky > 0f ? starSky / cometSky : 1f;
            }
            var planes = new float[starMaster.ChannelCount][,];
            for (var c = 0; c < planes.Length; c++)
            {
                planes[c] = (float[,])starMaster.GetChannelArray(c).Clone();
            }
            var placed = bodyModel.AddTo(planes, bodyOnStarCanvas, gains);
            if (placed == 0)
            {
                // The subtraction touched pixels in every frame, so the model is real; the only way to
                // place none of it is a position off the star canvas, i.e. the wrong basis.
                logger.LogWarning(
                    "  [comet] composite skipped: the body at ({X:F1}, {Y:F1}) lands on no pixel of the {W}x{H} star canvas",
                    bodyOnStarCanvas.X, bodyOnStarCanvas.Y, starMaster.Width, starMaster.Height);
                return null;
            }
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var plane in planes)
            {
                foreach (var v in plane)
                {
                    if (!float.IsFinite(v)) { continue; }
                    if (v < min) { min = v; }
                    if (v > max) { max = v; }
                }
            }
            var composite = new Image(planes, BitDepth.Float32, max, min, starMaster.Pedestal, starMaster.ImageMeta);
            var path = Path.Combine(outputDir, $"master_{slug}_composite.fits");
            var postProcessor = new MasterPostProcessor(logger, catalogDb, options.Enhance ? sharpenPipeline : null, enhanceProgress);
            await postProcessor.WriteMasterAsync(
                starLayer with { Master = composite }, path, searchHint, referenceRaw.GetImageDim(), referenceRaw.ImageMeta,
                layerStatsRect, layerStrategy,
                enhance: options.Enhance, enhanceBlend: options.EnhanceBlend, splitPlates: options.SplitPlates,
                enhanceOptions: options.EnhanceOptions ?? Enhancement.EnhanceOptions.Default,
                outputs: options.RenderOutputs, previewBoost: options.PreviewBoost,
                ultraHdrPeakNits: options.UltraHdrPeakNits,
                inheritedWhiteBalance: options.InheritedWhiteBalance,
                // Says what this is: registered on the stars, with the body composited in. The drift
                // and its source travel with it so the placement is reproducible from the header.
                alignment: new AlignmentProvenance("Composite", cometAlignment.TargetBody, cometAlignment.DriftPxPerHour, cometAlignment.RateSource),
                ct: token);
            timings.Record(StageNames.Post, compositeStart, 1, (long)starMaster.Width * starMaster.Height);
            logger.LogInformation(
                "  [comet] composite wrote {Path}: body placed at ({X:F2}, {Y:F2}) on the star canvas over {Px:N0} px, "
                    + "reach {Reach:F0} px, comet->star gain {Gains}, in {ElapsedMs} ms",
                path, bodyOnStarCanvas.X, bodyOnStarCanvas.Y, placed, bodyModel.ReachPx,
                string.Join("/", Array.ConvertAll(gains, g => g.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))),
                (long)Stopwatch.GetElapsedTime(compositeStart).TotalMilliseconds);
            return path;
        }

        string? compositePath = null;

        // The primary layer: comet-aligned when a rate is in play, an ordinary star stack otherwise.
        // Never masked -- on a comet-aligned canvas the body is the one thing that must survive.
        var primaryAlignment = cometRate is { } appliedDrift
            ? new AlignmentProvenance(
                "Comet",
                // A bare --comet reads the body from the frames' own OBJECT card, so fall back to it
                // rather than leaving TRACKOBJ absent on exactly the runs that used it.
                string.IsNullOrWhiteSpace(options.CometDesignation)
                    ? (string.IsNullOrWhiteSpace(referenceRaw.ImageMeta.ObjectName) ? null : referenceRaw.ImageMeta.ObjectName)
                    : options.CometDesignation,
                appliedDrift,
                options.CometRatePxPerHour is not null ? "Manual" : "Horizons")
            : AlignmentProvenance.Sidereal;

        if (await IntegrateLayerAsync(transforms, layerMask: null, layerModel: null,
                useStarless: true, layerSuffix: "", primaryAlignment, ct) is not { } primary)
        {
            return new GroupResult(slug, lightList.Count, matched.Count, Result: null, MasterFitsPath: null,
                PreviewPngPath: null, Elapsed: groupSw.Elapsed,
                SkipReason: layerSkipReason ?? "integration produced no master",
                Stages: timings.Snapshot());
        }
        var masterPath = primary.MasterPath;
        var postResult = primary.Post;

        // The companion STAR layer. Same frames, same reference, same canvas maths -- the comet
        // excluded per frame instead of registered on. See CometMask for why exclusion rather than
        // rejection: kappa-sigma removes the compact nucleus trail and structurally cannot remove the
        // diffuse coma, because at a pixel the body crosses it is present in a large fraction of the
        // frames rather than a small one.
        //
        // A failure here must never lose the comet master that was just written, so every refusal
        // below logs and carries on rather than returning.
        if (options.CometStarLayer && cometRate is not null)
        {
            var starLayerStart = StageTimings.Start();
            var pixelScale = referenceRaw.GetImageDim()?.PixelScale ?? 0.0;
            if (cometFit is not { } fit)
            {
                // --comet-rate states a difference and nothing else, so there is no anchor and no way
                // to know WHERE to exclude. Detecting the body in the pixels is the obvious
                // alternative and is exactly the step that has failed repeatedly on this data (a
                // star, a negative matched-filter peak, a green-ish star), so it is not attempted.
                logger.LogWarning(
                    "  [comet] star layer needs the body's position, which a manual --comet-rate does not carry; "
                        + "pass --comet <designation> to derive it from the ephemeris");
            }
            else if (!(pixelScale > 0.0))
            {
                logger.LogWarning(
                    "  [comet] star layer skipped: the reference frame states no pixel scale, so a {Arcsec:F0}\" "
                        + "mask cannot be sized in pixels", options.CometMaskArcsec);
            }
            else
            {
                // Where the body actually sits on the comet-aligned grid, which is NOT the bare
                // anchor. The compose is translate(-rate * (t_i - t_REF)) while the anchor describes
                // t_ANCHOR, the first ephemeris sample, so the body settles at
                // anchor + rate * (t_ref - t_anchor), evaluated at the reference's MID-exposure where
                // its light is centred. Those two epochs differ by up to the length of the session;
                // at 245 px/hr that is several hundred pixels, which is enough to crop the model out
                // of empty sky. That is exactly what happened on the first run, and it reported "the
                // star-removed difference holds no comet" rather than anything about position -- the
                // failure names the symptom, so the position is logged here.
                var cometOnGrid = CometCompose.BodyOnGrid(fit, reference.Meta);
                logger.LogInformation(
                    "  [comet] body sits at ({Gx:F2}, {Gy:F2}) on the comet-aligned grid "
                        + "(anchor ({Ax:F1}, {Ay:F1}) at {Epoch:u} carried {Hours:F3} h to the reference mid-exposure)",
                    cometOnGrid.X, cometOnGrid.Y, fit.AnchorPx.X, fit.AnchorPx.Y, fit.AnchorEpoch.UtcDateTime,
                    (reference.Meta.ExposureStartTime + reference.Meta.ExposureDuration / 2 - fit.AnchorEpoch).TotalHours);

                var radiusPx = (float)(options.CometMaskArcsec / pixelScale);
                var mask = new CometMask(
                    // Used exactly as SkyToPixel returned it. The repo rule about never subtracting a
                    // pixel applies here and matters more than usual: unlike the rate, an absolute
                    // position has no cancellation to protect it from a convention mistake.
                    fit.AnchorPx,
                    fit.PxPerHour,
                    fit.AnchorEpoch,
                    radiusPx);
                logger.LogInformation(
                    "  [comet] star layer: excluding r={Radius:F0} px ({Arcsec:F0}\" at {Scale:F3}\"/px) around the body, "
                        + "anchor ({Ax:F1}, {Ay:F1}) at {Epoch:u}",
                    radiusPx, options.CometMaskArcsec, pixelScale,
                    mask.AnchorRefPx.X, mask.AnchorRefPx.Y, fit.AnchorEpoch.UtcDateTime);

                // Prefer SUBTRACTING the body to excluding it. The model comes from the comet
                // layer just written: a star remover run on a comet-aligned plate removes the comet
                // (there the coma is the only compact source, every star being a streak), so the
                // difference is the comet alone. Measured on SWAN, that recovers 100% of it out to
                // 120 px and leaks no star trails at all. Masking stays as the fallback for a host
                // with no AI backend registered -- it is strictly worse, and on a slow body like 10P
                // it cannot work at all.
                CometModel? model = null;
                if (starRemover is null)
                {
                    logger.LogInformation(
                        "  [comet] no IStarRemover registered, so the star layer falls back to EXCLUDING the body. "
                            + "Register one (AddRcAstroAi() or AddTianWenAi()) to subtract it instead, which keeps "
                            + "every frame and removes the tail and the coma wings a disc cannot reach");
                }
                else
                {
                    var modelStart = StageTimings.Start();
                    model = await CometModel.TryBuildAsync(
                        primary.Integration.Master,
                        // Already starless when --remove-stars built the comet layer from per-frame
                        // star-removed plates: the master then holds the comet and nothing else, so
                        // there is no difference to take and no trail residue to fight.
                        options.RemoveStarsPerFrame,
                        Vector2.Transform(cometOnGrid, primary.CanvasShift),
                        // Stars streak along the drift vector on a comet-aligned plate, which is what
                        // lets the model's trail residue be removed by shape.
                        fit.PxPerHour,
                        starRemover, logger, ct);
                    if (model is null)
                    {
                        logger.LogWarning("  [comet] falling back to EXCLUDING the body; the model could not be built");
                    }
                    else
                    {
                        logger.LogInformation("  [comet] model ready in {ElapsedMs} ms",
                            (long)Stopwatch.GetElapsedTime(modelStart).TotalMilliseconds);
                        if (options.RemoveStarsPerFrame)
                        {
                            // The plates the model came from had their stars removed, and a star
                            // remover takes a comet's central condensation with them. The frames still
                            // have it, so without this the nucleus stays in every frame of the star
                            // layer as a line along the track (10P: +2 to +3.5 sigma). Restore it from a
                            // small comet-aligned median stack of the RAW frames, which the remover
                            // never saw. Costs one read + calibration per frame.
                            var coreStart = StageTimings.Start();
                            var rawFrames = new List<(FrameInfo Light, Matrix3x2 StarTransform)>(matched.Count);
                            foreach (var m in matched)
                            {
                                rawFrames.Add((m.Light, m.StarTransform));
                            }
                            var rawCore = await CometRawCore.StackAsync(
                                rawFrames, fit.PxPerHour, reference.Meta, cometOnGrid, model.ChannelCount,
                                CometRawCore.DefaultRadiusPx, calibrator, logger, ct);
                            if (rawCore is not null)
                            {
                                model.SpliceCore(rawCore, innerPx: 12f, featherPx: 6f, logger);
                            }
                            logger.LogInformation("  [comet] nucleus restored from the raw frames in {ElapsedMs} ms",
                                (long)Stopwatch.GetElapsedTime(coreStart).TotalMilliseconds);
                        }
                    }
                }

                var starTransforms = matched.ConvertAll(m => m.StarTransform);
                var starLayer = await IntegrateLayerAsync(
                    starTransforms, model is null ? mask : null, model,
                    useStarless: false, layerSuffix: "_stars", AlignmentProvenance.Sidereal, ct);
                if (starLayer is not { } star)
                {
                    logger.LogWarning("  [comet] star layer skipped: {Reason}",
                        layerSkipReason ?? "integration produced no master");
                }
                else if (star.MaskedPixels == 0)
                {
                    // Nothing was blanked in ANY frame, which no correct run produces: the body is on
                    // the sensor by construction, it is what the session was pointed at. This is the
                    // signature of an anchor in the wrong basis, and the resulting master would carry
                    // an untouched comet while looking entirely plausible. Say so.
                    logger.LogWarning(
                        "  [comet] star layer wrote {Path} but {What} touched NO pixels in any frame -- "
                            + "the anchor is very likely in the wrong basis; treat that master as untouched",
                        star.MasterPath, model is null ? "the mask" : "the model subtraction");
                }
                else
                {
                    // Blanked pixels summed over every frame, with the per-frame average beside it:
                    // the average is the one a reader can check against pi*r^2 at a glance, and a
                    // total that silently reads as a per-frame count invites exactly that mistake.
                    logger.LogInformation(
                        "  [comet] star layer wrote {Path} by {What} ({Px:N0} px over {Frames} frames, "
                            + "{PerFrame:N0}/frame) in {ElapsedMs} ms",
                        star.MasterPath, model is null ? "EXCLUDING the body" : "SUBTRACTING the body",
                        star.MaskedPixels, matched.Count,
                        star.MaskedPixels / Math.Max(matched.Count, 1),
                        (long)Stopwatch.GetElapsedTime(starLayerStart).TotalMilliseconds);

                    // The finished image: the star layer with the body added back ONCE, where the
                    // ephemeris puts it at the reference epoch. The same model that came out of every
                    // frame goes in here, so the composite is the star layer plus exactly the comet,
                    // placed by the same reference-space point the subtraction used -- no WCS, no
                    // centroid, and no chance of the two disagreeing. The hand-built version of this
                    // placed the body from a typed RA/Dec through the star layer's WCS and a
                    // core-weighted centroid that locked onto a star 40 px away and missed by 46 px.
                    if (options.CometComposite && model is { } bodyModel)
                    {
                        compositePath = await WriteCompositeAsync(
                            star.Integration, primary.Integration.Master, bodyModel,
                            Vector2.Transform(cometOnGrid, star.CanvasShift),
                            star.StatsRect, star.Strategy, primaryAlignment, ct);
                    }
                }
            }
        }

        // The manifest is written AFTER the master, so it can only ever describe a run that produced
        // one. A manifest beside a missing or failed master would be consumed happily by the next
        // layer and pin it to inputs nothing ever integrated.
        try
        {
            var writtenManifestPath = StackManifest.PathFor(masterPath);
            var digestStart = StageTimings.Start();
            // Digest in parallel: this is a second full read of every frame (~3 GB for 135 subs) and
            // it is pure I/O plus SHA-256, so it scales with cores rather than adding a serial tail.
            var digests = new string[manifestFates.Count];
            Parallel.For(0, manifestFates.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                digests[i] = StackManifest.DigestData(manifestFates[i].Frame.Path);
            });
            var manifestEntries = new ManifestFrame[manifestFates.Count];
            for (var i = 0; i < manifestFates.Count; i++)
            {
                var (frame, fate, star) = manifestFates[i];
                manifestEntries[i] = new ManifestFrame(
                    frame.Path,
                    digests[i],
                    fate,
                    frame.Meta.ExposureStartTime,
                    star is { } m ? ManifestFrame.From(m) : null);
            }
            var referenceDigest = "";
            for (var i = 0; i < manifestFates.Count; i++)
            {
                if (string.Equals(manifestFates[i].Frame.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
                {
                    referenceDigest = digests[i];
                    break;
                }
            }
            var writtenManifest = new StackManifest(
                StackManifest.CurrentSchemaVersion,
                slug,
                IntegrationFitsWriter.SoftwareCreator,
                DateTimeOffset.UtcNow,
                reference.Path,
                referenceDigest,
                options.SnrMin,
                options.MinStars,
                manifestEntries);
            await writtenManifest.WriteAsync(writtenManifestPath, ct);
            logger.LogInformation(
                "  wrote {Path} ({Matched} matched / {Total} frames, reference {Ref}, digests in {ElapsedMs} ms)",
                writtenManifestPath, matched.Count, manifestEntries.Length,
                Path.GetFileNameWithoutExtension(reference.Path),
                (long)Stopwatch.GetElapsedTime(digestStart).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A manifest is an enabler for a LATER run, so failing to write one must not lose the
            // master this run just produced.
            logger.LogWarning(ex, "  [manifest] could not be written; this master is still valid");
        }
        var previewPath = Path.ChangeExtension(masterPath, ".png");
        return new GroupResult(
            slug,
            FramesAttempted: lightList.Count,
            FramesMatched: matched.Count,
            Result: postResult.Result,
            MasterFitsPath: masterPath,
            PreviewPngPath: previewPath,
            Elapsed: groupSw.Elapsed,
            Spcc: postResult.Spcc,
            Stages: timings.Snapshot(),
            CompositeFitsPath: compositePath);
    }

    /// <summary>FITS card on a per-frame starless plate naming the <see cref="StarRemovalMode"/> it was
    /// made in, so a later run reuses the plate only for the same mode.</summary>
    private const string StarRemovalModeKeyword = "STARMODE";

    /// <summary>Median of one channel's finite pixels over a coarse subsample: the sky level of a
    /// master, for relating two layers' units.</summary>
    private static float SkyMedian(Image image, int channel)
    {
        var step = Math.Max(1, Math.Min(image.Width, image.Height) / 512);
        var values = new List<float>((image.Width / step + 1) * (image.Height / step + 1));
        for (var y = 0; y < image.Height; y += step)
        {
            for (var x = 0; x < image.Width; x += step)
            {
                var v = image[channel, y, x];
                if (float.IsFinite(v))
                {
                    values.Add(v);
                }
            }
        }
        if (values.Count == 0)
        {
            return 0f;
        }
        values.Sort();
        return values[values.Count / 2];
    }

    // =====================================================================
    // Helpers (moved from StackingEndToEndManualTest verbatim minus log
    // chatter; behaviour-identical)
    // =====================================================================

    /// <param name="frames">Calibration frames of one type; grouped internally by
    /// <see cref="MasterGroupKey"/>.</param>
    /// <param name="builder">Combiner for this frame type.</param>
    /// <param name="mastersDir">Where masters are cached.</param>
    /// <param name="pathSuffix">Appended to the cached master's filename. Used to give a master
    /// built a NEW way a new name, so a file cached by an older version is simply not found and is
    /// rebuilt. This cache trusts any file it finds (no fingerprint), so the name is the only place
    /// a change in how the master is built can be recorded.</param>
    /// <param name="ct">Cancellation.</param>
    /// <summary>
    /// Derives the comet's canvas rate for this group: plate-solve the reference frame, ask JPL
    /// Horizons for a TOPOCENTRIC track over the group's own time span, fit a straight line through
    /// the projected pixel positions.
    ///
    /// <para>Every failure answers <c>null</c> rather than throwing. A comet stack that silently
    /// becomes a star stack would be a bad outcome, which is why the caller says so loudly -- but a
    /// stack that dies because JPL was unreachable would be a worse one, and the explicit
    /// <c>CometRatePxPerHour</c> exists precisely so an offline run has an answer.</para>
    ///
    /// <para><b>The residual is logged and nothing gates on it.</b> It measures how STRAIGHT the
    /// track was, never whether it pointed the right way, and a geocentric (wrong) track fits
    /// straighter than the correct one because parallax is exactly what bends the correct one. It is
    /// here to catch a body too fast for one linear rate, not to validate the ephemeris.</para>
    /// </summary>
    /// <summary>
    /// Matched manifest frames keyed by exposure epoch, or <c>null</c> when any two share one.
    ///
    /// <para>This is the join for a DERIVED plate, whose pixels differ from its original by
    /// construction and can therefore never digest to it. <c>DATE-OBS</c> survives star removal
    /// (measured: 102 of 106 cards do) and the manifest already records the epoch, so this costs
    /// nothing to offer -- unlike stamping a provenance card into 135 files.</para>
    ///
    /// <para>It is refused outright on a duplicate rather than resolved by some tie-break, because
    /// the failure mode is handing one frame ANOTHER frame's transform: a plausible-looking master
    /// that is subtly misregistered, with nothing in any log to say so. One camera cannot start two
    /// exposures at the same instant, so a duplicate means the input set is not what it looks like
    /// (two OTAs merged, a frame duplicated) and that deserves to fail loudly.</para>
    /// </summary>
    private static Dictionary<DateTime, ManifestFrame>? BuildEpochIndex(StackManifest manifest, ILogger logger)
    {
        var byEpoch = new Dictionary<DateTime, ManifestFrame>(manifest.Frames.Length);
        foreach (var frame in manifest.Frames)
        {
            if (frame.Fate is not FrameFate.Matched)
            {
                continue;
            }
            var epoch = frame.ExposureStartUtc.UtcDateTime;
            if (!byEpoch.TryAdd(epoch, frame))
            {
                logger.LogWarning(
                    "  [manifest] two matched frames share exposure epoch {Epoch:O}; the epoch fallback is disabled for this run, so a derived plate must carry {Card}",
                    epoch, FrameProvenance.SourceDigestKeyword);
                return null;
            }
        }
        return byEpoch;
    }

    private async Task<CometRate?> TryResolveCometRateAsync(
        IReadOnlyList<FrameInfo> lightList,
        Image referenceRaw,
        WCS? searchHint,
        CancellationToken ct)
    {
        if (catalogDb is not { } db)
        {
            logger.LogWarning("  [comet] no catalog DB, so the reference frame cannot be plate-solved");
            return null;
        }

        // The window spans every frame in the group, not just the reference: the compose measures
        // each frame's drift from the reference epoch, so the fit has to be valid across the lot.
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        foreach (var lf in lightList)
        {
            var start = lf.Meta.ExposureStartTime;
            if (start < first) { first = start; }
            var end = start + lf.Meta.ExposureDuration;
            if (end > last) { last = end; }
        }

        var meta = referenceRaw.ImageMeta;
        if (CometTrackRequest.TryBuild(
                options.CometDesignation, meta.ObjectName,
                meta.Latitude, meta.Longitude, meta.SiteElevation, first, last) is not { } request)
        {
            logger.LogWarning(
                "  [comet] no ephemeris request from OBJECT=\"{Object}\" at SITELAT={Lat} SITELONG={Lon}: need a comet designation and a known site",
                meta.ObjectName, meta.Latitude, meta.Longitude);
            return null;
        }

        // Tycho-2 is what the solver matches against; the CLI kicks this off at startup so it has
        // usually landed by now and this returns through the idempotent fast path.
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
        var solveStart = StageTimings.Start();
        var solved = await new CatalogPlateSolver(db, logger).SolveImageAsync(
            referenceRaw, imageDim: referenceRaw.GetImageDim(), searchOrigin: searchHint, cancellationToken: ct);
        if (solved.Solution is not { } referenceWcs)
        {
            logger.LogWarning("  [comet] the reference frame did not plate-solve, so the ephemeris cannot be projected");
            return null;
        }
        logger.LogInformation("  [comet] reference solved in {ElapsedMs} ms",
            (long)Stopwatch.GetElapsedTime(solveStart).TotalMilliseconds);

        var track = await new HorizonsObserverSource(logger).TryFetchTrackAsync(
            request.Designation, request.SiteLatDeg, request.SiteLonDeg, request.SiteElevMetres,
            request.Start, request.Stop, request.Step, ct);
        if (track.Length < 2)
        {
            logger.LogWarning("  [comet] Horizons returned {Count} usable positions for {Designation}",
                track.Length, request.Designation);
            return null;
        }

        if (CometRateSolver.SolveCanvasRatePxPerHour(referenceWcs, track.AsSpan()) is not { } rate)
        {
            logger.LogWarning("  [comet] the track could not be fitted to a rate");
            return null;
        }

        logger.LogInformation(
            "  [comet] {Designation} from ({Lat:F4}, {Lon:F4}, {Elev:F0} m): {Count} samples over {Hours:F2} h -> "
                + "rate ({Vx:F3}, {Vy:F3}) px/hr, |v|={Mag:F2} px/hr, straightness residual {Residual:F3} px",
            request.Designation, request.SiteLatDeg, request.SiteLonDeg, request.SiteElevMetres,
            rate.SampleCount, (request.Stop - request.Start).TotalHours,
            rate.PxPerHour.X, rate.PxPerHour.Y, rate.PxPerHour.Length(), rate.MaxResidualPx);
        return rate;
    }

    private async Task<List<(MasterGroupKey Key, Image Master)>> BuildMastersAsync(
        List<FrameInfo>? frames,
        Func<IReadOnlyList<FrameInfo>, CancellationToken, Task<Image>> builder,
        string mastersDir,
        CancellationToken ct,
        string pathSuffix = "")
    {
        var masters = new List<(MasterGroupKey, Image)>();
        if (frames is null || frames.Count == 0) return masters;

        foreach (var group in frames.GroupBy(MasterGroupKey.FromFrame))
        {
            var key = group.Key;
            // One master per EPOCH (task #25): a config whose library was re-shot years later must
            // not blend both shoots into one master -- epoch merging attenuates recently-emerged
            // defects by frames-from-epoch/total and hides them from the hot-pixel detector, and the
            // blend was invisible (one representative DATE-OBS). The suffix is minted only when the
            // config actually split, so a single-epoch root keeps its legacy cache filename.
            var epochs = CalibrationEpochs.Split(group.ToList());
            foreach (var epoch in epochs)
            {
                var list = epoch.Frames;
                if (list.Count < 2) continue;
                var epochSuffix = epochs.Count > 1 ? CalibrationEpochs.EpochSlug(epoch.Start) : "";
                var masterPath = Path.Combine(mastersDir, $"master_{key.Slug()}{pathSuffix}{epochSuffix}.fits");

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
                // Shared provenance cards (SWCREATE + DATE-BEG/DATE-END): this write had the same
                // defect the dataset cache had -- the master inherited its subs' SWCREATE and
                // declared nothing about itself or its input span.
                master.WriteToFitsFile(masterPath, null, MasterFrameBuilder.ProvenanceHeaders(list));
                logger.LogInformation("  built {File} ({Count} input frames, {Start:yyyy-MM-dd}..{End:yyyy-MM-dd})",
                    Path.GetFileName(masterPath), list.Count, epoch.Start, epoch.End);
            }
        }
        return masters;
    }

    /// <summary>What a master is being matched FOR, because the three consumers weight different
    /// physics: a DARK must be a plausible light-dark (exposure band excludes mislabeled dark-flats,
    /// gain is a hard gate -- a wrong-gain dark mis-scales the fixed pattern it exists to remove); a
    /// FLAT must carry the same filter's dust/vignetting (exposure is irrelevant, gain a score); a
    /// flat PEDESTAL keeps the legacy exposure-proximity ranking (its exposure term is a proxy for
    /// the thermal signal the candidate removes; the caller pre-gates the candidate pool).</summary>
    internal enum MasterMatchKind { Dark, Flat, FlatPedestal, Bias }

    /// <summary>Find the best master for a light group. The gates and penalties are the dataset
    /// resolver's own (<see cref="CalibrationResolver"/>), so the two paths' matching arithmetic
    /// cannot drift: until 2026-08-17 this method never consulted gain, offset, filter or capture
    /// date at all -- a g252 dark silently calibrated g121 lights whenever it won on
    /// temperature/exposure, and a mislabeled 6.7s dark-flat could calibrate 60s lights when it was
    /// the only candidate. Ties break by ordinal slug, never by scan enumeration order.</summary>
    internal static (Image? Master, MasterGroupKey? Key) MatchMaster(
        List<(MasterGroupKey Key, Image Master)> masters, MasterGroupKey lightKey,
        MasterMatchKind kind, DateTimeOffset targetDate, bool requireGainMatch = true)
    {
        Image? bestMaster = null;
        MasterGroupKey? bestKey = null;
        var bestScore = double.PositiveInfinity;
        foreach (var (key, master) in masters)
        {
            if (!CalibrationResolver.DimensionCompatible(key, lightKey))
            {
                continue;
            }
            if (kind is MasterMatchKind.Dark
                && (!CalibrationResolver.ExposureCompatible(key.Exposure, lightKey.Exposure)
                    || !CalibrationResolver.GainCompatible(key, lightKey, requireGainMatch)))
            {
                continue;
            }
            // A bias is the ZERO-exposure frame, so there is no exposure to be compatible with --
            // but it is a readout offset, and read offset is a property of gain and offset, so
            // those gates still apply.
            if (kind is MasterMatchKind.Bias
                && !CalibrationResolver.GainCompatible(key, lightKey, requireGainMatch))
            {
                continue;
            }
            var score = CalibrationResolver.TempPenalty(key, lightKey) * 10.0
                + CalibrationResolver.GainPenalty(key, lightKey)
                + CalibrationResolver.TimePenalty(master.ImageMeta.ExposureStartTime, targetDate)
                + kind switch
                {
                    // A flat's exposure says nothing about its dust; its filter says everything.
                    MasterMatchKind.Flat =>
                        key.SameFilterAs(lightKey) ? 0.0 : 1000.0,
                    // Charging a bias for its exposure difference would price in the one thing a
                    // bias is defined by. Offset still counts -- it is what a bias measures.
                    MasterMatchKind.Bias => CalibrationResolver.OffsetPenalty(key, lightKey),
                    _ => Math.Abs((key.Exposure - lightKey.Exposure).TotalSeconds)
                        + (kind is MasterMatchKind.Dark ? CalibrationResolver.OffsetPenalty(key, lightKey) : 0.0),
                };
            if (score < bestScore
                || (score == bestScore && bestKey is not null && string.CompareOrdinal(key.Slug(), bestKey.Slug()) < 0))
            {
                bestScore = score;
                bestMaster = master;
                bestKey = key;
            }
        }
        return (bestMaster, bestKey);
    }

}

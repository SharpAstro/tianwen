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
using SharpAstro.Color.Icc;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// One-off end-to-end runner against a real prepared dataset. Skipped when
/// <c>C:\temp\stack</c> doesn't exist so CI keeps green. Intended as a
/// preview of the Phase 13 CLI orchestrator shape: load -> group -> build
/// masters -> pick reference -> register -> warp -> integrate -> write.
/// </summary>
public class StackingEndToEndManualTest(ITestOutputHelper output)
{
    private const string DataRoot = @"C:\temp\stack";

    /// <summary>
    /// Picks a pixel rejector for the integration step based on frame count.
    /// LFC is the highest-quality option per iteration but ~8x slower than
    /// plain sigma clip at large N (PixelRejectorBenchmarks: 16.4us vs 2.0us
    /// at N=244), and most of LFC's advantage shows up on bright-tail
    /// rejection-halo control which matters more when ditching a handful of
    /// outliers from many otherwise-clean frames. As N grows, sigma clip with
    /// an asymmetric kappa (low=3, high=5) is good enough to keep star tails
    /// while running fast.
    /// <list type="bullet">
    /// <item>N &lt; 5: no rejection (need at least a few frames to estimate
    /// a sigma reliably; the integrator just averages).</item>
    /// <item>5 &lt;= N &lt; 30: LFC. Small stacks benefit most from LFC's
    /// per-channel rank regression because there are few frames to spare.</item>
    /// <item>30 &lt;= N &lt; 60: Winsorized sigma clip. Middle ground -- less
    /// aggressive on the bright tail than plain sigma, ~3x faster than LFC.</item>
    /// <item>N &gt;= 60: Asymmetric SigmaClip (3/5). Speed wins. At N >= 60
    /// the noise floor is √N lower so the bright tail is fewer-stretched
    /// frames anyway; high-kappa keeps stars.</item>
    /// </list>
    /// </summary>
    private static IPixelRejector? BuildRejector(int frameCount) => frameCount switch
    {
        < 5  => null,
        < 30 => new LinearFitClipRejector(LowSigma: 3f, HighSigma: 3f, MaxIterations: 5),
        < 60 => new WinsorizedSigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5),
        _    => new SigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5),
    };

    [Fact]
    public async Task Stack_FullPipeline()
    {
        if (!Directory.Exists(DataRoot))
        {
            Assert.Skip($"Test data folder {DataRoot} not present.");
        }

        var ct = TestContext.Current.CancellationToken;
        // Output lives at <data>/output and master cache at <data>/output/masters.
        // Masters are pure functions of their input cal frames + builder settings,
        // so they survive across runs to skip the ~50 s rebuild step. Light-side
        // outputs (master_*_light_*.fits, *.rejection.fits) are wiped each run.
        // The recursive scan filters out anything under outputDir so previous-run
        // outputs don't get re-ingested as lights (the bug we hit before).
        var outputDir = Path.Combine(DataRoot, "output");
        var mastersDir = Path.Combine(outputDir, "masters");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(mastersDir);
        foreach (var f in Directory.EnumerateFiles(outputDir, "*.fits")) File.Delete(f);
        // Stale _staging from a previous run that died mid-group can balloon
        // to 25+ GB per group and fill the disk on a re-run. Wipe at start.
        var stagingRoot = Path.Combine(outputDir, "_staging");
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch { /* best-effort cleanup; if it fails the per-group code will surface it */ }
        }

        // Mirror all log output to stack-run.log alongside the masters so the
        // run is reviewable without scrolling through xUnit's per-test output.
        // AutoFlush so the file is tail-able while the run is still going.
        var logPath = Path.Combine(outputDir, "stack-run.log");
        using var logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
        void Log(string msg)
        {
            output.WriteLine(msg);
            logFile.WriteLine(msg);
        }
        Log($"[start] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  data={DataRoot}  out={outputDir}");

        // -----------------------------------------------------------------
        // 1) Enumerate ALL FITS recursively + group by MasterGroupKey
        // -----------------------------------------------------------------
        var sw = Stopwatch.StartNew();
        var source = new FitsFolderFrameSource(DataRoot, recursive: true);
        var allFrames = new List<FrameInfo>();
        var outputDirNormalised = Path.GetFullPath(outputDir);
        await foreach (var frame in source.EnumerateAsync(ct))
        {
            // Skip anything under outputDir -- masters and previous-run outputs
            // would otherwise be ingested as fresh lights. Path.GetFullPath
            // normalises separators / case so the StartsWith check is reliable
            // on Windows.
            if (Path.GetFullPath(frame.Path).StartsWith(outputDirNormalised, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            allFrames.Add(frame);
        }
        Log($"[scan] {allFrames.Count} frames in {sw.ElapsedMilliseconds} ms");

        var byType = allFrames.GroupBy(f => f.FrameType).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (type, frames) in byType)
        {
            Log($"  {type}: {frames.Count} frames");
        }

        // -----------------------------------------------------------------
        // 2) Build calibration masters per (FrameType, ExposureMs, TempC, Filter, Sensor, Gain, Offset, Size) group
        // -----------------------------------------------------------------
        sw.Restart();
        var biasMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Bias), MasterFrameBuilder.BuildBiasMasterAsync, "bias", mastersDir, Log, ct);
        var darkMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Dark), MasterFrameBuilder.BuildDarkMasterAsync, "dark", mastersDir, Log, ct);
        var flatMasters = await BuildMastersAsync(byType.GetValueOrDefault(FrameType.Flat), MasterFrameBuilder.BuildFlatMasterAsync, "flat", mastersDir, Log, ct);
        Log($"[masters] {biasMasters.Count} bias, {darkMasters.Count} dark, {flatMasters.Count} flat ready in {sw.ElapsedMilliseconds} ms");

        // -----------------------------------------------------------------
        // 3) For each lights group, run the integration pipeline
        // -----------------------------------------------------------------
        if (!byType.TryGetValue(FrameType.Light, out var lights) || lights.Count == 0)
        {
            Assert.Skip("No light frames found.");
        }

        // Light grouping uses LightGroupKey = (calibration signature + OBJECT
        // header). NINA writes every target's lights into one LIGHT/ folder,
        // so a 288-frame session can mix two targets imaged in the same night.
        // Frames of different targets look at different sky and never register
        // against each other -- they must end up in separate groups so each
        // gets its own reference and warp pipeline. When OBJECT is empty for
        // every frame the second axis collapses and grouping degenerates to
        // the legacy MasterGroupKey behavior.
        var lightGroups = lights.GroupBy(LightGroupKey.FromFrame).ToList();
        Log($"[lights] {lights.Count} lights in {lightGroups.Count} group(s)");

        // Substring match on the group slug to run a subset of groups only
        // (fast iteration on a 21 GB-free disk where the 60s groups don't
        // fit). Empty string = process all groups.
        // SoL 60s (244 frames) staging budget no longer exceeds disk now that
        // TilePipelined (Phase 8.2) is runnable -- the selector picks it when
        // disk-staged strategies fail and InRam exceeds RAM. Leave the
        // exclude blank to process every group; set to `_light_60s` to skip
        // the long-running SoL group on iteration.
        const string GroupExclude = "_light_60s";
        if (GroupExclude.Length > 0)
        {
            var beforeCount = lightGroups.Count;
            lightGroups = lightGroups.Where(g => !g.Key.Slug().Contains(GroupExclude, StringComparison.OrdinalIgnoreCase)).ToList();
            Log($"[filter] {beforeCount} group(s) -> {lightGroups.Count} after excluding '{GroupExclude}'");
        }
        const string GroupFilter = "";

        // Force a specific integration strategy regardless of probe budget.
        // Set to e.g. IntegrationStrategyKind.TilePipelined to validate Phase
        // 8.2 against a real dataset. null = auto-pick (production path).
        IntegrationStrategyKind? forceStrategy = null;
        if (GroupFilter.Length > 0)
        {
            var beforeCount = lightGroups.Count;
            lightGroups = lightGroups.Where(g => g.Key.Slug().Contains(GroupFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            Log($"[filter] {beforeCount} group(s) -> {lightGroups.Count} after filter '{GroupFilter}'");
        }

        foreach (var lightGroup in lightGroups)
        {
            var key = lightGroup.Key;
            var calKey = key.CalibrationKey;
            var lightList = lightGroup.ToList();
            Log($"\n=== Light group: {key.Slug()} ({lightList.Count} frames) ===");

            // Find best-matching calibration masters for THIS light group.
            //
            // Calibration path: bias is intentionally NOT passed to the
            // Calibrator. The master dark was built from raw darks (no bias
            // pre-subtraction), so its bias signal is already baked in --
            // subtracting both bias AND dark would double-subtract the bias
            // pedestal. Matched-exposure stacking (60s lights with 60s darks)
            // works cleanly with light - dark - flat alone.
            //
            // The bias master IS computed (saved as master_bias_*.fits for
            // QA), and is the right input for a future dark-scaling phase
            // (bias-pre-subtracted darks scale linearly by exposure ratio).
            // Calibration masters are sky-independent -- look them up by the
            // calibration signature alone (drop the OBJECT axis).
            var (dark, darkKey) = MatchMaster(darkMasters, calKey);
            var (flat, flatKey) = MatchMaster(flatMasters, calKey);
            Log($"  dark master: {(darkKey is null ? "NONE" : darkKey.Slug())}");
            Log($"  flat master: {(flatKey is null ? "NONE" : flatKey.Slug())}");

            var calibrator = new Calibrator(Bias: null, Dark: dark, Flat: flat, Pedestal: 0f);

            // For OSC sensors, register + stack on DEBAYERED data, not raw
            // Bayer. Star detection on raw Bayer is hopeless: the alternating
            // R/G/B pixels look like high-frequency noise to the star kernel,
            // which is why the previous all-raw run got 43/288 matches.
            //
            // Path: load raw -> calibrator.Apply (raw, matches the raw masters)
            //   -> VNG debayer to 3-channel RGB -> register against debayered
            //   reference -> warp 3 channels to ref grid -> integrate RGB.
            //
            // Two passes, two debayer algorithms:
            // - CentroidDebayerAlg drives FindStars (reference picker, ref-side
            //   star detection, per-frame match). VNG is the sweet spot: faster
            //   than AHD, much cleaner stars than BilinearMono.
            // - StackDebayerAlg drives the warp+integrate pass. AHD's adaptive
            //   homogeneity-directed interpolation preserves edge sharpness
            //   on stars and recovers more colour fidelity than VNG, at the
            //   cost of ~2-3x debayer time. The cost is per-frame and shows
            //   up only in the stack pass (the centroid pass stays on VNG so
            //   star detection time is unchanged).
            // Why not BilinearMono for the centroid pass: BilinearMono's
            // 2x2-cell-average produces centroids offset by ~0.5 px relative
            // to VNG, and mixing it with VNG-debayered stack frames pulls the
            // transforms off-axis. Verified empirically: drops 2-frame Skull
            // group's SPCC match count from 149/11k to 11/11k and breaks plate
            // solve. Keep both passes coordinate-aligned.
            // For mono cameras the whole debayer step is a no-op
            // (DebayerAsync short-circuits on Monochrome).
            const DebayerAlgorithm CentroidDebayerAlg = DebayerAlgorithm.VNG;
            // AHD by default. ~22x slower per-frame than VNG on the debayer
            // step (see DebayerBenchmarks) but cleaner channel reconstruction
            // means SPCC needs ~2x less correction (no false blue boost to
            // compensate for under-reconstructed green) and the master comes
            // out visibly neutral instead of green-biased. The cost is
            // per-frame so it scales with N -- swap back to VNG when wall
            // clock matters more than colour fidelity.
            const DebayerAlgorithm StackDebayerAlg = DebayerAlgorithm.AHD;
            const float snrMin = 5f;
            // minStars=2000 forces the FindStarsAsync retry loop to do a 2nd
            // pass at a lower detection_level (~7*noise), bringing the typical
            // star count from ~1300 to ~6500 in dense fields. This is what
            // rescues the post-flip frames whose 100 raw quad-pair candidates
            // (at 1300 stars) collapsed to a 3-inlier self-fit -- with 6500
            // stars they grow to 250+ raw candidates and RANSAC finds 25 inliers.
            const int minStars = 2000;

            // Top-K brightest stars used for quad fingerprinting. Faint stars
            // come and go between frames with detection-threshold variation,
            // which scrambles their nearest-neighbour quads; bright stars are
            // reproducible across the whole group, so a top-K subset produces
            // (nearly) identical quad signatures between any two frames. 500
            // is plenty for RANSAC -- we only need ~5-20 true correspondences,
            // and bright-star kNN over a 3008x3008 frame still gives well-
            // distributed quads across the FOV. astrometry.net / ASTAP use
            // the same trick.
            const int QuadStars = 500;

            // 3a. Pick reference (highest star count). We bypass
            // Registrator.PickReferenceAsync because it operates on the raw
            // FrameInfo without debayer awareness.
            sw.Restart();
            var frameStarCounts = new List<(FrameInfo Frame, int StarCount)>(lightList.Count);
            FrameInfo? reference = null;
            var bestCount = -1;
            foreach (var lf in lightList)
            {
                ct.ThrowIfCancellationRequested();
                var raw = await lf.LoadFullAsync(ct);
                var calibrated = calibrator.Apply(raw);
                var debayered = await calibrated.DebayerAsync(CentroidDebayerAlg, cancellationToken: ct);
                var stars = await debayered.FindStarsAsync(channel: 0, snrMin: snrMin, minStars: minStars, cancellationToken: ct);
                frameStarCounts.Add((lf, stars.Count));
                if (stars.Count > bestCount)
                {
                    bestCount = stars.Count;
                    reference = lf;
                }
            }
            if (reference is null)
            {
                Log($"  [skip] no reference frame could be picked");
                continue;
            }
            LogStarCountDistribution(frameStarCounts, Log);
            Log($"  reference: {Path.GetFileName(reference.Path)} " +
                $"({frameStarCounts.First(s => s.Frame.Path == reference.Path).StarCount} stars, {sw.ElapsedMilliseconds} ms)");

            // Pre-load + calibrate + debayer reference once; detect ref stars
            // once and wrap in SortedStarList so the per-frame matcher reuses
            // the cached quad list (FindQuadsAsync memoizes).
            var referenceRaw = await reference.LoadFullAsync(ct);
            // Grab the FITS header WCS as a plate-solve search hint. N.I.N.A. /
            // SharpCap captures usually stamp approximate RA/DEC keywords; we
            // pass these to CatalogPlateSolver so it knows where to look in
            // the Tycho-2 grid. If the hint is missing the solve step skips.
            WCS? searchHint = null;
            if (!Image.TryReadFitsFile(reference.Path, out _, out searchHint))
            {
                Log($"  [warn] couldn't reread ref FITS for WCS hint: {reference.Path}");
            }
            else if (searchHint is null)
            {
                Log($"  [info] ref FITS has no WCS headers; plate-solve will skip");
            }
            else
            {
                Log($"  [info] WCS hint: RA={searchHint.Value.CenterRA:F4}h Dec={searchHint.Value.CenterDec:F4}°");
            }
            // Reference for centroid + canvas-dim seed: mono debayer for fast
            // FindStars; the per-frame stack-warp pass uses StackDebayerAlg
            // separately, so the reference's stacked pixels come out colour
            // alongside every other frame.
            var referenceDebayered = await calibrator.Apply(referenceRaw).DebayerAsync(CentroidDebayerAlg, cancellationToken: ct);
            var referenceStars = await referenceDebayered.FindStarsAsync(channel: 0, snrMin: snrMin, minStars: minStars, cancellationToken: ct);
            var referenceSorted = new SortedStarList(referenceStars);
            var referenceQuads = await referenceSorted.FindQuadsAsync(maxStars: QuadStars, ct);
            Log($"  reference: stars={referenceStars.Count}, quads={referenceQuads.Count} (top-{QuadStars} by flux)");

            // 3b. Per-light: calibrate raw -> debayer -> register against
            // pre-debayered reference -> warp 3-channel RGB to ref grid.
            //
            // Quad-tolerance retry: the default 0.008 is too tight for typical
            // dithered light frames (we saw 5/288 match rate against this
            // dataset with crisp stars + small affine drift). Try increasingly
            // loose tolerances and take the first one that converges so a
            // single dataset-wide setting doesn't force false negatives.
            // The retry is cheap because SortedStarList.FindQuadsAsync
            // memoizes — only the match pass repeats.
            // Per-group staging dir. Each warped frame becomes a flat float32
            // binary so the streaming integrator can read row-stripes without
            // holding every aligned Image in RAM. Cleaned up at end of group.
            var stagingDir = Path.Combine(outputDir, "_staging", key.Slug());
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            sw.Restart();
            var matchStats = new List<(string Path, float TranslationPx, float RotationDeg, float Scale, float QuadTol)>();
            var skipCount = 0;
            // Min stars for a stable quad-invariant fit. Matches the matcher's
            // internal minStars/4=6 quad-correspondence floor with headroom.
            const int MinStarsForMatch = 24;

            // Per-stage cumulative timings, accumulated across all non-reference
            // frames in this group. Printed as a summary at group end so we can
            // see where time goes: load / calibrate / debayer / FindStars /
            // FindQuads / matcher ladder / warp. Excludes the reference frame
            // (already detected outside the loop) and the integration step
            // (logged separately below).
            var perfLoad = TimeSpan.Zero;
            var perfCalibrate = TimeSpan.Zero;
            var perfDebayer = TimeSpan.Zero;
            var perfFindStars = TimeSpan.Zero;
            var perfBuildQuads = TimeSpan.Zero;
            var perfMatch = TimeSpan.Zero;
            var perfWarp = TimeSpan.Zero;
            var perfFrames = 0;
            var stageSw = new Stopwatch();

            // Two-pass to honour the union bounding box of all valid pixels.
            //
            // Pass A: load -> calibrate -> debayer -> find stars -> match. Drop
            //   the debayered image, keep only (FrameInfo, transform). RAM
            //   stays bounded (one debayered Image in flight).
            // After pass A: project each matched transform through the source-
            //   corner quadrilateral to get its footprint in ref space, take
            //   the union to size the output canvas. Frames that dither past
            //   the reference's edges keep their valid data; frames that don't
            //   reach the reference's edges don't artificially shrink it
            //   either (the ref's own footprint is always part of the union).
            // Pass B: re-load each matched frame, apply a shift-corrected
            //   transform that puts pixel (0, 0) at the BB origin, warp into
            //   the BB-sized canvas, compute stats, stage to disk, drop.
            //
            // Re-load+cal+debayer in pass B costs ~225 ms per frame extra
            // (~55 s for a 244-frame group), well worth getting the canvas
            // size right. The proper fix is deferred warp during integration
            // (Phase 2) which keeps the bounding-box semantics without the
            // extra IO pass.
            var matched = new List<(FrameInfo Light, Matrix3x2 Transform, string Name, int Detected, int QuadCount, float QuadTolUsed)>();
            foreach (var lightInfo in lightList)
            {
                ct.ThrowIfCancellationRequested();

                stageSw.Restart();
                var lightRaw = await lightInfo.LoadFullAsync(ct);
                perfLoad += stageSw.Elapsed;

                stageSw.Restart();
                var calibrated = calibrator.Apply(lightRaw);
                perfCalibrate += stageSw.Elapsed;

                stageSw.Restart();
                var debayered = await calibrated.DebayerAsync(CentroidDebayerAlg, cancellationToken: ct);
                perfDebayer += stageSw.Elapsed;
                var name = Path.GetFileNameWithoutExtension(lightInfo.Path);

                Matrix3x2? transform;
                float quadTolUsed;
                int detected;
                int quadCount = 0;
                string reason = "";

                if (string.Equals(lightInfo.Path, reference.Path, StringComparison.OrdinalIgnoreCase))
                {
                    transform = Matrix3x2.Identity;
                    quadTolUsed = 0f;
                    detected = referenceStars.Count;
                    quadCount = referenceQuads.Count;
                }
                else
                {
                    stageSw.Restart();
                    var stars = await debayered.FindStarsAsync(channel: 0, snrMin: snrMin, minStars: minStars, cancellationToken: ct);
                    perfFindStars += stageSw.Elapsed;
                    detected = stars.Count;

                    if (stars.Count < MinStarsForMatch)
                    {
                        transform = null;
                        quadTolUsed = float.NaN;
                        reason = $"too few stars (<{MinStarsForMatch})";
                    }
                    else
                    {
                        stageSw.Restart();
                        using var lightSorted = new SortedStarList(stars);
                        var lightQuads = await lightSorted.FindQuadsAsync(maxStars: QuadStars, ct);
                        perfBuildQuads += stageSw.Elapsed;
                        quadCount = lightQuads.Count;

                        stageSw.Restart();
                        (transform, quadTolUsed) = await TryMatchAsync(lightSorted, referenceSorted, QuadStars);
                        perfMatch += stageSw.Elapsed;
                        if (transform is null) reason = "no quad fit at any tolerance";
                    }
                    perfFrames++;
                }

                if (transform is null)
                {
                    skipCount++;
                    Log($"  [{name}] stars={detected,4} quads={quadCount,5} -> SKIP ({reason})");
                    continue;
                }

                matched.Add((lightInfo, transform.Value, name, detected, quadCount, quadTolUsed));

                var t = transform.Value;
                var tx = (float)Math.Sqrt(t.M31 * t.M31 + t.M32 * t.M32);
                var rot = (float)(Math.Atan2(t.M12, t.M11) * 180.0 / Math.PI);
                var scale = (float)Math.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
                matchStats.Add((lightInfo.Path, tx, rot, scale, quadTolUsed));
                Log($"  [{name}] stars={detected,4} quads={quadCount,5} -> MATCH qt={quadTolUsed:F3} tx={tx,6:F1}px rot={rot,7:F3}deg");
            }

            // Compute the union bounding box of all matched frames' source
            // footprints in reference space. Include the ref itself (identity
            // transform on its own corners) so the canvas always covers at
            // least the ref's pixels even if every other frame happened to
            // shift away.
            float minX = 0f, minY = 0f;
            float maxX = referenceDebayered.Width, maxY = referenceDebayered.Height;
            // Heap-allocate 4 Vector2 (32 bytes total, one-shot per group) so
            // CA2014 doesn't flag the stackalloc inside the outer per-group
            // foreach. Negligible vs the multi-second match pass.
            var corners = new Vector2[4];
            corners[0] = Vector2.Zero;
            corners[1] = new Vector2(referenceDebayered.Width, 0f);
            corners[2] = new Vector2(0f, referenceDebayered.Height);
            corners[3] = new Vector2(referenceDebayered.Width, referenceDebayered.Height);
            foreach (var (_, mt, _, _, _, _) in matched)
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
            // Integer-quantise outward so we never lose a sub-pixel of valid data.
            var outOriginX = (int)MathF.Floor(minX);
            var outOriginY = (int)MathF.Floor(minY);
            var outWidth = (int)MathF.Ceiling(maxX) - outOriginX;
            var outHeight = (int)MathF.Ceiling(maxY) - outOriginY;
            Log($"  [canvas] union bbox = {outWidth}x{outHeight} (origin {outOriginX},{outOriginY} in ref space); ref was {referenceDebayered.Width}x{referenceDebayered.Height}");
            var canvasShift = Matrix3x2.CreateTranslation(-outOriginX, -outOriginY);

            // Compute the intersection of all warped frames' rotated-quad
            // footprints on the canvas. We use the AABB of this convex polygon
            // as the per-frame stats window for Normalizer.ComputeStats below,
            // so each frame's (min, median) is computed over the same central
            // region instead of its full extent. That avoids the union-BB
            // failure mode where a frame whose valid pixels happen to be
            // mostly sky background collapses (median - min) to ~0 and blows
            // up its normalization scale by 100x+.
            //
            // The AABB can overhang the polygon by small slivers where some
            // frame has NaN -- Normalizer's NaN-skipping handles that, we
            // just get slightly fewer valid samples in that frame.
            var intersectionPoly = new List<Vector2>
            {
                new(0f,        0f),
                new(outWidth,  0f),
                new(outWidth,  outHeight),
                new(0f,        outHeight),
            };
            var srcW = (float)referenceDebayered.Width;
            var srcH = (float)referenceDebayered.Height;
            // Heap-allocate the 4-corner buffer once outside the loop (CA2014:
            // stackalloc-in-loop). 32 bytes per group is negligible vs the
            // multi-second warp pass that follows.
            var quad = new Vector2[4];
            // Per-frame footprint AABB on the canvas. Same quad we use for the
            // intersection clip, AABB'd and clamped to canvas bounds, written
            // to IntegrationJob.FrameFootprints so FootprintStagedStrategy can
            // stage only the non-NaN sub-region of each warped frame.
            var frameFootprints = new List<Rectangle>(matched.Count);
            foreach (var (_, transformOrig, _, _, _, _) in matched)
            {
                var t = transformOrig * canvasShift;
                quad[0] = Vector2.Transform(new Vector2(0f,   0f),   t);
                quad[1] = Vector2.Transform(new Vector2(srcW, 0f),   t);
                quad[2] = Vector2.Transform(new Vector2(srcW, srcH), t);
                quad[3] = Vector2.Transform(new Vector2(0f,   srcH), t);
                // Per-frame footprint AABB before EnsureCwInCanvas reorders the
                // quad (axis-aligned bbox is winding-invariant either way).
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

                EnsureCwInCanvas(quad); // post-meridian-flip frames flip winding
                intersectionPoly = ClipConvex(intersectionPoly, quad);
                if (intersectionPoly.Count == 0) break;
            }
            // If the intersection short-circuited (frames disjoint), fill the
            // remaining footprints with full canvas so we don't index past the
            // end of the list during dispatch.
            while (frameFootprints.Count < matched.Count)
            {
                frameFootprints.Add(new Rectangle(0, 0, outWidth, outHeight));
            }
            {
                long totalFootprintBytes = 0;
                long fullCanvasBytes = (long)outWidth * outHeight * 3 * sizeof(float) * matched.Count;
                foreach (var f in frameFootprints)
                {
                    totalFootprintBytes += (long)f.Width * f.Height * 3 * sizeof(float);
                }
                Log($"  [footprints] {matched.Count} frames, total {totalFootprintBytes / 1e9:F2} GB " +
                    $"(full canvas would be {fullCanvasBytes / 1e9:F2} GB, " +
                    $"{(1.0 - (double)totalFootprintBytes / fullCanvasBytes):P1} saved)");
            }
            Rectangle statsRect;
            if (intersectionPoly.Count == 0)
            {
                Log("  [stats-rect] intersection empty (frames disjoint); falling back to whole-frame stats");
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
                Log($"  [stats-rect] intersection AABB = ({statsRect.X},{statsRect.Y}) {statsRect.Width}x{statsRect.Height} " +
                    $"(canvas {outWidth}x{outHeight}, coverage {(double)statsRect.Width * statsRect.Height / ((double)outWidth * outHeight):P1})");
            }

            // Pass B: producer that re-loads each matched frame and warps into
            // the BB canvas, yielding one Image at a time. The chosen
            // integration strategy decides whether to keep them in RAM
            // (InRamAllFrames) or stage them to disk (FootprintStaged etc.);
            // staged strategies stage + drop each frame as it comes out so
            // peak RAM stays bounded.
            var yieldedCount = 0;
            async IAsyncEnumerable<Image> WarpedFramesProducer(
                [EnumeratorCancellation] CancellationToken token)
            {
                foreach (var (lightInfo, transformOrig, name, _, _, _) in matched)
                {
                    token.ThrowIfCancellationRequested();
                    stageSw.Restart();
                    var lightRaw = await lightInfo.LoadFullAsync(token);
                    perfLoad += stageSw.Elapsed;
                    stageSw.Restart();
                    var calibrated = calibrator.Apply(lightRaw);
                    perfCalibrate += stageSw.Elapsed;
                    stageSw.Restart();
                    var debayered = await calibrated.DebayerAsync(StackDebayerAlg, cancellationToken: token);
                    perfDebayer += stageSw.Elapsed;
                    stageSw.Restart();
                    var shifted = transformOrig * canvasShift;
                    var warped = await debayered.WarpToReferenceGridAsync(shifted, outWidth, outHeight, token);
                    perfWarp += stageSw.Elapsed;
                    yieldedCount++;
                    yield return warped;
                }
            }

            LogMatchSummary(matchStats, Log);

            if (matched.Count < 2)
            {
                Log("  [skip] fewer than 2 matched frames; integration would be meaningless");
                try { Directory.Delete(stagingDir, recursive: true); }
                catch { /* hygiene only */ }
                continue;
            }

            // Snapshot host + log every strategy's verdict before dispatching.
            // Default policy is FidelityFirst so behaviour matches the
            // pre-strategy code: highest-fidelity that fits wins, speed is
            // only a tiebreaker once a CLI flag opens up Balanced / SpeedFirst.
            var probe = IntegrationProbe.Snapshot(
                frameCount: matched.Count,
                frameWidth: referenceDebayered.Width,
                frameHeight: referenceDebayered.Height,
                channelCount: 3,
                canvasWidth: outWidth,
                canvasHeight: outHeight,
                stagingDir: stagingDir,
                stagingDiskKind: DiskKind.Ssd);
            // Auto-pick under FidelityFirst policy unless `forceStrategy` is
            // set above to a specific kind (override path for validating an
            // individual strategy against the real dataset). TilePipelined now
            // returns CanRun=true (Phase 8.2) so it ranks against the others
            // by speed-fidelity blend; under FidelityFirst it loses to InRam
            // unless InRam exceeds the RAM budget.
            var selection = IntegrationStrategySelector.Pick(probe, preferred: forceStrategy);
            Log($"  [strategy] probe: N={probe.FrameCount} frame={probe.FrameWidth}x{probe.FrameHeight}x{probe.ChannelCount} " +
                $"canvas={probe.CanvasWidth}x{probe.CanvasHeight} freeRam={probe.AvailableRamBytes / 1e9:F1}GB " +
                $"freeDisk={probe.AvailableDiskBytes / 1e9:F1}GB disk={probe.StagingDiskKind}");
            foreach (var c in selection.Considered)
            {
                var verdict = c.Fit.CanRun ? "YES" : "NO ";
                Log($"  [strategy] {c.Strategy.Kind,-17} {verdict} fidel={c.Strategy.FidelityScore:F2} " +
                    $"eta={c.Fit.EstimatedDuration.TotalSeconds,5:F0}s ram={c.Fit.EstimatedRamBytes / 1e9,5:F1}GB " +
                    $"disk={c.Fit.EstimatedDiskBytes / 1e9,5:F1}GB  {c.Fit.Rationale}");
            }
            Log($"  [strategy] picked {selection.Chosen.Kind} -- {selection.Notes}");

            // 3c. Dispatch through the chosen strategy. The producer drives
            // both pass-B perf timing and the actual warp work via lazy
            // iteration -- nothing executes until the strategy starts
            // consuming the enumerable.
            sw.Restart();
            var rejector = BuildRejector(matched.Count);
            Log($"  rejector: {rejector?.GetType().Name ?? "<none>"}");
            // Build the raw-source list for TilePipelined: same matched-frame
            // transforms the producer uses, just packaged with the source FITS
            // path so the strategy can skip the producer's full-frame warp
            // pass and (eventually) do per-tile raw reads. Other strategies
            // ignore this field.
            var rawSources = new List<RawLightSource>(matched.Count);
            foreach (var (lightInfo, transformOrig, _, _, _, _) in matched)
            {
                rawSources.Add(new RawLightSource(
                    Path: lightInfo.Path,
                    TransformToCanvas: transformOrig * canvasShift));
            }

            var job = new IntegrationJob(
                WarpedFrames: WarpedFramesProducer,
                ExpectedFrameCount: matched.Count,
                Options: new IntegrationOptions(Rejector: rejector),
                StagingDir: stagingDir,
                StatsRect: statsRect,
                FrameFootprints: frameFootprints,
                RawLightSources: rawSources,
                Calibrator: calibrator,
                DebayerAlgorithm: StackDebayerAlg,
                CanvasWidth: outWidth,
                CanvasHeight: outHeight);
            IntegrationResult result;
            try
            {
                result = await selection.Chosen.RunAsync(job, ct);
            }
            catch (NotImplementedException ex)
            {
                Log($"  [strategy] {selection.Chosen.Kind} threw NotImplementedException: {ex.Message}");
                Log($"  [strategy] aborting this group; pick a different --strategy or implement the chosen one");
                try { Directory.Delete(stagingDir, recursive: true); }
                catch { /* hygiene */ }
                continue;
            }
            // TilePipelined consumes RawLightSources directly and never pulls
            // from WarpedFrames, so yieldedCount stays at 0 -- only the staged
            // / in-RAM strategies advertise yield/skip progress. Suppress the
            // "yielded 0/N" tag for the raw-consuming strategies so the line
            // doesn't read as "stack failed to load anything".
            var producerLabel = yieldedCount > 0
                ? $" (yielded {yieldedCount}/{matched.Count})"
                : $" (raw-sourced, N={matched.Count})";
            Log($"  integrated in {sw.ElapsedMilliseconds} ms{producerLabel}");
            Log($"    frames: {result.FrameCount}, total rejections: {result.TotalRejections}, mean rate: {result.MeanRejectionRate:P2}");

            // Per-stage cost breakdown after dispatch. All perf accumulators
            // were populated by the producer iterator above (pass A counters
            // came from the match loop earlier in this group).
            if (perfFrames > 0)
            {
                var totalNs = (perfLoad + perfCalibrate + perfDebayer + perfFindStars + perfBuildQuads + perfMatch + perfWarp).TotalMilliseconds;
                static string Pct(TimeSpan part, double total) => total > 0 ? $"{part.TotalMilliseconds / total * 100:F1}%" : "n/a";
                static string Per(TimeSpan total, int n) => n > 0 ? $"{total.TotalMilliseconds / n:F0} ms/frame" : "n/a";
                Log($"  [perf] {perfFrames} non-ref frames timed, total stage ms: {totalNs:F0}");
                Log($"    Load      : {perfLoad.TotalMilliseconds,7:F0} ms  ({Pct(perfLoad, totalNs),5})  avg {Per(perfLoad, perfFrames)}");
                Log($"    Calibrate : {perfCalibrate.TotalMilliseconds,7:F0} ms  ({Pct(perfCalibrate, totalNs),5})  avg {Per(perfCalibrate, perfFrames)}");
                Log($"    Debayer   : {perfDebayer.TotalMilliseconds,7:F0} ms  ({Pct(perfDebayer, totalNs),5})  avg {Per(perfDebayer, perfFrames)}");
                Log($"    FindStars : {perfFindStars.TotalMilliseconds,7:F0} ms  ({Pct(perfFindStars, totalNs),5})  avg {Per(perfFindStars, perfFrames)}");
                Log($"    BuildQuads: {perfBuildQuads.TotalMilliseconds,7:F0} ms  ({Pct(perfBuildQuads, totalNs),5})  avg {Per(perfBuildQuads, perfFrames)}");
                Log($"    Match     : {perfMatch.TotalMilliseconds,7:F0} ms  ({Pct(perfMatch, totalNs),5})  avg {Per(perfMatch, perfFrames)}");
                Log($"    Warp      : {perfWarp.TotalMilliseconds,7:F0} ms  ({Pct(perfWarp, totalNs),5})  avg {Per(perfWarp, perfFrames)}");
            }
            var attempted = yieldedCount + skipCount;
            Log($"  registered + warped {yieldedCount}/{attempted} frames " +
                $"(skipped {skipCount}) in {sw.ElapsedMilliseconds} ms");

            if (result.FrameCount < 2)
            {
                Log("  [skip] fewer than 2 frames in the integrated master; result not usable");
                continue;
            }

            // 3d. Post-processing: background neutralization -> plate solve ->
            // SPCC white balance -> bake WCS into the master FITS -> render a
            // display-encoded preview PNG. All best-effort: any step's failure
            // logs + continues, since this is an experimental pipeline test.
            // Pass the reference frame's ImageDim explicitly -- meta tends to
            // get partly stripped through debayer/warp/integrate, and the
            // solver bails immediately if GetImageDim() returns null.
            var masterPath = Path.Combine(outputDir, $"master_{key.Slug()}.fits");
            var previewPath = Path.Combine(outputDir, $"master_{key.Slug()}.png");
            var refImageDim = referenceRaw.GetImageDim();
            Log($"  master meta: focalLen={result.Master.ImageMeta.FocalLength}, " +
                $"pixSz={result.Master.ImageMeta.PixelSizeX}, sensor={result.Master.ImageMeta.SensorModel ?? "<null>"}, " +
                $"instr={result.Master.ImageMeta.Instrument ?? "<null>"}");
            Log($"  ref meta: focalLen={referenceRaw.ImageMeta.FocalLength}, " +
                $"pixSz={referenceRaw.ImageMeta.PixelSizeX}, sensor={referenceRaw.ImageMeta.SensorModel ?? "<null>"}");
            await PostProcessAndWriteAsync(result, masterPath, previewPath, searchHint, refImageDim, referenceRaw.ImageMeta, statsRect, Log, ct);
            if (result.TotalRejections > 0)
            {
                Log($"  wrote {IntegrationFitsWriter.RejectionPathFor(masterPath)}");
            }

            // Staging cleanup is the strategy's responsibility (FootprintStaged
            // deletes its dir in its finally block). Nothing to do here beyond
            // the start-of-group hygiene above.
        }

        Log($"[end] {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    }

    private static async Task<List<(MasterGroupKey Key, Image Master)>> BuildMastersAsync(
        List<FrameInfo>? frames,
        Func<IReadOnlyList<FrameInfo>, System.Threading.CancellationToken, Task<Image>> builder,
        string _unusedLabel,
        string mastersDir,
        Action<string> log,
        System.Threading.CancellationToken ct)
    {
        var masters = new List<(MasterGroupKey, Image)>();
        if (frames is null || frames.Count == 0) return masters;

        foreach (var group in frames.GroupBy(MasterGroupKey.FromFrame))
        {
            var key = group.Key;
            var list = group.ToList();
            if (list.Count < 2) continue;

            var masterPath = Path.Combine(mastersDir, $"master_{key.Slug()}.fits");

            // Cache hit: master from a previous run. Bias/dark/flat masters are
            // pure functions of their inputs + builder settings, so if the file
            // exists we trust it. Re-run to refresh by deleting outputDir/masters.
            if (File.Exists(masterPath) && Image.TryReadFitsFile(masterPath, out var cached) && cached is not null)
            {
                masters.Add((key, cached));
                log($"  cached {Path.GetFileName(masterPath)} ({list.Count} input frames)");
                continue;
            }

            var master = await builder(list, ct);
            masters.Add((key, master));

            // Slug already encodes the frame type, so "master_<slug>.fits" is enough
            // (avoids the double-naming "master_bias_bias_..." from earlier).
            master.WriteToFitsFile(masterPath);
            log($"  built  {Path.GetFileName(masterPath)} ({list.Count} input frames)");
        }
        return masters;
    }

    /// <summary>Logs star-count distribution + top-5 frames so we can spot
    /// "reference picked from a near-empty frame" pathologies.</summary>
    private static void LogStarCountDistribution(List<(FrameInfo Frame, int StarCount)> stats, Action<string> log)
    {
        if (stats.Count == 0) { log("  [stars] no frames scanned"); return; }
        var counts = stats.Select(s => s.StarCount).OrderBy(c => c).ToArray();
        var min = counts[0];
        var max = counts[^1];
        var median = counts[counts.Length / 2];
        var mean = counts.Average();
        var withStars = counts.Count(c => c > 0);
        log($"  [stars] {stats.Count} frames scanned: " +
            $"min={min}, median={median}, mean={mean:F1}, max={max}, with-stars={withStars}/{stats.Count}");
        var top5 = stats.OrderByDescending(s => s.StarCount).Take(5).ToArray();
        foreach (var (frame, count) in top5)
        {
            log($"    {count,5} stars: {Path.GetFileName(frame.Path)}");
        }
    }

    /// <summary>Ladder of quadTolerance values to try per frame, ascending.
    /// First-match wins. The lower rungs (0.008, 0.02, 0.05) are tuned for the
    /// all-stars quad path where fingerprints are dense and small drift only
    /// nudges Dist1/ratios fractionally. The top-K path (see <c>QuadStars</c>)
    /// has 20x fewer quads and a much sparser signature space, so cross-flip
    /// frames typically match at qt=0.1-0.2. The 0.5 ceiling is the runaway
    /// guard: false-positive cross-object pairs are still rejected by the
    /// affine validator + RANSAC min-inlier=4 even at this tolerance.</summary>
    private static readonly float[] QuadTolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    private static async Task<(Matrix3x2? Solution, float QuadTolerance)> TryMatchAsync(
        SortedStarList light, SortedStarList reference, int maxStars)
    {
        foreach (var tol in QuadTolerances)
        {
            // FindFitAsync internally memoizes FindQuadsAsync (per maxStars key),
            // so re-trying with a looser tolerance only re-runs the match pass,
            // not the quad build.
            var solution = await light.FindOffsetAndRotationAsync(reference, minimumCount: 6, quadTolerance: tol, maxStars: maxStars);
            if (solution is not null) return (solution, tol);
        }
        return (null, float.NaN);
    }

    /// <summary>Logs decomposed-transform stats for successful matches so we
    /// can sanity-check the registrator (translation magnitudes, rotation
    /// spread, scale uniformity, and which quad-tolerance levels were needed).
    /// Splits translation stats by pier side -- post-meridian-flip frames have
    /// a ~4250px "translation" component that is really the affine encoding of
    /// a 180-deg rotation around the image center, not actual mount drift.
    /// Lumping them with same-side frames hides the true dither magnitude.</summary>
    private static void LogMatchSummary(
        List<(string Path, float TranslationPx, float RotationDeg, float Scale, float QuadTol)> stats,
        Action<string> log)
    {
        if (stats.Count == 0) { log("  [match] no successful matches"); return; }
        var rotations = stats.Select(s => s.RotationDeg).OrderBy(v => v).ToArray();
        var scales = stats.Select(s => s.Scale).OrderBy(v => v).ToArray();
        log($"  [match] rotation deg:   min={rotations[0]:F3}, " +
            $"median={rotations[rotations.Length / 2]:F3}, max={rotations[^1]:F3}");
        log($"  [match] scale factor:   min={scales[0]:F5}, " +
            $"median={scales[scales.Length / 2]:F5}, max={scales[^1]:F5}");

        LogTranslationGroup(stats.Where(s => Math.Abs(s.RotationDeg) < 90f).ToList(), "same-side", log);
        LogTranslationGroup(stats.Where(s => Math.Abs(s.RotationDeg) >= 90f).ToList(), "flipped ", log);

        // Histogram of which quadTolerance level each match needed -- tells us
        // whether the dataset would have stacked fine with the default 0.008
        // (just slow-converging) or genuinely needs a looser cap.
        foreach (var grp in stats.GroupBy(s => s.QuadTol).OrderBy(g => g.Key))
        {
            log($"  [match] quadTol {grp.Key:F3}: {grp.Count()} frame(s)");
        }
    }

    private static void LogTranslationGroup(
        List<(string Path, float TranslationPx, float RotationDeg, float Scale, float QuadTol)> group,
        string label,
        Action<string> log)
    {
        if (group.Count == 0) return;
        var t = group.Select(s => s.TranslationPx).OrderBy(v => v).ToArray();
        log($"  [match] translation px ({label}, n={group.Count}): " +
            $"min={t[0]:F1}, median={t[t.Length / 2]:F1}, max={t[^1]:F1}");
    }

    /// <summary>Find the best master for a light group: exact key match preferred, else
    /// match on (SensorType, ChannelCount, Width, Height) with closest Exposure/Temp.
    /// Returns the matched key alongside the image so callers can log which group
    /// was picked.</summary>
    private static (Image? Master, MasterGroupKey? Key) MatchMaster(List<(MasterGroupKey Key, Image Master)> masters, MasterGroupKey lightKey)
    {
        if (masters.Count == 0) return (null, null);

        // Best is exact match. Fall back to same-shape match closest in temp + exposure.
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

    /// <summary>
    /// Post-integration pipeline: background neutralization -> plate solve ->
    /// SPCC white balance -> write master FITS with WCS baked in -> render
    /// display-encoded preview PNG. Each step is best-effort -- a failure
    /// logs and moves on so a missing FilterCurve database or unsolvable
    /// frame doesn't abort the whole stacking run. Mutates
    /// <paramref name="result"/>.Master in place (BG neutralize, WB) before
    /// writing.
    /// </summary>
    private static async Task PostProcessAndWriteAsync(
        IntegrationResult result,
        string masterPath,
        string previewPath,
        WCS? searchHint,
        ImageDim? imageDim,
        ImageMeta refMeta,
        Rectangle autocropRect,
        Action<string> Log,
        System.Threading.CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var master = result.Master;

        // 0) Fix the master's MaxValue tag without rescaling pixels. The
        //    integrator inherits MaxValue from the source frames (65535) but
        //    its actual pixel data is already in [0, 1] -- the warp + debayer
        //    pipeline emits normalised floats. The metadata-vs-data mismatch
        //    silently breaks downstream consumers that key on MaxValue: most
        //    importantly Histogram(), which puts MaxValue=65535 into the
        //    "non-rescale" branch and produces a 59k-bin histogram for pixel
        //    data living entirely in bin 0. FindStarsAsync's noise estimator
        //    then mis-calibrates and detects 20-50 stars instead of thousands.
        //    Calling ScaleFloatValuesToUnitInPlace would DIVIDE the already-
        //    [0,1] data by 65535, sending everything to ~1e-5 (the previous
        //    bug -- bg reported (0,0,0)). Wrap the same data arrays in a new
        //    Image record with MaxValue=1 instead; no pixel mutation.
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

        // 0b) Pre-compute the cropped master that the bgScan + PNG stretch
        //     stats will use. The union-BB master has NaN edges + low-coverage
        //     canvas slivers wherever not every frame contributed; sampling
        //     those drags background medians and stretch shadows away from
        //     where the rendered (full) image actually wants them. The crop
        //     is reused later for the _autocrop FITS/PNG output, so the
        //     allocation amortises.
        IntegrationResult? croppedResult = null;
        var statsSource = master;
        if (autocropRect.Width > 0 && autocropRect.Height > 0 &&
            (autocropRect.Width < master.Width || autocropRect.Height < master.Height))
        {
            croppedResult = CropIntegrationResult(result, autocropRect);
            statsSource = croppedResult.Master;
            Log($"  [stats-source] cropped {statsSource.Width}x{statsSource.Height} " +
                $"(from {master.Width}x{master.Height})");
        }

        // 1) Scan the sky background -- we need the per-channel medians to
        //    compute bg-neut gains, but DO NOT compute the gains yet. The
        //    shader applies bg-neut BEFORE WB, so if WB is non-identity (e.g.
        //    SPCC's typical R=0.27/B=1.43) the post-WB bg is no longer
        //    neutral. Defer the gain calc until step 4, after WB is known,
        //    so we can derive bn that neutralises the bg POST-shader.
        //    Computed against `statsSource` (cropped if available) so NaN
        //    edge pixels and low-frame-count canvas slivers don't bias the bg
        //    medians.
        float[]? perChannelBg = null;
        if (statsSource.ChannelCount >= 3)
        {
            var pedestals = new float[statsSource.ChannelCount];
            (perChannelBg, _) = statsSource.ScanBackgroundRegion(pedestals);
            Log($"  [bgScan] bg=({perChannelBg[0]:F4}, {perChannelBg[1]:F4}, {perChannelBg[2]:F4})");
        }

        // 2) Plate solve the master against the local Tycho-2 catalog using
        //    the FITS header WCS as a starting hint.
        WCS? solvedWcs = null;
        if (searchHint is { } hint)
        {
            try
            {
                var db = await SharedCatalogDB.InitAsync(ct);
                var solver = new CatalogPlateSolver(db);
                var psResult = await solver.SolveImageAsync(master, imageDim: imageDim, searchOrigin: hint, cancellationToken: ct);
                if (psResult.Solution is { } w)
                {
                    solvedWcs = w;
                    Log($"  [plateSolve] RA={w.CenterRA:F6}h Dec={w.CenterDec:F6}°  " +
                        $"matched={psResult.MatchedStars}/{psResult.DetectedStars} ({psResult.Elapsed.TotalMilliseconds:F0} ms)");
                }
                else
                {
                    Log($"  [plateSolve] no solution (matched={psResult.MatchedStars}/{psResult.DetectedStars}, {psResult.Elapsed.TotalMilliseconds:F0} ms)");
                }
            }
            catch (Exception ex)
            {
                Log($"  [plateSolve] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Log("  [plateSolve] skipped (no search hint)");
        }

        // 3) White balance. Try SPCC (Tycho-2 photometric matching + sensor
        //    throughput) first; if it can't run (no plate-solve, no throughput,
        //    or too few catalog matches) fall back to sky-background WB --
        //    "sky should be grey", same fallback the viewer's Calibrate button
        //    uses. Either way we DO NOT mutate the master -- WB is applied via
        //    stretch uniforms in the PNG render, keeping the FITS as the raw
        //    integrated stack.
        (float R, float G, float B)? wbGains = null;
        if (master.ChannelCount >= 3)
        {
            // Detect stars on the master once; SPCC needs the centroid list,
            // sky-bg fallback needs the star mask to exclude stars from the
            // bg sample. Cap at 500 -- the histogram fix lifted detection
            // counts from ~50 to ~11k on the master, and SPCC's photometry +
            // catalog-match work scales with that count (17 seconds at 11k).
            // The white-balance multiplier is a robust median of per-star
            // ratios, so the brightest 500 land it on the same answer as all
            // 11k. Plate solve uses its own internal star detector with a
            // 500 cap already, so this doesn't affect plate solving.
            StarList? masterStars = null;
            try
            {
                masterStars = await master.FindStarsAsync(channel: 0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Log($"  [WB] star detection failed: {ex.GetType().Name}: {ex.Message}");
            }

            // --- 3a. SPCC (requires WCS + sensor throughput + >= minStars) ---
            if (solvedWcs is { } wcs && masterStars is { Count: >= 3 })
            {
                var spccSw = Stopwatch.StartNew();
                try
                {
                    if (!FilterCurveDatabase.IsLoaded)
                    {
                        await FilterCurveDatabase.LoadAsync(ct);
                    }
                    // Master.ImageMeta tends to lose sensor / instrument fields
                    // through the staging round-trip; fall back to the reference
                    // frame's meta which still has the original FITS keywords.
                    var meta = string.IsNullOrEmpty(master.ImageMeta.SensorModel) ? refMeta : master.ImageMeta;
                    var throughputs = FilterCurveDatabase.BuildChannelThroughputs(meta);
                    if (throughputs is { } t)
                    {
                        var db = await SharedCatalogDB.InitAsync(ct);
                        var spcc = Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
                            master, masterStars, wcs, db, t.R, t.G, t.B);
                        if (spcc is { } wb)
                        {
                            wbGains = (wb.R, wb.G, wb.B);
                            Log($"  [SPCC] WB=({wb.R:F3}, {wb.G:F3}, {wb.B:F3}) from {wb.MatchCount} Tycho-2 matches ({spccSw.ElapsedMilliseconds} ms)");
                        }
                        else
                        {
                            Log($"  [SPCC] insufficient matches ({spccSw.ElapsedMilliseconds} ms); will try sky-bg fallback");
                        }
                    }
                    else
                    {
                        Log($"  [SPCC] no channel throughput for this sensor ({spccSw.ElapsedMilliseconds} ms); will try sky-bg fallback");
                    }
                }
                catch (Exception ex)
                {
                    Log($"  [SPCC] failed after {spccSw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // --- 3b. Sky-bg WB fallback (no WCS needed; assumes sky is grey) ---
            if (wbGains is null && masterStars is { StarMask: { } mask })
            {
                var skySw = Stopwatch.StartNew();
                var skyWb = AstroImageDocument.ComputeSkyBackgroundWB(master, mask);
                if (skyWb is { } w)
                {
                    wbGains = w;
                    Log($"  [skyBgWB] WB=({w.R:F3}, {w.G:F3}, {w.B:F3}) from darkest 10% sky-as-grey ({skySw.ElapsedMilliseconds} ms)");
                }
                else
                {
                    Log($"  [skyBgWB] insufficient clean samples after star mask ({skySw.ElapsedMilliseconds} ms); skipping WB");
                }
            }
            else if (wbGains is null)
            {
                Log("  [skyBgWB] skipped (no star mask available for bg exclusion)");
            }
        }

        // 3c) Now compute the bg-neut gains the shader will actually use.
        //     The shader applies bg-neut BEFORE WB: out = (raw*bn + (1-bn)) * wb.
        //     For the post-shader bg to be neutral across channels we need
        //     (bg_X * bn_X + (1 - bn_X)) * wb_X = K for some shared K.
        //     Solving for MinPivot (K = min over X of bg_X * wb_X):
        //         bn_X = (K / wb_X - 1) / (bg_X - 1)
        //     With wb=(1,1,1) this collapses to the standard MinPivot formula
        //     in BackgroundNeutralization.ComputeGains. With SPCC's extreme
        //     wb=(0.27, 1.0, 1.43) it produces bn=(1.0, 1.71, 1.79) -- which,
        //     fed to the shader, lands all three channels at bg=0.13 instead
        //     of the (0.13, 0.49, 0.70) blue cast the prior code produced.
        (float R, float G, float B)? bgGains = null;
        if (perChannelBg is { Length: >= 3 } bg)
        {
            var wb = wbGains ?? (1f, 1f, 1f);
            var bgRWb = bg[0] * wb.R;
            var bgGWb = bg[1] * wb.G;
            var bgBWb = bg[2] * wb.B;
            var pivot = MathF.Min(bgRWb, MathF.Min(bgGWb, bgBWb));
            float Solve(float bgX, float wbX) =>
                MathF.Abs(bgX - 1f) < 1e-6f ? 1f
                    : Math.Clamp((pivot / wbX - 1f) / (bgX - 1f), 0f, 10f);
            var bn = (Solve(bg[0], wb.R), Solve(bg[1], wb.G), Solve(bg[2], wb.B));
            bgGains = bn;
            Log($"  [bgNeut/MinPivot-postWB] target={pivot:F4} gains=({bn.Item1:F3}, {bn.Item2:F3}, {bn.Item3:F3})");
        }

        // 4) Write the master FITS with the solved WCS baked into the headers
        //    (CRVAL/CRPIX/CD matrix etc., via IntegrationFitsWriter's wcs arg).
        //    Pixels are the raw integrated stack; bg-neut + WB are display ops.
        var fitsSw = Stopwatch.StartNew();
        IntegrationFitsWriter.Write(masterPath, result, solvedWcs);
        Log($"  wrote {masterPath}{(solvedWcs is null ? "" : " (WCS embedded)")} ({fitsSw.ElapsedMilliseconds} ms)");

        // 5) Display-encoded preview PNG. Stretch uniforms carry bg-neut + WB
        //    so they apply in the shader path (per-channel normalize -> bg
        //    neut -> WB -> shadow/MTF), keeping channels aligned in Unlinked.
        try
        {
            var pngSw = Stopwatch.StartNew();
            // Render the full union-BB master, but compute the stretch stats
            // from statsSource (cropped master if available). The PNG pixels
            // still come from the full master so the user sees the full canvas
            // including any NaN edge regions; the stretch is anchored on the
            // well-covered data.
            var (statsMs, renderMs, encodeMs) = RenderPreviewPng(master, previewPath, bgGains, wbGains, statsSource: statsSource);
            Log($"  wrote {previewPath} ({pngSw.ElapsedMilliseconds} ms total: stats={statsMs}ms, render={renderMs}ms, encode={encodeMs}ms)");
        }
        catch (Exception ex)
        {
            Log($"  [preview] failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 6) Autocrop output: same master cropped to the intersection AABB so
        //    ASTAP / PixInsight / tifffile etc. get a clean rectangular image
        //    with no NaN edges. WCS CRPIX shifts by the crop offset so
        //    plate-solve coords still map to the same sky position. Reuses
        //    the croppedResult already computed in step 0b for bgScan +
        //    stretch stats so we don't allocate the ~108 MB crop twice.
        if (croppedResult is not null)
        {
            try
            {
                var cropSw = Stopwatch.StartNew();
                WCS? croppedWcs = solvedWcs is { } w
                    ? w with { CRPix1 = w.CRPix1 - autocropRect.X, CRPix2 = w.CRPix2 - autocropRect.Y }
                    : null;
                var cropFitsPath = WithSuffix(masterPath, "_autocrop");
                var cropPngPath  = WithSuffix(previewPath, "_autocrop");
                IntegrationFitsWriter.Write(cropFitsPath, croppedResult, croppedWcs);
                Log($"  wrote {cropFitsPath} (crop {autocropRect.Width}x{autocropRect.Height}, " +
                    $"{cropSw.ElapsedMilliseconds} ms{(croppedWcs is null ? "" : ", WCS embedded")})");
                var pngSw = Stopwatch.StartNew();
                var (cStatsMs, cRenderMs, cEncodeMs) = RenderPreviewPng(croppedResult.Master, cropPngPath, bgGains, wbGains);
                Log($"  wrote {cropPngPath} ({pngSw.ElapsedMilliseconds} ms total: stats={cStatsMs}ms, render={cRenderMs}ms, encode={cEncodeMs}ms)");
            }
            catch (Exception ex)
            {
                Log($"  [autocrop] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Log($"  [autocrop] skipped (rect {autocropRect.Width}x{autocropRect.Height} not smaller than master {master.Width}x{master.Height})");
        }

        Log($"  [post] total {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Inserts <paramref name="suffix"/> before the extension of
    /// <paramref name="path"/>: <c>"a/b/master.fits", "_autocrop"</c>
    /// -&gt; <c>"a/b/master_autocrop.fits"</c>.
    /// </summary>
    private static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, stem + suffix + ext);
    }

    /// <summary>
    /// Returns a new <see cref="IntegrationResult"/> with both <c>Master</c>
    /// and <c>RejectionMap</c> cropped to <paramref name="rect"/>. The crop
    /// allocates fresh per-channel arrays so callers can safely write the
    /// cropped FITS while the full master is still alive (no array sharing).
    /// </summary>
    private static IntegrationResult CropIntegrationResult(IntegrationResult full, Rectangle rect)
    {
        var croppedMaster = CropImage(full.Master, rect);
        var croppedRejection = CropImage(full.RejectionMap, rect);
        return full with { Master = croppedMaster, RejectionMap = croppedRejection };
    }

    /// <summary>
    /// Copies the pixels inside <paramref name="rect"/> into a new
    /// <see cref="Image"/>, preserving channel count, bit depth, and meta.
    /// Rect is clamped to image bounds; out-of-range coordinates throw.
    /// </summary>
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

    private static (long StatsMs, long RenderMs, long EncodeMs) RenderPreviewPng(
        Image image,
        string path,
        (float R, float G, float B)? bgGains,
        (float R, float G, float B)? wbGains,
        Image? statsSource = null)
    {
        // statsSource lets the caller compute the per-channel stretch stats
        // (pedestal / median / MAD) from a DIFFERENT image than the one being
        // rendered. For the union-BB master we pass the cropped master here so
        // shadow/midtone/rescale come from the geometrically valid region
        // instead of being biased by NaN edges + low-coverage canvas slivers.
        // The actual pixel values written to PNG still come from `image`.
        var stats = statsSource ?? image;
        var sw = Stopwatch.StartNew();
        var (channelCount, width, height) = image.Shape;
        if (stats.ChannelCount != channelCount)
        {
            throw new ArgumentException(
                $"statsSource channel count ({stats.ChannelCount}) must match image channel count ({channelCount}).",
                nameof(statsSource));
        }
        var perChannelStats = new ChannelStretchStats[channelCount];
        Span<float> bgPerCh = stackalloc float[3];
        if (bgGains is { } gIn) { bgPerCh[0] = gIn.R; bgPerCh[1] = gIn.G; bgPerCh[2] = gIn.B; }
        else { bgPerCh[0] = bgPerCh[1] = bgPerCh[2] = 1f; }
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = stats.GetPedestralMedianAndMADScaledToUnit(c);
            // Pre-adjust stats into the POST-bg-neut coordinate space so the
            // shadow lands where the shader will actually see the pixel
            // after `norm = norm * bn + (1 - bn)`. Stats are already scaled
            // to unit, so the same formula applies. Without this, Unlinked
            // mode keeps the pre-neut shadow (e.g. G shadow ≈ G_median - 5σ)
            // but the shader compares against (G_median * bn_G + (1 - bn_G)),
            // which lands BELOW that shadow for any bg-neut that shifts
            // toward the minimum channel -> G/B clamp to 0 -> the master
            // renders all-red. Channel ≥3 falls through with bn=1 (no shift).
            var bn = c < 3 ? bgPerCh[c] : 1f;
            var adjMed = med * bn + (1f - bn);
            var adjMad = mad * MathF.Abs(bn);
            perChannelStats[c] = new ChannelStretchStats(ped, adjMed, adjMad);
        }
        // Unlinked: each channel uses its own median/MAD for shadow + rescale.
        var uniforms = AstroImageDocument.ComputeStretchUniforms(
            StretchMode.Unlinked,
            StretchParameters.Default,
            perChannelStats,
            lumaStats: null,
            imageMaxValue: image.MaxValue,
            whiteBalance: wbGains);

        if (bgGains is { } bg)
        {
            uniforms = uniforms with { BackgroundNeutralization = bg };
        }
        var statsMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var rgba = new byte[width * height * 4];
        image.RenderStretchedRgba(uniforms, rgba);
        var renderMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var png = PngWriter.Encode(rgba, width, height, IccProfiles.SRgbV4.Span);
        File.WriteAllBytes(path, png);
        var encodeMs = sw.ElapsedMilliseconds;

        return (statsMs, renderMs, encodeMs);
    }

    /// <summary>
    /// Reverses <paramref name="quad"/> in place if its winding is CCW in
    /// canvas-y-down axes, so downstream <see cref="ClipConvex"/>, which
    /// expects CW-in-canvas (= "inside is on the right of the directed edge"
    /// under the cross-product sign convention in <c>ClipConvex</c>), gets a
    /// consistently oriented quad. The canvas rect <c>[(0,0),(W,0),(W,H),(0,H)]</c>
    /// is CW-in-canvas; a 180-degree-rotated frame (post-meridian-flip) flips
    /// winding to CCW-in-canvas and needs reversing.
    /// </summary>
    /// <remarks>
    /// Sign convention: the trapezoid formula <c>sum (b.X - a.X)(b.Y + a.Y)</c>
    /// equals <c>-2 * shoelace</c>. For the canvas rect above it evaluates to
    /// <c>-2 W H &lt; 0</c>, which is our reference for "correctly wound" (CW in
    /// canvas axes). Anything with the opposite sign (positive) is CCW in
    /// canvas axes and gets flipped.
    /// </remarks>
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

    /// <summary>
    /// Sutherland-Hodgman clipping: returns the intersection of the
    /// <paramref name="subject"/> convex polygon with the <paramref name="clip"/>
    /// convex polygon. Both must wind the same way (we pass CCW in canvas
    /// axes -- see <see cref="EnsureCcw"/>).
    /// </summary>
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
            // Inside test: cross product of edge with (point - a). Sign chosen
            // to match the CCW-in-canvas-axes convention used by EnsureCcw
            // (canvas y axis is flipped vs math; "inside" = on the right of
            // the directed edge from a to b in math axes = on the left in
            // canvas axes). Using >= 0 keeps points exactly on the edge.
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

    /// <summary>
    /// Line-line intersection: segment (p1, p2) with edge through (a, b),
    /// computed as the point on (p1, p2) that lies on the infinite line
    /// containing (a, b). Caller has already verified the segment straddles
    /// the edge so the denominator is non-zero.
    /// </summary>
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
}

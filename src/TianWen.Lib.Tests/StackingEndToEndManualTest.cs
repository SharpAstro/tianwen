using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
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
    /// Pixel rejector for the integration step. Swap the active line to pick
    /// an algorithm without touching the call site.
    /// <list type="bullet">
    /// <item><see cref="LinearFitClipRejector"/>: best for N >= 15 dithered subs with
    /// transparency drift. Kills the "rejection halos" plain sigma clip leaves around
    /// every star. PixInsight's gold standard. ~2x cost of sigma clip per iter.</item>
    /// <item><see cref="WinsorizedSigmaClipRejector"/>: less aggressive than plain sigma
    /// clip on the bright tail. Good middle-ground for N >= 8. ~10% over plain.</item>
    /// <item><see cref="SigmaClipRejector"/> (asymmetric): cheap baseline. Use kappa-high
    /// = 5+ to keep star tails when stacking dithered subs.</item>
    /// <item><see cref="PercentileClipRejector"/>: small N (3-7) where MAD-derived sigma
    /// is unreliable, or when contaminant fraction is known a priori.</item>
    /// <item><see cref="MinMaxClipRejector"/>: trivial baseline, "drop the worst k".</item>
    /// </list>
    /// </summary>
    private static IPixelRejector? BuildRejector(int frameCount)
    {
        if (frameCount < 5) return null; // too few frames for any rejection step

        return new LinearFitClipRejector(LowSigma: 3f, HighSigma: 3f, MaxIterations: 5);
        // return new WinsorizedSigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5);
        // return new SigmaClipRejector(LowSigma: 3f, HighSigma: 5f, MaxIterations: 5);
        // return new PercentileClipRejector(LowFraction: 0.1f, HighFraction: 0.1f);
        // return new MinMaxClipRejector(DropLowest: 1, DropHighest: 1);
    }

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
            // VNG is the sweet spot here: faster than AHD, much cleaner stars
            // than BilinearMono. For mono cameras the whole debayer step is a
            // no-op (DebayerAsync short-circuits on Monochrome).
            const DebayerAlgorithm DebayerAlg = DebayerAlgorithm.VNG;
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
                var debayered = await calibrated.DebayerAsync(DebayerAlg, cancellationToken: ct);
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
            var referenceDebayered = await calibrator.Apply(referenceRaw).DebayerAsync(DebayerAlg, cancellationToken: ct);
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
            var aligned = new List<StagedAlignedFrame>();
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
                var debayered = await calibrated.DebayerAsync(DebayerAlg, cancellationToken: ct);
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

            // Pass B: re-load each matched frame and warp into the BB canvas.
            foreach (var (lightInfo, transformOrig, name, _, _, _) in matched)
            {
                ct.ThrowIfCancellationRequested();
                stageSw.Restart();
                var lightRaw = await lightInfo.LoadFullAsync(ct);
                perfLoad += stageSw.Elapsed;
                stageSw.Restart();
                var calibrated = calibrator.Apply(lightRaw);
                perfCalibrate += stageSw.Elapsed;
                stageSw.Restart();
                var debayered = await calibrated.DebayerAsync(DebayerAlg, cancellationToken: ct);
                perfDebayer += stageSw.Elapsed;

                stageSw.Restart();
                var shifted = transformOrig * canvasShift;
                var warped = await debayered.WarpToReferenceGridAsync(shifted, outWidth, outHeight, ct);
                // Pre-compute normalisation stats while the warped image is in
                // RAM, then write it to a streaming-staging binary file. The
                // StagedAlignedFrame keeps only an open FileStream + a few
                // floats; the ~108 MB warped Image is eligible for GC after the
                // iteration ends.
                var stats = Normalizer.ComputeStats(warped);
                var stagingPath = Path.Combine(stagingDir, $"{name}.bin");
                StreamingFrameStaging.Write(warped, stagingPath);
                var reader = new StreamingFrameReader(stagingPath);
                aligned.Add(new StagedAlignedFrame(
                    reader,
                    warped.ImageMeta,
                    warped.MaxValue,
                    warped.Pedestal,
                    stats.PerChannelMin,
                    stats.PerChannelMedian));
                perfWarp += stageSw.Elapsed;
            }

            // Per-stage cost breakdown. Excludes reference frame (already detected
            // outside the loop) so per-frame averages are over the lights actually
            // matched against the reference.
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
            var attempted = aligned.Count + skipCount;
            Log($"  registered + warped {aligned.Count}/{attempted} frames " +
                $"(skipped {skipCount}) in {sw.ElapsedMilliseconds} ms");
            LogMatchSummary(matchStats, Log);

            if (aligned.Count < 2)
            {
                Log("  [skip] fewer than 2 aligned frames; integration would be meaningless");
                continue;
            }

            // 3c. Integrate via streaming integrator -- reads stripes from the
            // staged frame files, peak RAM bounded by stripe_h * w * c * n * 4.
            sw.Restart();
            var rejector = BuildRejector(aligned.Count);
            Log($"  rejector: {rejector?.GetType().Name ?? "<none>"}");
            var result = StreamingIntegrator.Integrate(aligned, new IntegrationOptions(Rejector: rejector));
            Log($"  integrated in {sw.ElapsedMilliseconds} ms");
            Log($"    frames: {result.FrameCount}, total rejections: {result.TotalRejections}, mean rate: {result.MeanRejectionRate:P2}");

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
            await PostProcessAndWriteAsync(result, masterPath, previewPath, searchHint, refImageDim, referenceRaw.ImageMeta, Log, ct);
            if (result.TotalRejections > 0)
            {
                Log($"  wrote {IntegrationFitsWriter.RejectionPathFor(masterPath)}");
            }

            // Release staging file handles + delete the per-group staging dir.
            // Keeping them around would balloon disk usage across groups.
            foreach (var staged in aligned) staged.Dispose();
            try { Directory.Delete(stagingDir, recursive: true); }
            catch (Exception ex) { Log($"  [warn] staging cleanup failed: {ex.Message}"); }
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
        Action<string> Log,
        System.Threading.CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var master = result.Master;

        // 1) Background neutralization. Compute pivot1 gains (per-channel
        //    multiplier so the sky bg sits neutral grey while highlights stay
        //    fixed). DO NOT mutate the master pixels -- apply via the stretch
        //    uniforms below, same as the live FITS viewer does. Mutating
        //    pixels breaks Linked-mode stretch because Linked uses ch0 (R)
        //    stats and after mutation R/G/B medians diverge: G/B drop below
        //    R's shadow and clamp to zero, producing the all-red render.
        (float R, float G, float B)? bgGains = null;
        if (master.ChannelCount >= 3)
        {
            var pedestals = new float[master.ChannelCount];
            var (perChannelBg, _) = master.ScanBackgroundRegion(pedestals);
            var gains = BackgroundNeutralization.ComputeGains(perChannelBg, BackgroundNeutralizationMethod.MinPivot);
            bgGains = gains;
            Log($"  [bgNeut/MinPivot] bg=({perChannelBg[0]:F4}, {perChannelBg[1]:F4}, {perChannelBg[2]:F4}) " +
                $"gains=({gains.R:F3}, {gains.G:F3}, {gains.B:F3})");
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

        // 3) SPCC white balance via Tycho-2 photometric matching + per-channel
        //    system throughput. Requires a solved WCS to pair detected stars
        //    with catalog entries. Returns WB multipliers; we DO NOT mutate
        //    the master -- the WB is applied via stretch uniforms (same as
        //    the viewer's PCC/SPCC button), keeping the master FITS as the
        //    raw integrated stack.
        (float R, float G, float B)? wbGains = null;
        if (solvedWcs is { } wcs && master.ChannelCount >= 3)
        {
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
                    var masterStars = await master.FindStarsAsync(channel: 0, snrMin: 5f, minStars: 50, maxRetries: 0, cancellationToken: ct);
                    var spcc = Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
                        master, masterStars, wcs, db, t.R, t.G, t.B);
                    if (spcc is { } wb)
                    {
                        wbGains = (wb.R, wb.G, wb.B);
                        Log($"  [SPCC] WB=({wb.R:F3}, {wb.G:F3}, {wb.B:F3}) from {wb.MatchCount} Tycho-2 matches");
                    }
                    else
                    {
                        Log("  [SPCC] insufficient matches; skipping WB");
                    }
                }
                else
                {
                    Log("  [SPCC] no channel throughput for this sensor; skipping WB");
                }
            }
            catch (Exception ex)
            {
                Log($"  [SPCC] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 4) Write the master FITS with the solved WCS baked into the headers
        //    (CRVAL/CRPIX/CD matrix etc., via IntegrationFitsWriter's wcs arg).
        //    Pixels are the raw integrated stack; bg-neut + WB are display ops.
        IntegrationFitsWriter.Write(masterPath, result, solvedWcs);
        Log($"  wrote {masterPath}{(solvedWcs is null ? "" : " (WCS embedded)")}");

        // 5) Display-encoded preview PNG. Stretch uniforms carry bg-neut +
        //    SPCC WB so they apply in the shader path (per-channel normalize
        //    -> bg neut -> WB -> shadow/MTF), keeping all channels aligned in
        //    Linked-mode stretch.
        try
        {
            RenderPreviewPng(master, previewPath, bgGains, wbGains);
            Log($"  wrote {previewPath}");
        }
        catch (Exception ex)
        {
            Log($"  [preview] failed: {ex.GetType().Name}: {ex.Message}");
        }

        Log($"  [post] total {sw.ElapsedMilliseconds} ms");
    }

    private static void RenderPreviewPng(
        Image image,
        string path,
        (float R, float G, float B)? bgGains,
        (float R, float G, float B)? wbGains)
    {
        var (channelCount, width, height) = image.Shape;
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }
        // Unlinked: each channel uses its own median/MAD for shadow + rescale.
        // Pairs naturally with bg-neut + WB applied in-shader since per-channel
        // clipping points adapt to each channel's own range. Factor/clipping
        // come from StretchParameters.Default = (0.1, -5).
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

        var rgba = new byte[width * height * 4];
        image.RenderStretchedRgba(uniforms, rgba);
        var png = PngWriter.Encode(rgba, width, height, IccProfiles.SRgbV4.Span);
        File.WriteAllBytes(path, png);
    }
}

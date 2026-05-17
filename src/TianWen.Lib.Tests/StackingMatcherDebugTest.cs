using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Focused diagnostic for the quad-invariant matcher. Loads the reference and a
/// handful of failed/matched lights from the real test dataset, runs the full
/// pipeline (calibrate -> debayer -> find stars -> build quads), then calls
/// <c>FindFitWithDiagnostics</c> at a ladder of tolerances so we can see exactly
/// which gate each pair falls through:
/// <list type="bullet">
///   <item>Gate 1: minimum quad count -- did either side build < 6 quads?</item>
///   <item>Gate 2: raw quad-pair count within tolerance -- < 6 means quads
///         don't fingerprint-match at all (centroid drift / different bright sets).</item>
///   <item>Gate 3: post-RANSAC inlier count -- must be >= <see cref="StarReferenceTable.RansacMinInliers"/>.
///         A failure here means RANSAC couldn't find a model with enough consistent
///         pairs beyond the 3-point sample triplet (which always self-fits).</item>
///   <item>Gate 4: affine fit scale-uniformity / skew validation. Failure here means
///         we got correspondences but the resulting transform isn't a rigid affine.</item>
/// </list>
/// Skipped when <c>C:\temp\stack</c> isn't present so CI stays green.
/// </summary>
public class StackingMatcherDebugTest(ITestOutputHelper output)
{
    private const string DataRoot = @"C:\temp\stack";
    private const string OutputDir = @"C:\temp\stack\output";
    private const string MastersDir = @"C:\temp\stack\output\masters";

    // Specific 60s-group frames hand-picked from the previous run's per-frame log.
    // The folder mixes TWO objects -- Skull and Crossbones Nebula (early frames,
    // 22:43-23:30 in this session) and Statue of Liberty Nebula (late frames,
    // 23:32-onwards). The reference _0252 at 04:40 is SoL. A frame imaged of a
    // different sky region can never quad-fingerprint match the reference, which
    // is a useful negative test case: NEG_CROSS_OBJECT.
    //
    //   Reference:          _0252 at 04:40:11 SoL (highest star count)
    //   Matched, qt=.008:   _0251 at 04:39:11 SoL, ~1 min before ref (clean)
    //   Matched, qt=.020:   _0234 at 04:19:57 SoL, ~20 min before, small dither
    //   Cross-object:       _0012 at 22:43:23 SCN, different target entirely
    //   Cross-object:       _0002 at 22:26:49 SCN, ~6h before ref + different target
    //   Meridian flip:      _0124 at 02:12:26 SoL, matched at qt=0.050 with rot=-180
    //   Post-flip skip:     _0016 at 00:08:14 SoL, didn't match at any runner tolerance
    //                       (hypothesis: real flip + extra offset, still SoL)
    //   Pre-flip skip:      _0030 at 00:24:30 SoL, surrounded by qt=0.020 matches in the
    //                       2026-05-15 runner but skipped every tolerance vs the chosen
    //                       reference _0233. Confirmed reproducer when REF=_0233: only 6
    //                       raw quad pairs at qt=0.020 -> RANSAC samples a 3-triplet that
    //                       always self-fits (n=3 affine is exact, residual zero), gate 4
    //                       catches the nonsense (scale 1.33/1.47, rot=176°). Versus
    //                       _0252 (this test's REF) the same frame matches cleanly at
    //                       qt=0.020 (14 raw / 11 filtered, tx=4048 rot=-179.96°). So
    //                       failure is REF-specific, not frame-defective. Mitigations to
    //                       consider: bump min-inlier threshold to >=4 (kill the n=3 self
    //                       fit), or fall back to next-best REF when ladder fails.
    private static readonly (string Label, string Filename)[] Frames =
    [
        ("REF              ", @"LIGHT\2026-02-15_04-18-56__-5.10_60.00s_0233.fits"),
        ("MATCH_NEAR       ", @"LIGHT\2026-02-15_04-19-57__-5.10_60.00s_0234.fits"),
        ("MATCH_DRIFT      ", @"LIGHT\2026-02-15_04-39-11__-5.00_60.00s_0251.fits"),
        ("NEG_CROSS_OBJECT1", @"LIGHT\2026-02-14_22-43-23__-5.00_60.00s_0012.fits"),
        ("NEG_CROSS_OBJECT2", @"LIGHT\2026-02-14_22-26-49__-5.10_60.00s_0002.fits"),
        ("MERIDIAN         ", @"LIGHT\2026-02-15_02-12-26__-5.00_60.00s_0124.fits"),
        ("POSTFLIP_SKIP    ", @"LIGHT\2026-02-15_00-08-14__-4.90_60.00s_0016.fits"),
        ("PREFLIP_SKIP_0030", @"LIGHT\2026-02-15_00-24-30__-5.10_60.00s_0030.fits"),
        // Two frames the 2026-05-17 SoL 60s run (REF=_0233) couldn't register at any
        // tolerance, both surrounded by clean post-flip matches:
        //   _0021 neighbours: _0020 qt=0.100 (tx=4056.1 rot=-179.915),
        //                     _0022 qt=0.100 (tx=4056.3 rot=-179.924)
        //   _0084 neighbours: _0083 qt=0.050 (tx=4058.5 rot=-179.982),
        //                     _0085 qt=0.050 (tx=4058.6 rot=-179.972)
        // Both reported plenty of stars/quads (6567/310 and 6884/304) so the failure
        // is gate-2/3/4 territory, not "too few quads".
        ("POSTFLIP_SKIP_0021", @"LIGHT\2026-02-15_00-14-19__-5.00_60.00s_0021.fits"),
        ("POSTFLIP_SKIP_0084", @"LIGHT\2026-02-15_01-26-17__-5.00_60.00s_0084.fits"),
    ];

    private static readonly float[] Tolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    /// <summary>Top-K brightest stars used for quad fingerprinting. Bright stars
    /// are reproducible across detection-threshold variation between frames, so
    /// top-K quad signatures stay stable group-wide where the all-stars kNN
    /// quads do not. See <c>StackingEndToEndManualTest.QuadStars</c> for the
    /// long-form rationale.</summary>
    private const int QuadStars = 500;

    [Fact]
    public async Task Diagnose_FailedMatches_AgainstReference()
    {
        if (!Directory.Exists(DataRoot))
        {
            Assert.Skip($"Test data folder {DataRoot} not present.");
        }
        var darkMasterPath = Path.Combine(MastersDir, "master_dark_60s_-5C_g120.fits");
        var flatMasterPath = Path.Combine(MastersDir, "master_flat_7.24s_-5C_None_g120.fits");
        if (!File.Exists(darkMasterPath) || !File.Exists(flatMasterPath))
        {
            Assert.Skip($"Run StackingEndToEndManualTest first to generate masters in {MastersDir}");
        }

        var ct = TestContext.Current.CancellationToken;

        // Load calibration masters built by the main runner.
        Image.TryReadFitsFile(darkMasterPath, out var darkLoad).ShouldBeTrue();
        Image.TryReadFitsFile(flatMasterPath, out var flatLoad).ShouldBeTrue();
        var dark = darkLoad!;
        var flat = flatLoad!;
        var calibrator = new Calibrator(Bias: null, Dark: dark, Flat: flat, Pedestal: 0f);

        // Process every frame: load -> calibrate -> debayer -> find stars ->
        // SortedStarList -> quads. Store the SortedStarList per frame so we
        // can pair it against the reference without re-detecting stars.
        var perFrame = new List<(string Label, string Path, int Stars, SortedStarList Sorted, StarQuadList Quads)>();
        foreach (var (label, rel) in Frames)
        {
            var fullPath = Path.Combine(DataRoot, rel);
            if (!File.Exists(fullPath))
            {
                output.WriteLine($"[{label}] MISSING: {fullPath}");
                continue;
            }

            var sw = Stopwatch.StartNew();
            Image.TryReadFitsFile(fullPath, out var rawLoad).ShouldBeTrue();
            var raw = rawLoad!;
            var objectName = raw.ImageMeta.ObjectName;
            var calibrated = calibrator.Apply(raw);
            var debayered = await calibrated.DebayerAsync(DebayerAlgorithm.VNG, cancellationToken: ct);
            // minStars=1000 forces the retry loop to keep lowering the
            // detection_level (30*noise -> 7*noise floor) until at least 1000
            // stars are detected. snrMin=5 then post-filters out the very
            // dimmest. Tests whether moderate-depth detection supplies enough
            // extra quads to register POSTFLIP_SKIP without paying the 3000
            // budget (which took ~1.5 s per frame).
            var stars = await debayered.FindStarsAsync(
                channel: 0,
                snrMin: 5f,
                minStars: 2000,
                cancellationToken: ct);
            var sorted = new SortedStarList(stars);
            var quads = await sorted.FindQuadsAsync(maxStars: QuadStars, ct);
            sw.Stop();

            output.WriteLine($"[{label}] {Path.GetFileName(fullPath)}  OBJECT='{objectName}'  stars={stars.Count}  quads={quads.Count}  ({sw.ElapsedMilliseconds} ms)");
            perFrame.Add((label, fullPath, stars.Count, sorted, quads));
        }

        // First entry is the reference; pair every other frame against it.
        if (perFrame.Count < 2) { Assert.Skip("Need at least one non-reference frame to diagnose."); }

        var refEntry = perFrame[0];
        output.WriteLine("");
        output.WriteLine($"REFERENCE: {Path.GetFileName(refEntry.Path)}  ({refEntry.Quads.Count} quads)");
        output.WriteLine("");
        output.WriteLine($"Per-pair diagnostics (gate 1 = quads<6; gate 2 = rawPairs<6; gate 3 = filteredPairs<{StarReferenceTable.RansacMinInliers}; gate 4 = affine rejected)");
        output.WriteLine("");

        // Track per-label whether any tolerance produced a passing MATCH so we can
        // assert at the end that cross-object frames are rejected at every tolerance.
        var labelPassed = new Dictionary<string, bool>();
        for (var i = 1; i < perFrame.Count; i++)
        {
            var entry = perFrame[i];
            labelPassed[entry.Label] = false;
            output.WriteLine($"=== {entry.Label} vs REF ({Path.GetFileName(entry.Path)}) ===");

            foreach (var tol in Tolerances)
            {
                // Match the runner's parameter order: this-frame = quads1 (dest),
                // reference = quads2 (source). The transform maps light -> ref so
                // WarpToReferenceGridAsync can inverse-sample into the ref grid.
                var (table, diag) = StarReferenceTable.FindFitWithDiagnostics(
                    quadStarDistances1: entry.Quads,       // dest (this frame)
                    quadStarDistances2: refEntry.Quads,    // source (reference)
                    minimumCount: 6,
                    quadTolerance: tol);

                string outcome;
                string transformSummary = "";
                if (table is null)
                {
                    if (diag.Quads1 < 6 || diag.Quads2 < 6) outcome = "GATE 1 (too few quads)";
                    else if (diag.RawPairs < 6) outcome = "GATE 2 (too few raw pairs)";
                    else if (diag.FilteredPairs < StarReferenceTable.RansacMinInliers) outcome = $"GATE 3 (post-outlier pairs={diag.FilteredPairs}, median ratio={diag.MedianRatio:F4})";
                    else outcome = "PASS but affine rejected (n/a here)";
                }
                else
                {
                    // Run the affine fit AND its decomposition manually so we can
                    // see exactly how off the scale/skew are even when the validator
                    // rejects (the validator only returns null otherwise).
                    var raw = table.FitAffineTransform();
                    if (raw is null)
                    {
                        outcome = "GATE 4 (LSQ affine fit returned null)";
                    }
                    else
                    {
                        var t = raw.Value;
                        var (scale, skew, _, _) = t.Decompose();
                        var tx = (float)Math.Sqrt(t.M31 * t.M31 + t.M32 * t.M32);
                        var rot = (float)(Math.Atan2(t.M12, t.M11) * 180.0 / Math.PI);
                        var scaleDelta = MathF.Abs(scale.X / scale.Y - 1f);
                        var maxSkew = MathF.Max(MathF.Abs(skew.X), MathF.Abs(skew.Y));
                        const float SolutionTolerance = 1e-3f;
                        var fitPasses = scale.X > 0f && scale.Y > 0f && scaleDelta <= SolutionTolerance && maxSkew <= SolutionTolerance;
                        outcome = fitPasses ? "MATCH" : "GATE 4 (validator rejected)";
                        transformSummary = $"  tx={tx,6:F1}px  rot={rot,7:F3}deg  " +
                            $"scale=({scale.X:F5},{scale.Y:F5}) dRatio={scaleDelta:E2} maxSkew={maxSkew:E2}";
                        if (fitPasses) labelPassed[entry.Label] = true;
                    }
                }

                output.WriteLine(
                    $"  qt={tol:F3}  rawPairs={diag.RawPairs,5}  filteredPairs={diag.FilteredPairs,5}  " +
                    $"medianRatio={(float.IsNaN(diag.MedianRatio) ? "n/a   " : diag.MedianRatio.ToString("F4"))}  " +
                    $"rms={(float.IsNaN(diag.RmsResidualPx) ? "  n/a" : diag.RmsResidualPx.ToString("F3") + "px")}  -> {outcome}{transformSummary}");
            }

            output.WriteLine("");
        }

        // Cross-object frames must never produce a passing match against the
        // reference: their stars look at a different patch of sky and any
        // affine that "fits" is the trivial 3-point self-fit of RANSAC's
        // sample, which the affine validator (Gate 4) rejects on scale
        // uniformity. The MATCH_* frames are the inverse: they MUST match.
        foreach (var (label, passed) in labelPassed)
        {
            var trimmed = label.Trim();
            if (trimmed.StartsWith("NEG_"))
            {
                passed.ShouldBeFalse($"{trimmed} (different object) must not register against the reference at any tolerance");
            }
            else if (trimmed.StartsWith("MATCH_") || trimmed == "MERIDIAN")
            {
                passed.ShouldBeTrue($"{trimmed} must register against the reference at at least one tolerance");
            }
        }
    }
}

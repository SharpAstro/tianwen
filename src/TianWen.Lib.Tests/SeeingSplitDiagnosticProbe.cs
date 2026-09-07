using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;
using static TianWen.Lib.Tests.DeconvolutionProbeMeasures;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where does a seeing split's width difference go? The store's per-sub widths for the Orion
/// 2025-10-15 night ran 1.71 to 2.60 px and the two thirds stacked from them measured 2.84 and 2.76,
/// the same within noise (E2.10a's third run). One estimator, whole frame, at every stage between a
/// sub and a master: the three sharpest and three softest subs as the analyzer sees them (a mono
/// rendition of the raw mosaic) and as a colour plane (AHD), the two thirds, the whole-session staged
/// master, and the retained drizzle master of the same night. Whichever stage the difference vanishes
/// at is the one E2.10 has to change.
/// </summary>
/// <remarks>Skipped unless <c>TIANWEN_E210_DIAG=1</c>, with <c>TIANWEN_E210_PAIR_DIR</c> (the pair's
/// output dir, holding the full master beside <c>sharp/</c> and <c>soft/</c>) and
/// <c>TIANWEN_PSF_STORE_DIR</c> (the store for the sub list and the retained master).</remarks>
public class SeeingSplitDiagnosticProbe(ITestOutputHelper output)
{
    private const string SessionKey = "Great-Orion-Nebula/2025-10-15";

    private async Task ReportAsync(string label, Image image, CancellationToken ct, bool fitGreen = false)
    {
        var (channels, width, height) = image.Shape;
        var cells = new List<string>();
        for (var c = 0; c < channels; c++)
        {
            var (fwhm, stars) = await MeasuredFwhmAsync(FullPlane(image, c), width, height, ct);
            cells.Add($"ch{c} {fwhm,5:F2} px / {stars,5} stars");
        }

        if (fitGreen)
        {
            // The bright-star profile fit, which a faint detection cannot inflate: the width the
            // estimator step would read off this frame.
            var green = Math.Min(1, channels - 1);
            var plane = FullPlane(image, green);
            var (fit, diag) = await FitStarProfileAsync(plane, width, height, PsfProfileFit.StarSelection.SignalFloor, 5f, 3000, ct);
            cells.Add(fit is { } f ? $"fit ch{green} {f.Fwhm:F2} px beta {f.MoffatBeta:F1}" : $"fit ch{green} {Describe(diag)}");

            // Brightness-matched: the median FWHM of the hundred BRIGHTEST detections by flux. A deep
            // stack admits stars a sub cannot, and every median over "all detections" (the estimator's
            // above, and the fit's MAD-relative floor) then reaches fainter, where the measured width
            // grows with noise; the hundred brightest are the same physical stars in a sub and its stack.
            var wrapped = Wrap(plane, width, height);
            try
            {
                var stars = await wrapped.FindStarsAsync(channel: 0, snrMin: 5f, cancellationToken: ct);
                var bright = stars.Where(s => s.StarFWHM > 0f).OrderByDescending(s => s.Flux).Take(100).Select(s => s.StarFWHM).ToList();
                cells.Add(bright.Count == 0 ? "top100 -" : $"top100 ch{green} {Median(bright):F2} px");
            }
            finally
            {
                wrapped.Release();
            }
        }

        output.WriteLine($"{label,-46} {width,5} x {height,-5} {string.Join("   ", cells)}");
    }

    /// <summary>
    /// The registration scatter measured directly: the frames a <c>--save-normalized</c> run left on the
    /// shared canvas (<c>exp-near6-norm/_staging/*/normalized/*.fits</c>), each detected on its green
    /// plane and matched star by star against the reference frame's detections. The median offset is
    /// what the transform left uncorrected, the spread about it is what a stack's average smears by;
    /// the width-versus-frame-count curve (two or three frames at the subs' width, six or more at 2.6
    /// to 2.8 px) says to expect about 0.7 px RMS.
    /// </summary>
    [Fact]
    public async Task ReportTheRegistrationScatterOfTheWarpedFrames()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        // TIANWEN_E210_NORM_EXP names the stage directory (exp-near6-norm by default; exp-near6-fixed
        // for the run after the refiner fix), so the same probe reads both.
        var exp = Environment.GetEnvironmentVariable("TIANWEN_E210_NORM_EXP") ?? "exp-near6-norm";
        var stagingDir = Path.Combine(pairDir!, exp, "_staging");
        Assert.SkipWhen(!Directory.Exists(stagingDir), $"no {exp}/_staging directory");
        var normDir = Directory.GetDirectories(stagingDir, "*")
            .Select(d => Path.Combine(d, "normalized")).FirstOrDefault(Directory.Exists);
        Assert.SkipWhen(normDir is null, $"no {exp}/_staging/*/normalized directory");
        var files = Directory.GetFiles(normDir!, "*.fits").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.SkipWhen(files.Length < 2, "fewer than two normalised frames");
        var ct = TestContext.Current.CancellationToken;

        // The reference is the frame the manifest names; its warp was the identity. A stage directory
        // that wrote its own manifest names it there; the original near6 run was driven by the one
        // beside the full master.
        var manifestPath = Directory.GetFiles(Path.Combine(pairDir!, exp), "master_*.manifest.json").FirstOrDefault()
            ?? Directory.GetFiles(pairDir!, "master_*-near6.manifest.json").FirstOrDefault();
        var referenceName = manifestPath is null ? "" : Path.GetFileNameWithoutExtension(
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement.GetProperty("ReferencePath").GetString() ?? "");

        var lists = new List<(string Name, StarList Stars, float Fwhm, int Count)>();
        foreach (var file in files)
        {
            if (!Image.TryReadFitsFile(file, out var frame) || frame is null)
            {
                output.WriteLine($"{Path.GetFileName(file),-60} (unreadable)");
                continue;
            }

            try
            {
                var (channels, width, height) = frame.Shape;
                var plane = FullPlane(frame, Math.Min(1, channels - 1));
                var (fwhm, count) = await MeasuredFwhmAsync(plane, width, height, ct);
                var wrapped = Wrap(plane, width, height);
                try
                {
                    lists.Add((Path.GetFileName(file), await wrapped.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct), fwhm, count));
                }
                finally
                {
                    wrapped.Release();
                }
            }
            finally
            {
                frame.Release();
            }
        }

        var anchorIndex = lists.FindIndex(l => l.Name.Contains(referenceName, StringComparison.OrdinalIgnoreCase));
        if (anchorIndex < 0) anchorIndex = 0;
        var anchor = lists[anchorIndex];
        output.WriteLine($"normalised frames under {normDir}; anchor {anchor.Name} ({anchor.Stars.Count} stars at snr 20); green plane; widths are the estimator's median at snr 5");
        output.WriteLine($"{"frame",-60} {"fwhm",5} {"stars",5} {"matched",7} {"dx med",7} {"dy med",7} {"rms",6} {"p90",6}");
        foreach (var (name, stars, fwhm, count) in lists)
        {
            var dx = new List<double>();
            var dy = new List<double>();
            foreach (var a in anchor.Stars)
            {
                ImagedStar? best = null;
                var bestD2 = 9.0;
                foreach (var s in stars)
                {
                    var ddx = s.XCentroid - a.XCentroid;
                    var ddy = s.YCentroid - a.YCentroid;
                    var d2 = (ddx * ddx) + (ddy * ddy);
                    if (d2 < bestD2) { bestD2 = d2; best = s; }
                }

                if (best is { } b)
                {
                    dx.Add(b.XCentroid - a.XCentroid);
                    dy.Add(b.YCentroid - a.YCentroid);
                }
            }

            if (dx.Count == 0)
            {
                output.WriteLine($"{name,-60} {fwhm,5:F2} {count,5} {0,7}");
                continue;
            }

            var mx = Median(new List<double>(dx));
            var my = Median(new List<double>(dy));
            var residual = dx.Zip(dy, (x, y) => Math.Sqrt(((x - mx) * (x - mx)) + ((y - my) * (y - my)))).OrderBy(v => v).ToList();
            var rms = Math.Sqrt(residual.Sum(v => v * v) / residual.Count);
            output.WriteLine($"{name,-60} {fwhm,5:F2} {count,5} {dx.Count,7} {mx,7:F2} {my,7:F2} {rms,6:F2} {Percentile(residual, 0.9),6:F2}");
        }

        output.WriteLine("");
        output.WriteLine("A median offset is a transform error (the whole frame sits off the reference); the rms about it is the");
        output.WriteLine("centroid scatter a stack averages over. The anchor's own row is the detector's repeatability floor.");
    }

    /// <summary>
    /// Where the registration loses half of a small shift. The warped near6 frames sit off the reference
    /// by minus their manifest translation for every frame under about 2 px of drift and on it for the
    /// one frame 6 px away (the scatter probe above; a phase correlation of the raw subs puts the true
    /// drift at twice the manifest's). The suspect is the rigid refiner's nearest-neighbour pairing: a
    /// detection fixed to the sensor (a residual warm pixel, the group's dark being 17 degrees colder
    /// than its lights) sits at the same position in both frames, pairs with itself inside the 5 px
    /// tolerance, and the Procrustes fit averages the two populations. Every stage is printed for each
    /// near6 frame against the reference, then the refinement is re-run with the unmoved detections
    /// removed.
    /// </summary>
    [Fact]
    public async Task ReportWhereTheRegistrationLosesHalfTheShift()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        var manifestPath = Directory.GetFiles(pairDir!, "master_*-near6.manifest.json").FirstOrDefault();
        Assert.SkipWhen(manifestPath is null, "no near6 manifest");
        var ct = TestContext.Current.CancellationToken;

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var referencePath = manifest.RootElement.GetProperty("ReferencePath").GetString() ?? "";
        var frames = manifest.RootElement.GetProperty("Frames").EnumerateArray()
            .Select(f => (Path: f.GetProperty("Path").GetString() ?? "",
                          T: f.GetProperty("StarTransform").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()))
            .Where(f => f.T.Length == 6)
            .ToList();
        Assert.SkipWhen(frames.Count < 2 || !frames.Any(f => f.Path.Equals(referencePath, StringComparison.OrdinalIgnoreCase)), "manifest lacks the reference");

        // The group's own calibration, as the pipeline chose it: the -5C dark under 12C lights and the
        // L-QuadEnhance flat, no bias once a dark matched. Calibrator.Apply consumes the light.
        var mastersDir = Path.Combine(pairDir!, "masters");
        var dark = LoadMaster(Path.Combine(mastersDir, "master_dark_120s_-5C_g120.fits"));
        var flat = LoadMaster(Path.Combine(mastersDir, "master_flat_7s_10C_OptolongL-QuadEnhance_g120_ps.fits"));
        var calibrator = new Calibrator(Dark: dark, Flat: flat);
        output.WriteLine($"calibration: dark {(dark is null ? "none" : "120s -5C g120")}, flat {(flat is null ? "none" : "7s 10C L-QuadEnhance g120")}; detection snr 5, min stars 2000 (the pipeline's)");

        var refStars = await DetectAsync(referencePath, calibrator, ct);
        using var refSorted = new SortedStarList(refStars);
        output.WriteLine($"reference {Path.GetFileName(referencePath)}: {refStars.Count} detections");
        output.WriteLine($"{"frame",-44} {"manifest t",15} {"stars",5} | {"raw pairs",9} {"unmoved",7} {"moved at",22} | {"bulk t",15} {"refined t",15} {"pairs",5} {"rms",5} | {"unmoved removed",15} {"pairs",5} {"rms",5}");
        foreach (var (path, t) in frames)
        {
            if (path.Equals(referencePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stars = await DetectAsync(path, calibrator, ct);
            using var lightSorted = new SortedStarList(stars);

            // (a) Nearest-neighbour pairing with NO transform inside the refiner's 5 px: the residuals
            // split into an unmoved population (a detection at the same sensor position in both frames)
            // and a moved one at the true drift.
            var residuals = NearestResiduals(lightSorted, refSorted, Matrix3x2.Identity, 5f);
            var unmoved = residuals.Count(r => r.LengthSquared() < 0.6f * 0.6f);
            var (modeX, modeY, modeCount) = Mode(residuals.Where(r => r.LengthSquared() >= 0.6f * 0.6f), 0.5f);

            // (b) the bulk quad solution and (c) the rigid refinement, as the pipeline applies them.
            var (solution, _, _) = await FrameRegistration.TryMatchAsync(lightSorted, refSorted, FrameRegistration.DefaultQuadStars);
            var bulk = "no fit";
            var refinedText = "";
            var withoutUnmovedText = "";
            var pairs = 0;
            var rms = float.NaN;
            var pairs2 = 0;
            var rms2 = float.NaN;
            if (solution is { } s)
            {
                bulk = $"({s.M31,6:F2},{s.M32,6:F2})";
                var (refined, _, _, _, _, rmsPx, matched, _) = RegistrationRefiner.RefineRigid(lightSorted, refSorted, s);
                refinedText = $"({refined.M31,6:F2},{refined.M32,6:F2})";
                pairs = matched;
                rms = rmsPx;

                // (d) the same refinement once every detection with a counterpart within 0.6 px of the
                // SAME raw position is dropped from both lists. Valid here because every near6 drift is
                // over 1 px; a frame that has not moved cannot be separated this way, which is the
                // design problem a fix has to solve.
                using var lightMoving = new SortedStarList(WithoutUnmoved(stars, refStars, 0.6f));
                using var refMoving = new SortedStarList(WithoutUnmoved(refStars, stars, 0.6f));
                var (refined2, _, _, _, _, rmsPx2, matched2, _) = RegistrationRefiner.RefineRigid(lightMoving, refMoving, s);
                withoutUnmovedText = $"({refined2.M31,6:F2},{refined2.M32,6:F2})";
                pairs2 = matched2;
                rms2 = rmsPx2;
            }

            var name = Path.GetFileName(path);
            output.WriteLine($"{name[..Math.Min(44, name.Length)],-44} ({t[4],6:F2},{t[5],6:F2}) {stars.Count,5} | {residuals.Count,9} {unmoved,7} {modeCount,5}@({modeX,6:F2},{modeY,6:F2}) | {bulk,15} {refinedText,15} {pairs,5} {rms,5:F2} | {withoutUnmovedText,15} {pairs2,5} {rms2,5:F2}");
        }

        output.WriteLine("");
        output.WriteLine("raw pairs: light detections with a reference detection within 5 px, no transform applied; unmoved: those within");
        output.WriteLine("0.6 px of their own position; moved at: the modal residual of the rest in 0.5 px bins, the true drift as the");
        output.WriteLine("detector sees it. A refined t near half the modal drift, and near the whole of it once the unmoved detections");
        output.WriteLine("are removed, is the refiner averaging two populations. The manifest t is what the stack applied.");
    }

    private static Image? LoadMaster(string path)
        => File.Exists(path) && Image.TryReadFitsFile(path, out var image) ? image : null;

    private static async Task<StarList> DetectAsync(string path, Calibrator calibrator, CancellationToken ct)
    {
        Image.TryReadFitsFile(path, out var raw).ShouldBeTrue(path);
        var calibrated = calibrator.Apply(raw!);
        try
        {
            var (stars, debayered) = await FrameRegistration.DetectAsync(calibrated, DebayerAlgorithm.VNG, 5f, 2000, ct);
            debayered.Release();
            return stars;
        }
        finally
        {
            calibrated.Release();
        }
    }

    private static List<Vector2> NearestResiduals(SortedStarList light, SortedStarList reference, Matrix3x2 transform, float tolerancePx)
    {
        var refX = reference.Select(s => s.XCentroid).ToArray();
        var refY = reference.Select(s => s.YCentroid).ToArray();
        var tolSq = tolerancePx * tolerancePx;
        var result = new List<Vector2>();
        foreach (var ls in light)
        {
            var p = Vector2.Transform(new Vector2(ls.XCentroid, ls.YCentroid), transform);
            var bestSq = float.MaxValue;
            var best = Vector2.Zero;
            for (var j = 0; j < refX.Length; j++)
            {
                var d = new Vector2(refX[j] - p.X, refY[j] - p.Y);
                var sq = d.LengthSquared();
                if (sq < bestSq && sq <= tolSq)
                {
                    bestSq = sq;
                    best = d;
                }
            }

            if (bestSq < float.MaxValue)
            {
                result.Add(best);
            }
        }

        return result;
    }

    private static (float X, float Y, int Count) Mode(IEnumerable<Vector2> residuals, float binPx)
    {
        var bins = new Dictionary<(int, int), (double SumX, double SumY, int N)>();
        foreach (var r in residuals)
        {
            var key = ((int)MathF.Floor(r.X / binPx), (int)MathF.Floor(r.Y / binPx));
            bins[key] = bins.TryGetValue(key, out var b) ? (b.SumX + r.X, b.SumY + r.Y, b.N + 1) : (r.X, r.Y, 1);
        }

        if (bins.Count == 0)
        {
            return (float.NaN, float.NaN, 0);
        }

        var top = bins.Values.MaxBy(b => b.N);
        return ((float)(top.SumX / top.N), (float)(top.SumY / top.N), top.N);
    }

    /// <summary>The detections of <paramref name="list"/> with no counterpart in <paramref name="other"/>
    /// within <paramref name="tolerancePx"/> of the same raw position: what moved with the sky.</summary>
    private static StarList WithoutUnmoved(StarList list, StarList other, float tolerancePx)
    {
        var otherX = other.Select(s => s.XCentroid).ToArray();
        var otherY = other.Select(s => s.YCentroid).ToArray();
        var tolSq = tolerancePx * tolerancePx;
        var kept = new ConcurrentBag<ImagedStar>();
        foreach (var s in list)
        {
            var unmoved = false;
            for (var j = 0; j < otherX.Length && !unmoved; j++)
            {
                var dx = otherX[j] - s.XCentroid;
                var dy = otherY[j] - s.YCentroid;
                unmoved = (dx * dx) + (dy * dy) <= tolSq;
            }

            if (!unmoved)
            {
                kept.Add(s);
            }
        }

        return new StarList(kept);
    }

    [Fact]
    public async Task ReportTheWidthAtEveryStageBetweenASubAndAMaster()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        var storeDir = Environment.GetEnvironmentVariable("TIANWEN_PSF_STORE_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir) || string.IsNullOrWhiteSpace(storeDir), "TIANWEN_E210_PAIR_DIR and TIANWEN_PSF_STORE_DIR are both needed");
        var ct = TestContext.Current.CancellationToken;

        var store = await DatasetPsfStore.ReadAsync(Path.Combine(storeDir!, "stats", DatasetPsfStore.FileName), cancellationToken: ct);
        var record = store.Values.FirstOrDefault(r => r.SessionId.Contains(SessionKey, StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(record?.SubFile is null, $"no record with sub identity for {SessionKey}");
        var ranked = record!.SubFile!.Zip(record.SubFwhm, (f, w) => (File: f, Fwhm: w)).OrderBy(t => t.Fwhm).ToArray();
        var picks = ranked.Take(3).Concat(ranked.TakeLast(3)).ToArray();

        output.WriteLine($"session   {record.SessionId}; store widths {ranked[0].Fwhm:F2} to {ranked[^1].Fwhm:F2} px over {ranked.Length} subs (the analyzer's, mono mosaic)");
        output.WriteLine("estimator HfdPsfEstimator median FWHM over every detection at snr 5, each plane normalised to a peak of 1 first; whole frame");
        output.WriteLine("");

        foreach (var (file, storeFwhm) in picks)
        {
            if (!Image.TryReadFitsFile(file, out var raw) || raw is null)
            {
                output.WriteLine($"{Path.GetFileName(file),-44} (unreadable)");
                continue;
            }

            try
            {
                var name = Path.GetFileName(file)[..Math.Min(34, Path.GetFileName(file).Length)];
                var mono = await raw.DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: ct);
                try
                {
                    await ReportAsync($"sub {name} store {storeFwhm:F2} mono", mono, ct);
                }
                finally
                {
                    if (!ReferenceEquals(mono, raw)) mono.Release();
                }

                foreach (var algorithm in new[] { DebayerAlgorithm.AHD, DebayerAlgorithm.VNG })
                {
                    var colour = await raw.DebayerAsync(algorithm, cancellationToken: ct);
                    try
                    {
                        await ReportAsync($"sub {name} store {storeFwhm:F2} {algorithm}", colour, ct, fitGreen: true);
                    }
                    finally
                    {
                        if (!ReferenceEquals(colour, raw)) colour.Release();
                    }
                }
            }
            finally
            {
                raw.Release();
            }
        }

        output.WriteLine("");
        var masters = new List<(string Label, string? Path)>
        {
            ("third: sharp (Float16Staged, 20)", Directory.GetFiles(Path.Combine(pairDir!, "sharp"), "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("third: soft (Float16Staged, 21)", Directory.GetFiles(Path.Combine(pairDir!, "soft"), "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("whole night (Float16Staged, 71)", Directory.GetFiles(pairDir!, "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("retained master (BayerDrizzle, 60)", Directory.GetFiles(Path.Combine(storeDir!, "session-masters"), "*.fits").FirstOrDefault(p => Path.GetFileName(p).Contains("Great-Orion-Nebula_2025-10-15", StringComparison.OrdinalIgnoreCase))),
        };
        // Stage stacks (exp-<label>/): the reference alone, the reference plus one and plus two sharp
        // frames, so the blur a stack adds over its subs can be placed at calibration + debayer, at
        // the bilinear warp, or at the averaging of frames whose PSF differs.
        foreach (var dir in Directory.GetDirectories(pairDir!, "exp-*").OrderBy(d => d, StringComparer.Ordinal))
        {
            masters.Add(($"stage: {Path.GetFileName(dir)} (Float16Staged)",
                Directory.GetFiles(dir, "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))));
        }
        foreach (var (label, path) in masters)
        {
            if (path is null || !Image.TryReadFitsFile(path, out var master) || master is null)
            {
                output.WriteLine($"{label,-44} (missing or unreadable)");
                continue;
            }

            try
            {
                await ReportAsync(label, master, ct, fitGreen: true);
            }
            finally
            {
                master.Release();
            }
        }
    }
}

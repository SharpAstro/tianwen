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

        var (refStars, refCalibrated) = await DetectAsync(referencePath, calibrator, ct);
        refCalibrated.Release();
        using var refSorted = new SortedStarList(refStars);
        output.WriteLine($"reference {Path.GetFileName(referencePath)}: {refStars.Count} detections");
        output.WriteLine($"{"frame",-44} {"manifest t",15} {"stars",5} | {"raw pairs",9} {"unmoved",7} {"moved at",22} | {"bulk t",15} {"refined t",15} {"pairs",5} {"rms",5} | {"unmoved removed",15} {"pairs",5} {"rms",5}");
        foreach (var (path, t) in frames)
        {
            if (path.Equals(referencePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (stars, calibrated) = await DetectAsync(path, calibrator, ct);
            calibrated.Release();
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

    /// <summary>
    /// The signature that would let the detector refuse the unmoved detections at the source. For each
    /// near6 frame against the reference, every detection is classed by the reference pairing (unmoved:
    /// a reference detection within 0.35 px of the same raw position while the bulk affine moved it by
    /// more than 0.7; moved: paired within 1 px after the bulk affine) and measured on the CALIBRATED
    /// MOSAIC as the fraction of its background-subtracted 3 by 3 flux that the peak photosite carries.
    /// The detector's own HFD, FWHM and SNR are printed beside it, to see whether any of them separates
    /// the two populations already. Pre-registered in the plan (E2.10a, "the third finding placed").
    /// </summary>
    [Fact]
    public async Task ReportTheSinglePhotositeSignatureOfTheUnmovedDetections()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        var manifestPath = Directory.GetFiles(pairDir!, "master_*-near6.manifest.json").FirstOrDefault();
        Assert.SkipWhen(manifestPath is null, "no near6 manifest");
        var ct = TestContext.Current.CancellationToken;

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var referencePath = manifest.RootElement.GetProperty("ReferencePath").GetString() ?? "";
        var paths = manifest.RootElement.GetProperty("Frames").EnumerateArray()
            .Select(f => f.GetProperty("Path").GetString() ?? "")
            .Where(p => p.Length > 0)
            .ToList();
        Assert.SkipWhen(!paths.Any(p => p.Equals(referencePath, StringComparison.OrdinalIgnoreCase)), "manifest lacks the reference");

        var mastersDir = Path.Combine(pairDir!, "masters");
        var calibrator = new Calibrator(
            Dark: LoadMaster(Path.Combine(mastersDir, "master_dark_120s_-5C_g120.fits")),
            Flat: LoadMaster(Path.Combine(mastersDir, "master_flat_7s_10C_OptolongL-QuadEnhance_g120_ps.fits")));

        var (refStars, refCalibrated) = await DetectAsync(referencePath, calibrator, ct);
        refCalibrated.Release();
        using var refSorted = new SortedStarList(refStars);
        var refX = refSorted.Select(s => s.XCentroid).ToArray();
        var refY = refSorted.Select(s => s.YCentroid).ToArray();
        output.WriteLine($"reference {Path.GetFileName(referencePath)}: {refStars.Count} detections; peak fraction = (peak photosite - background) / sum of the positive background-subtracted 3x3 about it, on the calibrated mosaic");
        output.WriteLine($"{"frame",-44} {"unmoved",7} {"moved",5} | {"peak fraction p10/p50/p90, unmoved",34} {"moved",20} | {"share over 0.85 / 0.70 / 0.60: unmoved",40} {"moved",20} | {"hfd p50 u/m",12} {"fwhm p50 u/m",13} {"snr p50 u/m",12}");
        foreach (var path in paths)
        {
            if (path.Equals(referencePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (stars, calibrated) = await DetectAsync(path, calibrator, ct);
            try
            {
                var (_, width, height) = calibrated.Shape;
                var plane = FullPlane(calibrated, 0);
                using var lightSorted = new SortedStarList(stars);
                var (solution, _, _) = await FrameRegistration.TryMatchAsync(lightSorted, refSorted, FrameRegistration.DefaultQuadStars);
                var name = Path.GetFileName(path);
                if (solution is not { } bulk)
                {
                    output.WriteLine($"{name[..Math.Min(44, name.Length)],-44} no quad fit");
                    continue;
                }

                var unmoved = new List<(float Pf, ImagedStar S)>();
                var moved = new List<(float Pf, ImagedStar S)>();
                foreach (var s in stars)
                {
                    var raw = new Vector2(s.XCentroid, s.YCentroid);
                    var predicted = Vector2.Transform(raw, bulk);
                    var pf = PeakPhotositeFraction(plane, width, height, s.XCentroid, s.YCentroid);
                    if (float.IsNaN(pf))
                    {
                        continue;
                    }

                    if (NearestDistance(refX, refY, raw) <= RegistrationRefiner.UnmovedTolerancePx
                        && Vector2.Distance(predicted, raw) > 2f * RegistrationRefiner.UnmovedTolerancePx)
                    {
                        unmoved.Add((pf, s));
                    }
                    else if (NearestDistance(refX, refY, predicted) <= 1.0f)
                    {
                        moved.Add((pf, s));
                    }
                }

                static string Percentiles(List<(float Pf, ImagedStar S)> set)
                {
                    if (set.Count == 0)
                    {
                        return "n/a";
                    }

                    var v = set.Select(t => (double)t.Pf).OrderBy(x => x).ToList();
                    return $"{Percentile(v, 0.1):F2}/{Percentile(v, 0.5):F2}/{Percentile(v, 0.9):F2}";
                }

                static string Shares(List<(float Pf, ImagedStar S)> set)
                    => set.Count == 0 ? "n/a" : $"{set.Count(t => t.Pf > 0.85f) / (float)set.Count:F2} / {set.Count(t => t.Pf > 0.70f) / (float)set.Count:F2} / {set.Count(t => t.Pf > 0.60f) / (float)set.Count:F2}";

                static double P50(List<(float Pf, ImagedStar S)> set, Func<ImagedStar, float> pick)
                    => set.Count == 0 ? double.NaN : Percentile(set.Select(t => (double)pick(t.S)).OrderBy(x => x).ToList(), 0.5);

                output.WriteLine($"{name[..Math.Min(44, name.Length)],-44} {unmoved.Count,7} {moved.Count,5} | {Percentiles(unmoved),34} {Percentiles(moved),20} | {Shares(unmoved),40} {Shares(moved),20} | {P50(unmoved, s => s.HFD),5:F2}/{P50(moved, s => s.HFD),5:F2} {P50(unmoved, s => s.StarFWHM),6:F2}/{P50(moved, s => s.StarFWHM),5:F2} {P50(unmoved, s => s.SNR),5:F0}/{P50(moved, s => s.SNR),5:F0}");
            }
            finally
            {
                calibrated.Release();
            }
        }
    }

    /// <summary>
    /// The six near6 subs' own widths (calibrated as the pipeline calibrates them, AHD, green fit and
    /// estimator), so the near6 masters' 2.65 and 2.47 px can be read against what went in rather than
    /// against the night's sharpest and softest frames.
    /// </summary>
    [Fact]
    public async Task ReportTheNear6SubsOwnWidths()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        var manifestPath = Directory.GetFiles(pairDir!, "master_*-near6.manifest.json").FirstOrDefault();
        Assert.SkipWhen(manifestPath is null, "no near6 manifest");
        var ct = TestContext.Current.CancellationToken;

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var paths = manifest.RootElement.GetProperty("Frames").EnumerateArray()
            .Select(f => f.GetProperty("Path").GetString() ?? "")
            .Where(p => p.Length > 0)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        var mastersDir = Path.Combine(pairDir!, "masters");
        var calibrator = new Calibrator(
            Dark: LoadMaster(Path.Combine(mastersDir, "master_dark_120s_-5C_g120.fits")),
            Flat: LoadMaster(Path.Combine(mastersDir, "master_flat_7s_10C_OptolongL-QuadEnhance_g120_ps.fits")));

        output.WriteLine("near6 subs, calibrated as the pipeline calibrates them, AHD; estimator median at snr 5, green fit with the signal floor, top-100 brightest");
        foreach (var path in paths)
        {
            Image.TryReadFitsFile(path, out var raw).ShouldBeTrue(path);
            var calibrated = calibrator.Apply(raw!);
            try
            {
                var debayered = await calibrated.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
                try
                {
                    await ReportAsync(Path.GetFileName(path)[..Math.Min(46, Path.GetFileName(path).Length)], debayered, ct, fitGreen: true);
                }
                finally
                {
                    debayered.Release();
                }
            }
            finally
            {
                calibrated.Release();
            }
        }
    }

    /// <summary>
    /// The same stars in a warped frame and in the master built from it, width against width. A fit or
    /// a median over "all detections" reaches fainter on a deeper image, so a stack read that way can
    /// look wider than its inputs without being so; pairing each star with itself removes the selection.
    /// For every normalised frame of the stage directory (<c>TIANWEN_E210_NORM_EXP</c>): its green-plane
    /// detections at snr 20 matched within 1 px to the master's, kept where the frame's star is
    /// unsaturated and well above noise (SNR 30 to 300), and the median of the master's FWHM over the
    /// frame's on those stars. The reference frame was shifted by an integer and never interpolated, so
    /// its row is the stack's own cost; the others carry their bilinear resampling too.
    /// </summary>
    [Fact]
    public async Task ReportPerStarWidthsOfTheWarpedFramesAgainstTheirMaster()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        var exp = Environment.GetEnvironmentVariable("TIANWEN_E210_NORM_EXP") ?? "exp-near6-fixed";
        var expDir = Path.Combine(pairDir!, exp);
        var masterPath = Directory.Exists(expDir)
            ? Directory.GetFiles(expDir, "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop", StringComparison.OrdinalIgnoreCase) && !p.Contains("rejection", StringComparison.OrdinalIgnoreCase))
            : null;
        Assert.SkipWhen(masterPath is null, $"no full-canvas master under {exp}");
        var normDir = Directory.Exists(Path.Combine(expDir, "_staging"))
            ? Directory.GetDirectories(Path.Combine(expDir, "_staging"), "*").Select(d => Path.Combine(d, "normalized")).FirstOrDefault(Directory.Exists)
            : null;
        Assert.SkipWhen(normDir is null, $"no {exp}/_staging/*/normalized directory");
        var ct = TestContext.Current.CancellationToken;

        Image.TryReadFitsFile(masterPath!, out var master).ShouldBeTrue(masterPath);
        List<ImagedStar> masterStars;
        try
        {
            var (channels, width, height) = master!.Shape;
            var wrapped = Wrap(FullPlane(master, Math.Min(1, channels - 1)), width, height);
            try
            {
                masterStars = (await wrapped.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct)).Where(s => s.StarFWHM > 0f).ToList();
            }
            finally
            {
                wrapped.Release();
            }
        }
        finally
        {
            master.Release();
        }

        var masterX = masterStars.Select(s => s.XCentroid).ToArray();
        var masterY = masterStars.Select(s => s.YCentroid).ToArray();
        output.WriteLine($"{exp}: master {Path.GetFileName(masterPath)} with {masterStars.Count} green detections at snr 20; pairs are the frame's stars with SNR 30 to 300 matched within 1 px");
        output.WriteLine($"{"frame",-60} {"pairs",5} {"frame fwhm",10} {"master fwhm",11} {"master/frame p25/p50/p75",24} {"quadrature add",14}");
        foreach (var file in Directory.GetFiles(normDir!, "*.fits").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!Image.TryReadFitsFile(file, out var frame) || frame is null)
            {
                continue;
            }

            List<(float Frame, float Master)> pairs = [];
            try
            {
                var (channels, width, height) = frame.Shape;
                var wrapped = Wrap(FullPlane(frame, Math.Min(1, channels - 1)), width, height);
                try
                {
                    foreach (var s in await wrapped.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct))
                    {
                        if (s.StarFWHM <= 0f || s.SNR < 30f || s.SNR > 300f)
                        {
                            continue;
                        }

                        var bestSq = 1f;
                        var best = -1;
                        for (var j = 0; j < masterX.Length; j++)
                        {
                            var dx = masterX[j] - s.XCentroid;
                            var dy = masterY[j] - s.YCentroid;
                            var sq = (dx * dx) + (dy * dy);
                            if (sq < bestSq)
                            {
                                bestSq = sq;
                                best = j;
                            }
                        }

                        if (best >= 0)
                        {
                            pairs.Add((s.StarFWHM, masterStars[best].StarFWHM));
                        }
                    }
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

            var name = Path.GetFileName(file);
            if (pairs.Count < 20)
            {
                output.WriteLine($"{name[..Math.Min(60, name.Length)],-60} {pairs.Count,5} (too few pairs)");
                continue;
            }

            var frameMedian = Median(pairs.Select(p => (double)p.Frame).ToList());
            var masterMedian = Median(pairs.Select(p => (double)p.Master).ToList());
            var ratios = pairs.Select(p => (double)p.Master / p.Frame).OrderBy(r => r).ToList();
            var add = masterMedian > frameMedian ? Math.Sqrt((masterMedian * masterMedian) - (frameMedian * frameMedian)) : 0.0;
            output.WriteLine($"{name[..Math.Min(60, name.Length)],-60} {pairs.Count,5} {frameMedian,10:F2} {masterMedian,11:F2} {Percentile(ratios, 0.25),8:F3}/{Percentile(ratios, 0.5),5:F3}/{Percentile(ratios, 0.75),5:F3}   {add,14:F2}");
        }
    }

    /// <summary>
    /// R1's ringing read: for each stage master named in <c>TIANWEN_E210_RING</c> (comma-separated exp
    /// directories; the bilinear and Lanczos twins by default), the deepest undershoot below the local
    /// background in an annulus about each star, in MAD units, and the share of stars past one MAD, on a
    /// 1024 px square from the canvas centre of the green plane, beside the star count and width there.
    /// A sinc kernel rings; the pre-registration allows under 10 percent of a star's peak and kills at 20,
    /// and the undershoot in MADs is the noise-referred form the oracle probes already use.
    /// </summary>
    [Fact]
    public async Task ReportRingingOfTheStageMasters()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir), "TIANWEN_E210_PAIR_DIR not set");
        var exps = (Environment.GetEnvironmentVariable("TIANWEN_E210_RING") ?? "exp-near6-fixed,exp-near6-lanczos,exp-full-norm,exp-full-lanczos")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ct = TestContext.Current.CancellationToken;
        const int side = 1024;

        output.WriteLine($"{"stage",-22} {"canvas",13} {"stars",5} {"fwhm",5} {"bg mad",8} {"undershoot p50 (MADs)",21} {"share past 1 MAD",16} {"peak p50",9} {"undershoot / peak",17}");
        foreach (var exp in exps)
        {
            var expDir = Path.Combine(pairDir!, exp);
            var masterPath = Directory.Exists(expDir)
                ? Directory.GetFiles(expDir, "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop", StringComparison.OrdinalIgnoreCase) && !p.Contains("rejection", StringComparison.OrdinalIgnoreCase))
                : null;
            if (masterPath is null || !Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                output.WriteLine($"{exp,-22} (no master)");
                continue;
            }

            try
            {
                var (channels, width, height) = master.Shape;
                if (width < side || height < side)
                {
                    output.WriteLine($"{exp,-22} {width,5} x {height,-5} smaller than the {side} px square");
                    continue;
                }

                var crop = CropCentre(master, Math.Min(1, channels - 1), side);
                var (bg, mad) = BackgroundStats(crop);
                var wrapped = Wrap(crop, side, side);
                try
                {
                    var stars = (await wrapped.FindStarsAsync(channel: 0, snrMin: 20f, cancellationToken: ct)).Where(s => s.StarFWHM > 0f).ToList();
                    var (undershoot, share) = Ringing(crop, side, stars, bg, mad);
                    var fwhm = stars.Count == 0 ? double.NaN : Median(stars.Select(s => (double)s.StarFWHM).ToList());
                    // The star's peak above background in the same units, for the undershoot as a share of it.
                    var peaks = new List<double>();
                    foreach (var s in stars)
                    {
                        var cx = (int)MathF.Round(s.XCentroid);
                        var cy = (int)MathF.Round(s.YCentroid);
                        if (cx >= 1 && cy >= 1 && cx < side - 1 && cy < side - 1)
                        {
                            var peak = float.MinValue;
                            for (var dy = -1; dy <= 1; dy++)
                            {
                                for (var dx = -1; dx <= 1; dx++)
                                {
                                    peak = MathF.Max(peak, crop[((cy + dy) * side) + cx + dx]);
                                }
                            }

                            peaks.Add(peak - bg);
                        }
                    }

                    var peakMad = peaks.Count == 0 ? double.NaN : Median(peaks) / mad;
                    output.WriteLine($"{exp,-22} {width,5} x {height,-5} {stars.Count,5} {fwhm,5:F2} {mad,8:G3} {undershoot,21:F2} {share,16:F3} {peakMad,9:F0} {(double.IsNaN(peakMad) ? double.NaN : undershoot / peakMad),17:P1}");
                }
                finally
                {
                    wrapped.Release();
                }
            }
            finally
            {
                master.Release();
            }
        }

        output.WriteLine("");
        output.WriteLine("undershoot: median over stars of the deepest pixel in the annulus (1.2 to 2.5 FWHM) below the background, in MADs of the");
        output.WriteLine("background; a positive number is a dip. undershoot / peak reads it against the median star's own peak.");
    }

    private static float NearestDistance(float[] xs, float[] ys, Vector2 p)
    {
        var bestSq = float.MaxValue;
        for (var j = 0; j < xs.Length; j++)
        {
            var dx = xs[j] - p.X;
            var dy = ys[j] - p.Y;
            var sq = (dx * dx) + (dy * dy);
            if (sq < bestSq)
            {
                bestSq = sq;
            }
        }

        return MathF.Sqrt(bestSq);
    }

    /// <summary>The share of a detection's background-subtracted 3 by 3 flux carried by its peak
    /// photosite, on the raw mosaic: near 1 for a single warm photosite, well under that for a star
    /// sampled over several. Background is the median of the 9 by 9 ring outside the 5 by 5.</summary>
    private static float PeakPhotositeFraction(float[] plane, int width, int height, float xc, float yc)
    {
        var cx = (int)MathF.Round(xc);
        var cy = (int)MathF.Round(yc);
        if (cx < 5 || cy < 5 || cx >= width - 5 || cy >= height - 5)
        {
            return float.NaN;
        }

        var ring = new List<float>(56);
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)
                {
                    var v = plane[((cy + dy) * width) + cx + dx];
                    if (!float.IsNaN(v))
                    {
                        ring.Add(v);
                    }
                }
            }
        }

        if (ring.Count == 0)
        {
            return float.NaN;
        }

        ring.Sort();
        var bg = ring[ring.Count / 2];

        // The peak photosite within the 3x3 about the centroid (a centroid can sit between photosites),
        // then the 3x3 about IT.
        var px = cx;
        var py = cy;
        var peak = float.MinValue;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var v = plane[((cy + dy) * width) + cx + dx];
                if (v > peak)
                {
                    peak = v;
                    px = cx + dx;
                    py = cy + dy;
                }
            }
        }

        var sum = 0f;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var v = plane[((py + dy) * width) + px + dx] - bg;
                if (v > 0f)
                {
                    sum += v;
                }
            }
        }

        return sum > 0f ? (peak - bg) / sum : float.NaN;
    }

    /// <summary>The pipeline's detection on a sub: calibrate, detect on the mosaic through the mono path.
    /// The caller releases the calibrated frame (the raw one is consumed by the calibrator).</summary>
    private static async Task<(StarList Stars, Image Calibrated)> DetectAsync(string path, Calibrator calibrator, CancellationToken ct)
    {
        Image.TryReadFitsFile(path, out var raw).ShouldBeTrue(path);
        var calibrated = calibrator.Apply(raw!);
        var (stars, debayered) = await FrameRegistration.DetectAsync(calibrated, DebayerAlgorithm.VNG, 5f, 2000, ct);
        debayered.Release();
        return (stars, calibrated);
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

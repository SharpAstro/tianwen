using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What a plate solve COSTS, split the two ways a session actually pays it: the FIRST solve in a
    /// process, which carries the Tycho-2 bulk decode, and every solve after it, which does not.
    /// <see cref="T:TianWen.UI.Benchmarks.PlateSolveBenchmarks"/> structurally cannot report the first
    /// one -- the catalog is cached per process, so its second iteration is already a warm start -- and
    /// on this workload its per-iteration Error came out wider than the effects being chased, so a
    /// stage split over real frames is the measurement that carries information.
    /// </summary>
    /// <remarks>
    /// <para>Env-gated (<c>TIANWEN_SOLVE_TIMING</c>): it decodes Tycho-2 and then solves five real
    /// frames twice over, which is minutes rather than the milliseconds a unit test may spend.</para>
    /// <para>The stage split is read off the solver's OWN Debug lines rather than from a second set of
    /// stopwatches here, so the probe cannot report a decomposition the production path lacks.</para>
    /// </remarks>
    [Collection("Astrometry")]
    public class SolveTimingProbe(ITestOutputHelper output)
    {
        private sealed class StageCapture : ILogger<CatalogPlateSolver>
        {
            public List<string> Lines { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                if (msg.Contains("ms") || msg.Contains("QuadScaleRecovery") || logLevel >= LogLevel.Warning)
                {
                    Lines.Add(logLevel >= LogLevel.Warning ? $"[{logLevel}] {msg}" : msg);
                }
            }
        }

        /// <summary>
        /// The real frames committed to this suite that carry both a pointing hint and a scale hint.
        /// Two rigs and two capture programs, so the numbers are not one camera's story: the 270 mm
        /// SH61 at 2.87"/px and the 130/135 mm Samyang at ~5.9"/px, N.I.N.A. and SharpCap.
        /// </summary>
        private static readonly (string Fixture, string What)[] Frames =
        [
            ("2026-02-15_00-56-23__-5.00_60.00s_0058", "NGC 3576  SH61 270mm  3008x3008 GRBG i16"),
            ("2026-01-18_23-26-51__-5.00_60.00s_0002", "HD 71216  SY 130mm   3008x3008 RGGB i16"),
            ("RGGB_frame_bx0_by0_top_down", "Horsehead SY 135mm   3008x3008 RGGB i16 (SharpCap)"),
            ("Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop", "Vela P8   SY 130mm   2354x2150 mono f32 (master crop)"),
            ("Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop", "Vela P10  SY 130mm   1310x1291 rgb  f32 (master crop)"),
        ];

        [Fact(Timeout = 1_800_000)]
        public async Task ReportTheColdAndWarmCostOfSolvingRealFrames()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_SOLVE_TIMING")),
                "Set TIANWEN_SOLVE_TIMING=1 to run the solve timing probe");

            var ct = TestContext.Current.CancellationToken;
            var process = Stopwatch.StartNew();

            // The cold half: a process pays this once, before any frame is solved.
            var sw = Stopwatch.StartNew();
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            var catalogInitMs = sw.Elapsed.TotalMilliseconds;
            output.WriteLine($"catalog init  {catalogInitMs,8:F1} ms   (ONCE per process; tycho2={db.Tycho2BulkLoadState})");
            foreach (var (phase, elapsed) in db.LastInitPhaseTimings)
            {
                output.WriteLine($"    {elapsed.TotalMilliseconds,8:F1} ms  {phase}");
            }
            output.WriteLine("");

            var capture = new StageCapture();
            var solver = new CatalogPlateSolver(db, capture);
            const int Passes = 6;
            var rows = new List<string>();

            for (var pass = 1; pass <= Passes; pass++)
            {
                output.WriteLine(pass == 1
                    ? "=== pass 1: first solve of each frame (JIT still cold on the first row) ==="
                    : $"=== pass {pass} of {Passes}: the same frames again, everything warm ===");

                foreach (var (fixture, what) in Frames)
                {
                    sw.Restart();
                    var image = await SharedTestData.ExtractGZippedFitsImageAsync(fixture, isReadOnly: false, cancellationToken: ct);
                    var loadMs = sw.Elapsed.TotalMilliseconds;

                    if (image.GetImageDim() is not { } dim || double.IsNaN(image.ImageMeta.TargetRA))
                    {
                        output.WriteLine($"  {what}  SKIPPED (no pixel scale, or no pointing hint)");
                        image.Release();
                        continue;
                    }

                    var hint = new WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);

                    capture.Lines.Clear();
                    sw.Restart();
                    var result = await solver.SolveImageAsync(image, dim, searchOrigin: hint, cancellationToken: ct);
                    var solveMs = sw.Elapsed.TotalMilliseconds;

                    output.WriteLine($"  {what}");
                    output.WriteLine($"    load        {loadMs,8:F1} ms   {dim.PixelScale:F3}\"/px  fov {dim.FieldOfView.width:F2}x{dim.FieldOfView.height:F2} deg");
                    foreach (var line in capture.Lines)
                    {
                        output.WriteLine($"    | {line}");
                    }

                    if (result.Solution is { } s)
                    {
                        var prior = solver.LastScalePrior is { } sp
                            ? $"{dim.PixelScale / sp.Ratio:F4}\"/px ({sp.Candidates} cands, spread {sp.RelativeSpread:F4})"
                            : "declined";
                        output.WriteLine($"    SOLVE       {solveMs,8:F1} ms   {result.CatalogStars} cat / {result.DetectedStars} det / {result.MatchedStars} matched, {result.Iterations} iter");
                        output.WriteLine($"    scale       header {dim.PixelScale:F4} -> quad {prior} -> solved {s.PixelScaleArcsec:F4}\"/px");
                        rows.Add($"{what,-56} pass{pass} {solveMs,8:F1} ms  load {loadMs,7:F1} ms  det {result.DetectedStars,4}  matched {result.MatchedStars,4}");
                    }
                    else
                    {
                        output.WriteLine($"    NO SOLUTION {solveMs,8:F1} ms   {result.CatalogStars} cat / {result.DetectedStars} det");
                        rows.Add($"{what,-56} pass{pass} {solveMs,8:F1} ms  NO SOLUTION");
                    }

                    output.WriteLine("");
                    image.Release();
                }
            }

            output.WriteLine($"=== summary (process wall clock {process.Elapsed.TotalSeconds:F1} s) ===");
            output.WriteLine($"cold catalog init {catalogInitMs:F0} ms is paid once, and is NOT part of any solve below");
            foreach (var row in rows)
            {
                output.WriteLine($"  {row}");
            }
        }

        /// <summary>
        /// Why the Vela P10 crop does not solve, and what it looks like. Nothing had ever asked this
        /// fixture for a WCS before -- it is a stretch / colour / codec fixture everywhere else -- so
        /// its failure is a property of the FILE, and this separates the two things that can be wrong
        /// with a crop's header: it points somewhere else (widen the search radius until it lands) or
        /// it states the wrong scale (offer the solver half and double). P8, the crop that DOES solve,
        /// is rendered beside it as the control.
        /// </summary>
        [Fact(Timeout = 900_000)]
        public async Task ReportWhyTheVelaCropDoesNotSolveAndRenderIt()
        {
            Assert.SkipUnless(
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_SOLVE_TIMING")),
                "Set TIANWEN_SOLVE_TIMING=1 to run the solve timing probe");

            var ct = TestContext.Current.CancellationToken;
            var outDir = SharedTestData.CreateTempTestOutputDir();

            foreach (var (fixture, what) in new[] { Frames[4], Frames[3] })
            {
                var path = await SharedTestData.ExtractGZippedFitsFileAsync(fixture, ct);
                if (await AstroImageDocument.OpenAsync(path, DebayerAlgorithm.AHD, ct) is not { } doc)
                {
                    output.WriteLine($"{what}: could not open");
                    continue;
                }

                var uniforms = doc.ComputeStretchUniforms(StretchMode.Linked, new StretchParameters(0.15, -2.8));
                var img = doc.UnstretchedImage;
                var (ch, w, h) = img.Shape;
                var rgba = new byte[w * h * 4];
                img.RenderStretchedRgba(uniforms, rgba);
                var png = Path.Combine(outDir, fixture[..Math.Min(28, fixture.Length)] + ".png");
                await File.WriteAllBytesAsync(png, PngWriter.Encode(rgba, w, h), ct);
                output.WriteLine($"{what}");
                output.WriteLine($"   {ch}ch {w}x{h} -> {png}");
            }

            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            var capture = new StageCapture();
            var solver = new CatalogPlateSolver(db, capture);

            var image = await SharedTestData.ExtractGZippedFitsImageAsync(Frames[4].Fixture, isReadOnly: false, cancellationToken: ct);
            var declared = image.GetImageDim()!.Value;
            var hint = new WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec);
            output.WriteLine("");
            output.WriteLine($"P10 header hint RA={hint.CenterRA:F5}h Dec={hint.CenterDec:F4}, declared {declared.PixelScale:F4}\"/px, {declared.Width}x{declared.Height}");

            // Hypothesis 1: the crop points somewhere the default radius does not reach.
            foreach (var radius in new[] { 1.63, 3.0, 6.0, 12.0 })
            {
                var r = await solver.SolveImageAsync(image, declared, searchOrigin: hint, searchRadius: radius, cancellationToken: ct);
                if (r.Solution is { } s)
                {
                    output.WriteLine($"  radius {radius,5:F2} deg -> SOLVED at ({s.CenterRA:F5}h {s.CenterDec:F4}), " +
                        $"{SepDeg(hint.CenterRA, hint.CenterDec, s.CenterRA, s.CenterDec) * 60:F1} arcmin off the hint, {s.PixelScaleArcsec:F4}\"/px");
                    break;
                }

                output.WriteLine($"  radius {radius,5:F2} deg -> no solution ({r.Elapsed.TotalMilliseconds:F0} ms, {r.CatalogStars} cat / {r.DetectedStars} det)");
            }

            // Hypothesis 2: the crop came off a master at a different sampling than FOCALLEN implies.
            foreach (var factor in new[] { 0.5f, 0.8f, 0.9f, 1.1f, 1.25f, 1.5f, 2.0f })
            {
                var scaled = new ImageDim(declared.PixelScale * factor, declared.Width, declared.Height);
                var r = await solver.SolveImageAsync(image, scaled, searchOrigin: hint, searchRadius: 6.0, cancellationToken: ct);
                output.WriteLine($"  scale x{factor} ({scaled.PixelScale:F4}\"/px) -> " +
                    (r.Solution is { } s
                        ? $"SOLVED at ({s.CenterRA:F5}h {s.CenterDec:F4}), {s.PixelScaleArcsec:F4}\"/px"
                        : $"no solution ({r.Elapsed.TotalMilliseconds:F0} ms)"));
            }

            image.Release();
        }

        private static double SepDeg(double ra1H, double dec1, double ra2H, double dec2)
        {
            var r1 = double.DegreesToRadians(ra1H * 15.0);
            var d1 = double.DegreesToRadians(dec1);
            var r2 = double.DegreesToRadians(ra2H * 15.0);
            var d2 = double.DegreesToRadians(dec2);
            var cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(r1 - r2);
            return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1.0, 1.0)));
        }

    }
}

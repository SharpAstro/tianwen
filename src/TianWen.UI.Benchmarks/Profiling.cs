using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Jpeg;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Profiling driver — tight-loops a single image-decode path so an external
/// sampling profiler can attach via <c>dotnet-trace</c>. Distinct from
/// <see cref="ImageReadBenchmarks"/> because BenchmarkDotNet's iteration
/// harness adds overhead and event spans of its own to the trace, drowning
/// out the per-method samples we actually want to see.
/// <para>Usage:</para>
/// <code>
/// # Terminal A (loops the decode for 15s, prints throughput)
/// dotnet TianWen.UI.Benchmarks.dll profile cr2 --seconds 15
///
/// # Terminal B (start trace; converts to .speedscope.json on exit)
/// dotnet-trace collect --name TianWen.UI.Benchmarks --profile cpu-sampling \
///     --duration 00:00:12 --format Speedscope
/// </code>
/// <para>The benchmark process keeps decoding until the duration elapses,
/// then prints "decoded N frames in X seconds (Y ms/frame)" so you can
/// sanity-check the trace timeline against the throughput.</para>
/// </summary>
internal static class Profiling
{
    public static void Run(string format, string[] args)
    {
        var seconds = 15;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var s))
                seconds = s;
        }

        var fmt = format.ToLowerInvariant();

        // `tabrender` mode measures the per-frame cost of a full layout-driven tab render
        // (GuiderTab: builds the frame layout tree + arranges + paints, plus the paint-owning
        // guide-graph / scatter / star-profile chart controls) versus a chart control alone. It
        // reports us/frame AND bytes/frame, so the "does per-frame layout-tree building cost us?"
        // question gets a number instead of a guess.
        if (fmt == "tabrender")
        {
            ProfileTabRender(seconds);
            return;
        }
        // `ljpeg` mode is a different shape from the file-decode modes:
        // we extract the lossless-JPEG strip from a CR2 once, then loop
        // StbImageSharp directly so the trace shows pure Huffman/predictor
        // hotspots with no CR2 wrapper / TIFF walk / file I/O noise.
        if (fmt == "ljpeg")
        {
            ProfileLosslessJpeg(seconds);
            return;
        }

        // `findstars` mode loops Image.FindStarsAsync on the real-data IMX533
        // fixture so dotnet-trace can capture hot frames inside DetectStarsAsync
        // / AnalyseStar without BDN harness noise.
        if (fmt == "findstars")
        {
            ProfileFindStars(seconds, args);
            return;
        }

        // `planetary` mode prints a per-stage breakdown of a live rolling-window stack
        // (load/grade/align/fold + the per-publish wavelet+adopt), then tight-loops the full stack so
        // dotnet-trace can pinpoint method hotspots. `--frames N` sets the window size (default 300).
        if (fmt == "planetary")
        {
            ProfilePlanetary(seconds, args);
            return;
        }

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var path = fmt switch
        {
            "cr2" => Path.Combine(fixturesDir, "CR2", "_MG_7578.CR2"),
            "cr3" => Path.Combine(fixturesDir, "CR3", "Canon_EOS_R5_CRAW.CR3"),
            "tiff" => SetupSyntheticTiff(),
            _ => throw new ArgumentException(
                $"unknown format '{format}', expected cr2 / cr3 / tiff / ljpeg / findstars / planetary", nameof(format))
        };
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"fixture not found: {path}");
            return;
        }

        Console.WriteLine($"Profiling decode of {format.ToUpperInvariant()} ({Path.GetFileName(path)}) for {seconds}s.");
        PrintAttachInstructions(seconds);

        var deadline = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.Elapsed < deadline)
        {
            Image.TryReadImageFile(path, out _);
            count++;
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"decoded {count} frames in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({sw.Elapsed.TotalMilliseconds / Math.Max(1, count):F1} ms/frame)");
    }

    /// <summary>Tight-loop of <see cref="Image.FindStarsAsync"/> on a real
    /// SVBONY SV605CC (IMX533M, 3008x3008) 60s frame, debayered once before
    /// the loop. Used to profile <c>DetectStarsAsync</c> / <c>AnalyseStar</c>
    /// hotspots independently of the BDN iteration harness.
    /// <para>Args (optional, after `profile findstars`):</para>
    /// <list type="bullet">
    ///   <item><c>--cfg default</c> -- snrMin=10, maxStars=500 (single-pass)</item>
    ///   <item><c>--cfg runner</c>  -- snrMin=5,  minStars=2000 (two-pass) [default]</item>
    /// </list></summary>
    private static void ProfileFindStars(int seconds, string[] args)
    {
        var cfg = "runner";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--cfg") cfg = args[i + 1].ToLowerInvariant();
        }

        var bench = new FindStarsBenchmarks();
        bench.Setup();
        // GlobalSetup loads + VNG-debayers the real fixture once. The
        // FindStarsAsync calls below use the same cached image, so the loop
        // measures pure detection cost -- BUT FindStarsAsync memoizes results
        // keyed on its arguments, so we MUST invalidate per iteration or
        // every call after the first is a dict lookup masquerading as work.
        Func<Task> step = cfg switch
        {
            "default" => () => bench.Real_IMX533_DefaultCfg(),
            "runner" => () => bench.Real_IMX533_RunnerCfg(),
            "platesolve" => () => bench.Real_IMX533_PlateSolveCfg(),
            _ => throw new ArgumentException(
                $"unknown --cfg '{cfg}', expected default / runner / platesolve", nameof(args))
        };

        Console.WriteLine($"Profiling Image.FindStarsAsync on real IMX533 frame, cfg={cfg}, for {seconds}s.");
        PrintAttachInstructions(seconds);
        var deadline = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.Elapsed < deadline)
        {
            bench.InvalidateCaches();
            step().GetAwaiter().GetResult();
            count++;
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"detected stars in {count} iterations in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({sw.Elapsed.TotalMilliseconds / Math.Max(1, count):F1} ms/iter)");
    }

    /// <summary>Tight-loop of <see cref="LosslessJpeg.FromMemory"/> on a
    /// pre-extracted CR2 raw IFD strip. Used to profile the StbImageSharp
    /// SOF3 decoder in isolation, since the full CR2 read includes ~20% of
    /// time outside of LosslessJpeg (TIFF walk, slice unscramble, EXIF
    /// parse, Image construction) and obscures Huffman / predictor
    /// hotspots in the trace.</summary>
    private static void ProfileLosslessJpeg(int seconds)
    {
        var bench = new LosslessJpegBenchmarks();
        bench.Setup();
        Console.WriteLine($"Profiling LosslessJpeg.FromMemory for {seconds}s.");
        PrintAttachInstructions(seconds);
        var deadline = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.Elapsed < deadline)
        {
            bench.DecodeRawIfdStrip();
            count++;
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"decoded {count} strips in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({sw.Elapsed.TotalMilliseconds / Math.Max(1, count):F1} ms/strip)");
    }

    /// <summary>Prints a per-stage breakdown of a live rolling-window stack (so the hotspot is obvious
    /// without an external profiler), then tight-loops the full stack for <paramref name="seconds"/> so
    /// dotnet-trace can capture method-level hotspots. <c>--frames N</c> sets the window size.</summary>
    private static void ProfilePlanetary(int seconds, string[] args)
    {
        var frames = 300;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var fr)) frames = fr;
        }

        var stack = new PlanetaryStackBenchmarks { Frames = frames };
        stack.Setup();
        var master = new PlanetaryMasterBenchmarks { Size = 512 };
        master.Setup();

        var load = TimeMs(() => stack.Load());
        var grade = TimeMs(() => stack.Grade());
        var align = TimeMs(() => stack.Align());
        var fold = TimeMs(() => stack.Fold());
        var full = TimeMs(() => stack.FullStack().GetAwaiter().GetResult());
        var sharpen = TimeMs(() => master.Sharpen());
        var adopt = TimeMs(() => master.Adopt().GetAwaiter().GetResult());

        var perFrameTotal = load + grade + align + fold;
        Console.WriteLine();
        Console.WriteLine($"Live rolling-window stack stage breakdown -- {frames} frames @ {PlanetaryStackBenchmarks.Size}px sub-plane:");
        Console.WriteLine($"  load    {load,9:F1} ms   {Pct(load, perFrameTotal)}");
        Console.WriteLine($"  grade   {grade,9:F1} ms   {Pct(grade, perFrameTotal)}");
        Console.WriteLine($"  align   {align,9:F1} ms   {Pct(align, perFrameTotal)}");
        Console.WriteLine($"  fold    {fold,9:F1} ms   {Pct(fold, perFrameTotal)}");
        Console.WriteLine($"  ---------------------------------");
        Console.WriteLine($"  FULL window stack  {full,9:F1} ms");
        Console.WriteLine();
        Console.WriteLine($"Per-publish display cost (512px RGB master -- runs on every slider change):");
        Console.WriteLine($"  wavelet sharpen   {sharpen,9:F1} ms");
        Console.WriteLine($"  adopt (stats)     {adopt,9:F1} ms");
        Console.WriteLine();

        Console.WriteLine($"Tight-looping FullStack({frames}) for {seconds}s.");
        PrintAttachInstructions(seconds);
        var deadline = TimeSpan.FromSeconds(seconds);
        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.Elapsed < deadline)
        {
            stack.FullStack().GetAwaiter().GetResult();
            count++;
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"ran {count} full stacks in {sw.Elapsed.TotalSeconds:F2}s " +
            $"({sw.Elapsed.TotalMilliseconds / Math.Max(1, count):F0} ms/stack)");
    }

    // Warm once, then report the fastest of 3 runs (steady-state, GC/JIT excluded).
    private static double TimeMs(Action action)
    {
        action();
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    private static string Pct(double part, double whole)
        => whole > 0 ? $"({100 * part / whole,3:F0}%)" : "(  -)";

    private static void PrintAttachInstructions(int seconds)
    {
        Console.WriteLine($"Process PID: {Environment.ProcessId} — attach trace now:");
        Console.WriteLine($"  dotnet-trace collect --process-id {Environment.ProcessId} --profile dotnet-sampled-thread-time --duration 00:00:{seconds - 2:D2} --format Speedscope");
        Console.WriteLine();
        Console.WriteLine("Looping decode...");
    }

    /// <summary>
    /// Times a full layout-driven tab render vs. a single paint-owning chart control, over the CPU
    /// <see cref="DIR.Lib.RgbaImageRenderer"/> (no GPU). Font is left empty so text draws no-op --
    /// glyph rasterisation is identical before/after the refactor, so isolating the layout-tree build +
    /// arrange + fill cost is what answers the perf question. Prints us/frame and bytes/frame for each.
    /// </summary>
    private static void ProfileTabRender(int seconds)
    {
        var state = BuildGuidingLiveState();
        var timeProvider = new BenchTimeProvider();
        using var renderer = new DIR.Lib.RgbaImageRenderer(1280, 800);
        var tab = new TianWen.UI.Abstractions.GuiderTab<DIR.Lib.RgbaImage>(renderer)
        {
            DpiScale = 1f,
            FontPath = string.Empty,
        };
        var fullRect = new DIR.Lib.RectF32(0, 0, renderer.Width, renderer.Height);
        var graphRect = new DIR.Lib.RectF32(0, 640, renderer.Width, 160);

        // Warm up (JIT + first-frame allocations).
        for (var i = 0; i < 50; i++)
        {
            tab.Render(state, fullRect, timeProvider);
            TianWen.UI.Abstractions.GuideGraphRenderer.Render(renderer, graphRect,
                state.GuideSamples, state.LastGuideStats, 1f, string.Empty, 13f, showAxisLabels: true, showLegend: true);
        }

        Console.WriteLine($"Tab-render profile (1280x800 CPU, empty font): looping for {seconds}s each...");
        Console.WriteLine();

        Measure("GuiderTab.Render  (full frame: layout tree build+arrange+paint + all charts)",
            seconds, () => tab.Render(state, fullRect, timeProvider));

        Measure("GuideGraphRenderer.Render  (chart control only: direct pixel draw, no tree)",
            seconds, () => TianWen.UI.Abstractions.GuideGraphRenderer.Render(renderer, graphRect,
                state.GuideSamples, state.LastGuideStats, 1f, string.Empty, 13f, showAxisLabels: true, showLegend: true));

        static void Measure(string label, int seconds, Action frame)
        {
            // Allocation: a fixed-count loop with a GC-quiesced baseline.
            const int allocIters = 2000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < allocIters; i++) frame();
            var bytesPerFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)allocIters;

            // Throughput: loop until the wall-clock budget elapses.
            var sw = Stopwatch.StartNew();
            var frames = 0L;
            var budget = TimeSpan.FromSeconds(seconds);
            while (sw.Elapsed < budget)
            {
                frame();
                frames++;
            }
            sw.Stop();
            var usPerFrame = sw.Elapsed.TotalMilliseconds * 1000.0 / frames;

            Console.WriteLine($"  {label}");
            Console.WriteLine($"    {frames:N0} frames in {sw.Elapsed.TotalSeconds:F1}s  ->  {usPerFrame:F1} us/frame,  {bytesPerFrame:N0} bytes/frame");
            Console.WriteLine();
        }
    }

    /// <summary>Mid-guiding <see cref="TianWen.UI.Abstractions.LiveSessionState"/> (60 samples, stats,
    /// star profile) -- the same shape as GuiderTabLayoutTests, so the tab paints real chart content.</summary>
    private static TianWen.UI.Abstractions.LiveSessionState BuildGuidingLiveState()
    {
        var start = new DateTimeOffset(2025, 12, 15, 22, 0, 0, TimeSpan.Zero);
        var samples = System.Collections.Immutable.ImmutableArray.CreateBuilder<TianWen.Lib.Sequencing.GuideErrorSample>(60);
        for (var i = 0; i < 60; i++)
        {
            samples.Add(new TianWen.Lib.Sequencing.GuideErrorSample(
                start + TimeSpan.FromSeconds(i * 2),
                RaError: Math.Sin(i * 0.3) * 0.8,
                DecError: Math.Cos(i * 0.2) * 0.5,
                RaCorrectionMs: i % 5 == 0 ? 120 : 0,
                DecCorrectionMs: i % 7 == 0 ? -90 : 0,
                IsDither: i == 30,
                IsSettling: i is > 30 and < 35));
        }

        var h = new float[21];
        var v = new float[21];
        for (var i = 0; i < h.Length; i++)
        {
            var d = i - 10;
            h[i] = 1000f * MathF.Exp(-d * d / 8f);
            v[i] = 900f * MathF.Exp(-d * d / 10f);
        }

        return new TianWen.UI.Abstractions.LiveSessionState
        {
            IsRunning = true,
            Phase = TianWen.Lib.Sequencing.SessionPhase.Observing,
            GuiderState = "Guiding",
            GuideSamples = samples.MoveToImmutable(),
            LastGuideStats = new TianWen.Lib.Devices.Guider.GuideStats
            {
                TotalRMS = 0.82, RaRMS = 0.55, DecRMS = 0.61, PeakRa = 1.6, PeakDec = 1.3,
                LastRaErr = 0.31, LastDecErr = -0.22,
            },
            GuideExposure = TimeSpan.FromSeconds(2),
            GuideStarProfile = (h, v),
        };
    }

    /// <summary>Minimal <see cref="TianWen.Lib.Devices.ITimeProvider"/> for the render loop (no timers /
    /// sleeps are exercised during a paint).</summary>
    private sealed class BenchTimeProvider : TianWen.Lib.Devices.ITimeProvider
    {
        public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public long GetTimestamp() => Stopwatch.GetTimestamp();
        public long TimestampFrequency => Stopwatch.Frequency;
        public System.Threading.ITimer CreateTimer(System.Threading.TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new NotSupportedException();
        public ValueTask SleepAsync(TimeSpan duration, System.Threading.CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public TimeProvider System => TimeProvider.System;
    }

    /// <summary>Returns the path to a synthetic TIFF used by the TIFF
    /// profile mode, writing it on first use (same one ImageReadBenchmarks
    /// uses; we re-find it here rather than duplicate the writer).</summary>
    private static string SetupSyntheticTiff()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks", "ImageReadBenchmarks");
        var path = Path.Combine(tempDir, "synthetic_4096x4096_uint16.tif");
        if (File.Exists(path)) return path;
        // Force the benchmark setup to materialise it. Hacky but keeps the
        // synthetic-TIFF writer in one place — Profiling.cs doesn't need to
        // own a copy of it.
        var bench = new ImageReadBenchmarks();
        bench.Setup();
        return path;
    }
}

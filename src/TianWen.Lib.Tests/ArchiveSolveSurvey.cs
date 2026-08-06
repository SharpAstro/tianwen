using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using nom.tam.fits;
using nom.tam.util;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Sweeps an on-disk archive and reports, per capture session, how many of its light frames
    /// <see cref="CatalogPlateSolver"/> actually solves and what each stage costs. This is a
    /// MEASUREMENT harness, not an assertion suite: the pass/fail bar for a real archive belongs
    /// to whoever reads the report, because a session shot through cloud is allowed to fail.
    ///
    /// <para><b>Read-only by construction</b>, same rule as <see cref="VelaMosaicStarListExport"/>:
    /// every archive path is opened for reading and the only write goes to a caller-named output
    /// file. The archive is the user's single copy of the data -- do not add a write path here,
    /// and in particular do not wire up <c>--update-fits</c>-style WCS write-back.</para>
    ///
    /// <para>Run it as:
    /// <code>
    /// TIANWEN_SOLVE_SURVEY=&lt;archive-root&gt; \
    /// TIANWEN_SOLVE_SURVEY_OUT=&lt;report.tsv&gt; \
    /// dotnet test TianWen.Lib.Tests -c Release --filter FullyQualifiedName~SurveyArchive
    /// </code>
    /// <c>TIANWEN_SOLVE_SURVEY</c> takes one or more roots separated by <c>;</c>.
    /// <c>TIANWEN_SOLVE_SURVEY_LIMIT</c> caps frames per session (0 = every frame, the default);
    /// <c>TIANWEN_SOLVE_SURVEY_PARALLEL</c> overrides the worker count;
    /// <c>TIANWEN_SOLVE_SURVEY_EXCLUDE</c> drops paths containing any of its <c>;</c>-separated
    /// substrings; <c>TIANWEN_SOLVE_SURVEY_INCLUDE_UNTYPED=1</c> also attempts frames whose header
    /// carries no <c>IMAGETYP</c>.</para>
    /// </summary>
    [Collection("Astrometry")]
    public class ArchiveSolveSurvey(ITestOutputHelper output)
    {
        /// <summary>
        /// Detection arguments shared with <see cref="VelaMosaicStarListExport"/> and with what the
        /// solver itself asks for, so the star list is computed once and cached on the
        /// <see cref="Image"/> rather than twice with different parameters.
        /// </summary>
        private const float SnrMin = 5f;
        private const int MaxStars = 500;
        private const int MinStars = 50;

        /// <summary>
        /// Default worker count. Each worker holds one 3008x3008 float frame plus detection
        /// scratch (~150 MB peak measured), and the archive lives on a USB spinning disk, so
        /// beyond ~8 the run is seek-bound rather than CPU-bound.
        /// </summary>
        private const int DefaultParallel = 8;

        private sealed record FrameOutcome(
            string Session,
            string File,
            string ObjectName,
            string Instrument,
            double ExposureSec,
            int Width,
            int Height,
            double PixelScale,
            bool HasHint,
            int DetectedStars,
            float MedianHfd,
            float MedianEllipticity,
            bool Solved,
            string Verdict,
            int MatchedStars,
            int CatalogStars,
            int SipOrder,
            double HintOffsetArcmin,
            double ReadMs,
            double DetectMs,
            double SolveMs);

        [Fact(Timeout = 6 * 60 * 60 * 1000)]
        public async Task SurveyArchive()
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_SOLVE_SURVEY") is not { Length: > 0 } rootsSpec)
            {
                Assert.Skip("Set TIANWEN_SOLVE_SURVEY to one or more archive roots (';'-separated) to run the solve survey.");
                return;
            }

            if (Environment.GetEnvironmentVariable("TIANWEN_SOLVE_SURVEY_OUT") is not { Length: > 0 } outPath)
            {
                Assert.Skip("Set TIANWEN_SOLVE_SURVEY_OUT to the TSV report path to write.");
                return;
            }

            var cancellationToken = TestContext.Current.CancellationToken;
            var perSessionLimit = ParseInt("TIANWEN_SOLVE_SURVEY_LIMIT", 0);
            var parallel = ParseInt("TIANWEN_SOLVE_SURVEY_PARALLEL", Math.Min(DefaultParallel, Environment.ProcessorCount));
            var includeUntyped = ParseInt("TIANWEN_SOLVE_SURVEY_INCLUDE_UNTYPED", 0) != 0;
            var range = ParseInt("TIANWEN_SOLVE_SURVEY_RANGE_PCT", 0) is var pct and > 0
                ? pct / 100f
                : IPlateSolver.DefaultRange;

            // ITestOutputHelper only surfaces when the test ENDS, and this one runs for hours over
            // a slow disk -- so every progress line is also appended to a sibling .progress.log
            // that can be tailed from outside the test host. Without it a stalled enumeration and
            // a working solve loop look exactly the same from the shell.
            var progressPath = Path.ChangeExtension(outPath, ".progress.log");
            File.WriteAllText(progressPath, "");

            // A lock rather than a Task hand-off or a CAS swap: the shared resource is an OS file
            // handle opened per append, which nothing lock-free can serialise. It is off the hot
            // path (one line per 100 frames, i.e. per ~20 s of work) and no rendering thread can
            // reach it, so the two other clauses of the standing rule are satisfied too.
            var progressLock = new Lock();
            void Log(string message)
            {
                output.WriteLine(message);
                lock (progressLock)
                {
                    File.AppendAllText(progressPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }

            var roots = rootsSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var excludes = (Environment.GetEnvironmentVariable("TIANWEN_SOLVE_SURVEY_EXCLUDE") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var swScan = Stopwatch.StartNew();
            var candidates = new List<string>();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    Log($"root not found, skipped: {root}");
                    continue;
                }
                candidates.AddRange(Directory.EnumerateFiles(root, "*.fits", SearchOption.AllDirectories));
                candidates.AddRange(Directory.EnumerateFiles(root, "*.fit", SearchOption.AllDirectories));
            }

            // A non-sidereal capture folder (lunar / planetary burst) is full of LIGHT frames that
            // hold no star field, so leaving them in would report a solver failure for frames the
            // solver was never meant to see. Excluded by path substring rather than by heuristic:
            // which folders those are is the operator's knowledge, not something to guess at.
            var excluded = excludes.Length > 0
                ? candidates.RemoveAll(p => excludes.Any(e => p.Contains(e, StringComparison.OrdinalIgnoreCase)))
                : 0;

            // SharpCap live-stack output, dropped by NAME because the header cannot always tell.
            // The EXPTIME-vs-EXPOSURE test catches the usual `Stack_32bits_140frames_8400s.fits`,
            // but SharpCap also emits `Stack_32bits_1frames_200s.fits` -- a "stack" of ONE frame,
            // whose total exposure equals its sub exposure, so no header rule can distinguish it
            // from a raw light. It is still an integration, it is still a 3-plane 32-bit file that
            // costs ~280 MB to read, and it was in the set that ran this harness out of memory.
            var stacked = candidates.RemoveAll(static p =>
                Path.GetFileName(p).StartsWith("Stack_", StringComparison.OrdinalIgnoreCase));
            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            Log($"{candidates.Count} FITS under {roots.Length} root(s) in {swScan.Elapsed.TotalSeconds:F0} s" +
                (excluded > 0 ? $" ({excluded} excluded by path)" : "") +
                (stacked > 0 ? $" ({stacked} Stack_* live-stack outputs skipped)" : ""));

            // Group by the directory that holds the frames: in this archive that IS the pointing
            // (one LIGHT folder per panel per night), which is the unit the report is about.
            var bySession = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in candidates)
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                if (!bySession.TryGetValue(dir, out var list))
                {
                    bySession[dir] = list = new List<string>();
                }
                list.Add(path);
            }

            var work = new List<string>();
            foreach (var (_, files) in bySession)
            {
                work.AddRange(perSessionLimit > 0 && files.Count > perSessionLimit ? files.Take(perSessionLimit) : files);
            }

            // Header pre-pass. A full read pulls 36 MB per 3008^2 frame off a USB spinning disk
            // just to discover the frame is a bias; the header-only path costs ~3 KB, so on an
            // archive that is mostly calibration this is the difference between minutes and hours.
            swScan.Restart();
            var lights = new ConcurrentBag<(long Shape, long Bytes, string Path)>();
            var typeCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken },
                (path, ct) =>
                {
                    var kind = Image.TryReadFitsHeader(path, out var info)
                        ? info.IsMaster ? "master:" + info.FrameType
                            : IsIntegration(path, info) ? "integration:" + info.FrameType
                            : info.FrameType.ToString()
                        : "unreadable-header";
                    typeCounts.AddOrUpdate(kind, 1, static (_, n) => n + 1);

                    // An older capture program may write no IMAGETYP at all, so a frame typed
                    // None is of UNKNOWN kind, not known-not-a-light -- on this archive that is
                    // a five-figure blind spot. Opt in to solve them anyway: a frame that solves
                    // was a light, which is the only evidence available once the header is silent.
                    if (info is not null && (kind == nameof(FrameType.Light) || (includeUntyped && kind == nameof(FrameType.None))))
                    {
                        // Carry the shape from the pre-pass so the solve order can group by it --
                        // the header read already knows it, and re-deriving it later would mean
                        // opening every file a third time.
                        lights.Add(((long)info.Height << 32 | (uint)info.Width, SafeFileLength(path), path));
                    }
                    return ValueTask.CompletedTask;
                });

            // Order by frame SHAPE first, path second. Array2DPool buckets on exact
            // (height, width), so interleaving 24 shapes evicts a bucket before it is reused and
            // the pool degenerates into an allocator with extra steps; running one shape to
            // completion keeps a single bucket hot. Path order within a shape preserves session
            // locality, which is also what keeps the OS file cache useful.
            var ordered = lights.ToList();
            ordered.Sort(static (a, b) => a.Shape != b.Shape
                ? a.Shape.CompareTo(b.Shape)
                : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            var toSolve = ordered.ConvertAll(static t => t.Path);
            var distinctShapes = new HashSet<long>(ordered.ConvertAll(static t => t.Shape)).Count;
            var prePass = $"header pre-pass: {work.Count} files in {swScan.Elapsed.TotalSeconds:F0} s -> " +
                string.Join(", ", typeCounts.OrderByDescending(static kv => kv.Value).Select(static kv => $"{kv.Key}={kv.Value}")) +
                Environment.NewLine +
                $"{toSolve.Count} light frames across {bySession.Count} directories, " +
                $"{distinctShapes} distinct frame shapes, {parallel} workers";
            Log(prePass);
            work = toSolve;

            var db = await SharedCatalogDB.InitAsync(cancellationToken);

            // One solver per worker: CatalogPlateSolver carries per-solve counters
            // (_catalogStars / _detectedStars) as instance fields, so sharing one across
            // threads would interleave the diagnostics it reports.
            var solvers = new ConcurrentBag<CatalogPlateSolver>();

            var outcomes = new ConcurrentBag<FrameOutcome>();
            var done = 0;
            var swAll = Stopwatch.StartNew();

            // Bound the BYTES in flight, not just the file count -- that is the quantity that
            // exhausted memory. A LARGE frame additionally takes one permit here, so only a few
            // can be resident at once while small frames run at the full worker count.
            //
            // Deliberately ONE permit per item rather than a weighted N-permit acquire. The
            // weighted version deadlocks and did: SemaphoreSlim has no atomic multi-acquire, so
            // eight workers each grabbing 3 of 8 permits one at a time all block holding 2, and
            // the run froze at 500/3174 with the CPU flat. One permit per item cannot deadlock.
            var largeFrameGate = new SemaphoreSlim(LargeFrameConcurrency, LargeFrameConcurrency);

            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken },
                async (path, ct) =>
                {
                    var isLarge = SafeFileLength(path) > LargeFrameBytes;
                    if (isLarge)
                    {
                        await largeFrameGate.WaitAsync(ct);
                    }

                    if (!solvers.TryTake(out var solver))
                    {
                        solver = new CatalogPlateSolver(db, NullLogger.Instance);
                    }
                    try
                    {
                        if (await SurveyFrameAsync(solver, path, includeUntyped, range, ct) is { } outcome)
                        {
                            outcomes.Add(outcome);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Same rule as Image.TryReadFitsHeader: this walks an UNTRUSTED archive, so
                        // one malformed frame must be recorded and stepped over, never allowed to
                        // abort a multi-thousand-frame sweep. (It already did once, on a frame with
                        // no IMAGETYP, losing the whole run's results at 1000/3570.)
                        Log($"  EXCEPTION on {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                        outcomes.Add(ErrorOutcome(path, ex));
                    }
                    finally
                    {
                        solvers.Add(solver);
                        if (isLarge)
                        {
                            largeFrameGate.Release();
                        }
                    }

                    var n = Interlocked.Increment(ref done);
                    if (n % 100 == 0)
                    {
                        Log($"  {n}/{work.Count} in {swAll.Elapsed.TotalMinutes:F1} min");
                    }
                });

            var all = outcomes.ToList();
            all.Sort(static (a, b) => string.Compare(a.Session + a.File, b.Session + b.File, StringComparison.OrdinalIgnoreCase));
            all.Count.ShouldBeGreaterThan(0, "no light frame was surveyed -- check the root paths");

            await WriteReportAsync(all, outPath, cancellationToken);

            // The summary goes to a sibling file as well as to test output: a long run is normally
            // watched from outside the test host, where ITestOutputHelper is not visible.
            var summary = prePass + Environment.NewLine + Environment.NewLine + BuildSummary(all, swAll.Elapsed);
            output.WriteLine(summary);
            await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".summary.txt"), summary, cancellationToken);
        }

        private async Task<FrameOutcome?> SurveyFrameAsync(CatalogPlateSolver solver, string path, bool includeUntyped, float range, CancellationToken ct)
        {
            var session = SessionOf(path);
            var file = Path.GetFileName(path);

            var swRead = Stopwatch.StartNew();
            // Pooled, but only because two things now hold that did not on the first attempt:
            // Array2DPool has a total byte ceiling (so 24 distinct shapes can no longer pin arrays
            // the GC wanted back -- that regression cost 12 OOM failures against 6), and the work
            // is ordered by shape so one bucket stays hot instead of thrashing. The frame is owned
            // for exactly one detect+solve and released on every exit path.
            if (!Image.TryReadFitsFile(path, out var image, out var fileWcs, pooled: true))
            {
                return null;
            }
            swRead.Stop();

            // Six exit paths below, several of them early-outs. A `using` covers all of them;
            // a missed one would quietly turn the pooled read back into a plain allocation.
            using var frameOwner = new OwnedFrame(image);

            var meta = image.ImageMeta;

            // Lights only. Calibration frames have no sky in them, and an already-integrated
            // master is not a sub, so neither belongs in a per-session solve rate.
            var typedLight = meta.FrameType is FrameType.Light || (includeUntyped && meta.FrameType is FrameType.None);
            if (!typedLight || meta.IsMaster)
            {
                return null;
            }

            var swDetect = Stopwatch.StartNew();
            var stars = await image.FindStarsAsync(0, snrMin: SnrMin, maxStars: MaxStars, minStars: MinStars, maxRetries: 0, cancellationToken: ct);
            swDetect.Stop();

            var hfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
            var ecc = stars.MapReduceStarProperty(SampleKind.Ellipticity, AggregationMethod.Median);

            var partial = new FrameOutcome(
                session, file, meta.ObjectName ?? "", meta.Instrument ?? "", meta.ExposureDuration.TotalSeconds,
                image.Width, image.Height, double.NaN, fileWcs is not null,
                stars.Count, hfd, ecc,
                false, "", 0, 0, 0, double.NaN,
                swRead.Elapsed.TotalMilliseconds, swDetect.Elapsed.TotalMilliseconds, 0);

            if (image.GetImageDim() is not { } dim)
            {
                // No FOCALLEN / XPIXSZ: the solver has no scale to search around. That is a
                // header problem, not a solver failure, and is reported as its own verdict.
                return partial with { Verdict = "no-scale" };
            }
            partial = partial with { PixelScale = dim.PixelScale };

            var swSolve = Stopwatch.StartNew();
            PlateSolveResult result;
            try
            {
                result = await solver.SolveImageAsync(image, dim, range, searchOrigin: fileWcs, cancellationToken: ct);
            }
            catch (PlateSolverException ex)
            {
                return partial with { Verdict = "error:" + ex.GetType().Name, SolveMs = swSolve.Elapsed.TotalMilliseconds };
            }
            swSolve.Stop();

            if (result.Solution is not { } wcs || !wcs.HasCDMatrix)
            {
                return partial with
                {
                    Verdict = fileWcs is null ? "no-lock-blind" : "no-lock",
                    CatalogStars = result.CatalogStars,
                    SolveMs = swSolve.Elapsed.TotalMilliseconds,
                };
            }

            var offsetArcmin = fileWcs is { } hint ? Separation(hint, wcs) * 60.0 : double.NaN;

            return partial with
            {
                Solved = true,
                Verdict = "solved",
                MatchedStars = result.MatchedStars,
                CatalogStars = result.CatalogStars,
                SipOrder = wcs.HasSip ? wcs.SipOrder : 0,
                HintOffsetArcmin = offsetArcmin,
                SolveMs = swSolve.Elapsed.TotalMilliseconds,
            };
        }

        private static async Task WriteReportAsync(List<FrameOutcome> all, string outPath, CancellationToken ct)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(all.Count * 160);
            sb.AppendLine("session\tfile\tobject\tinstrument\texposure_s\twidth\theight\tscale_arcsec\thas_hint\t" +
                "stars\thfd\tecc\tsolved\tverdict\tmatched\tcatalog\tsip_order\thint_off_arcmin\tread_ms\tdetect_ms\tsolve_ms");
            foreach (var o in all)
            {
                sb.Append(o.Session).Append('\t').Append(o.File).Append('\t').Append(o.ObjectName).Append('\t')
                    .Append(o.Instrument).Append('\t').Append(o.ExposureSec.ToString("F1", inv)).Append('\t')
                    .Append(o.Width).Append('\t').Append(o.Height).Append('\t')
                    .Append(o.PixelScale.ToString("F4", inv)).Append('\t').Append(o.HasHint ? 1 : 0).Append('\t')
                    .Append(o.DetectedStars).Append('\t').Append(o.MedianHfd.ToString("F3", inv)).Append('\t')
                    .Append(o.MedianEllipticity.ToString("F3", inv)).Append('\t').Append(o.Solved ? 1 : 0).Append('\t')
                    .Append(o.Verdict).Append('\t').Append(o.MatchedStars).Append('\t').Append(o.CatalogStars).Append('\t')
                    .Append(o.SipOrder).Append('\t').Append(o.HintOffsetArcmin.ToString("F2", inv)).Append('\t')
                    .Append(o.ReadMs.ToString("F0", inv)).Append('\t').Append(o.DetectMs.ToString("F0", inv)).Append('\t')
                    .Append(o.SolveMs.ToString("F0", inv)).AppendLine();
            }
            await File.WriteAllTextAsync(outPath, sb.ToString(), ct);
        }

        private static string BuildSummary(List<FrameOutcome> all, TimeSpan wall)
        {
            var sb = new StringBuilder();
            sb.AppendLine("session\tframes\tsolved\trate\tmed_stars\tmed_matched\tsip%\tmed_off'\tmed_read\tmed_det\tmed_solve\tverdicts");

            foreach (var group in all.GroupBy(static o => o.Session).OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var frames = group.ToList();
                var solved = frames.Count(static f => f.Solved);
                var verdicts = string.Join(",", frames.GroupBy(static f => f.Verdict)
                    .OrderByDescending(static g => g.Count())
                    .Select(static g => $"{g.Key}={g.Count()}"));
                var sipPct = solved > 0 ? 100.0 * frames.Count(static f => f.Solved && f.SipOrder > 0) / solved : 0.0;

                sb.AppendLine(
                    $"{group.Key}\t{frames.Count}\t{solved}\t{100.0 * solved / frames.Count:F1}%\t" +
                    $"{Median(frames, static f => f.DetectedStars):F0}\t{Median(frames.Where(static f => f.Solved), static f => f.MatchedStars):F0}\t" +
                    $"{sipPct:F0}%\t{Median(frames.Where(static f => f.Solved), static f => f.HintOffsetArcmin):F1}\t" +
                    $"{Median(frames, static f => f.ReadMs):F0}\t{Median(frames, static f => f.DetectMs):F0}\t" +
                    $"{Median(frames, static f => f.SolveMs):F0}\t{verdicts}");
            }

            var total = all.Count;
            var totalSolved = all.Count(static f => f.Solved);
            sb.AppendLine();
            sb.AppendLine($"TOTAL {totalSolved}/{total} solved ({100.0 * totalSolved / total:F1}%) in {wall.TotalMinutes:F1} min wall");
            sb.AppendLine($"  per frame (median): read {Median(all, static f => f.ReadMs):F0} ms, " +
                $"detect {Median(all, static f => f.DetectMs):F0} ms, solve {Median(all, static f => f.SolveMs):F0} ms");
            sb.AppendLine($"  per frame (p90):    read {Percentile(all, static f => f.ReadMs, 0.9):F0} ms, " +
                $"detect {Percentile(all, static f => f.DetectMs, 0.9):F0} ms, solve {Percentile(all, static f => f.SolveMs, 0.9):F0} ms");
            sb.AppendLine($"  throughput: {total / wall.TotalSeconds:F2} frames/s wall, " +
                $"{wall.TotalSeconds / total * 1000:F0} ms/frame wall");

            foreach (var v in all.GroupBy(static f => f.Verdict).OrderByDescending(static g => g.Count()))
            {
                sb.AppendLine($"  verdict {v.Key}: {v.Count()}");
            }
            return sb.ToString();
        }

        private static double Median<T>(IEnumerable<T> source, Func<T, double> selector)
        {
            var values = source.Select(selector).Where(double.IsFinite).ToList();
            if (values.Count == 0)
            {
                return double.NaN;
            }
            values.Sort();
            return values[values.Count / 2];
        }

        private static double Percentile<T>(IEnumerable<T> source, Func<T, double> selector, double p)
        {
            var values = source.Select(selector).Where(double.IsFinite).ToList();
            if (values.Count == 0)
            {
                return double.NaN;
            }
            values.Sort();
            return values[Math.Clamp((int)(values.Count * p), 0, values.Count - 1)];
        }

        /// <summary>
        /// Returns a pooled frame's channel arrays on scope exit. The survey owns each frame for
        /// exactly one detect-and-solve, which is the contract the pooled read asks for.
        ///
        /// <para>Deliberately NOT named for a lease: <see cref="Image.TryLease"/> is the BORROW
        /// primitive, taken by a reader that does not own the frame, and this is the opposite
        /// role. See <c>docs/plans/frame-lifecycle.md</c>.</para>
        /// </summary>
        private readonly struct OwnedFrame(Image image) : IDisposable
        {
            public void Dispose() => image.Release();
        }

        /// <summary>
        /// Above this file size a frame counts as LARGE and competes for a scarce permit. A flat
        /// worker count is what made this harness die: eight concurrent reads is fine for the
        /// 18 MB subs it was tuned on and fatal for a 140 MB 32-bit stack, because the read holds
        /// FITS.Lib's typed array AND the widened <c>float[,]</c> at once, roughly 2x the file and
        /// all of it on the large-object heap.
        /// </summary>
        private const long LargeFrameBytes = 48L * 1024 * 1024;

        /// <summary>How many large frames may be resident at once, whatever the worker count.</summary>
        private const int LargeFrameConcurrency = 2;

        /// <summary>File length, or one slot's worth if it cannot be stat'ed -- a file we cannot
        /// measure is about to fail its read anyway, and must not throw out of the gate.</summary>
        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return LargeFrameBytes;
            }
        }

        /// <summary>
        /// True when this file is an already-integrated product rather than a raw sub, so it does
        /// not belong in a per-session solve rate.
        ///
        /// <para>The three markers the production scan uses (<c>IsMaster</c>, <c>STACK_N</c>, a
        /// TianWen <c>SWCREATE</c>) do NOT catch an Astro Pixel Processor integration: APP copies
        /// the subs' headers, so its output reports <c>IMAGETYP=LIGHT</c> and
        /// <c>SWCREATE='N.I.N.A. 3.2...'</c>, carries no <c>STACK_N</c>, and is not flagged master.
        /// What it cannot hide is that <c>EXPTIME</c> becomes the TOTAL integration (8400 s) while
        /// <c>EXPOSURE</c> stays the per-sub value (60 s). On every raw sub in this archive the two
        /// are equal, so a disagreement is the discriminator.</para>
        /// </summary>
        private static bool IsIntegration(string path, FrameInfo info)
        {
            if (info.StackedFrameCount > 0 || IntegrationFitsWriter.IsTianWenProduct(info.Meta.SWCreator))
            {
                return true;
            }

            // Resilient like Image.TryReadFitsHeader: this walks an untrusted archive, and a file
            // whose header will not parse is simply not classifiable as an integration.
            try
            {
                using var reader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
                using var fits = new Fits(reader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
                if (fits.ReadHDUHeaderOnly()?.Header is not { } header)
                {
                    return false;
                }
                var expTime = header.GetDoubleValue("EXPTIME", double.NaN);
                var exposure = header.GetDoubleValue("EXPOSURE", double.NaN);
                return double.IsFinite(expTime) && double.IsFinite(exposure) && exposure > 0
                    && expTime > exposure * IntegrationExposureFactor;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// How far <c>EXPTIME</c> must exceed <c>EXPOSURE</c> before the frame counts as an
        /// integration. 1.5 rather than 1.0 so a capture program that rounds one of the two
        /// differently (or records live-stack dwell) cannot make a single sub look stacked; a real
        /// integration is a whole multiple of the sub, so the nearest true case is 2x.
        /// </summary>
        private const double IntegrationExposureFactor = 1.5;

        /// <summary>A frame that threw: recorded as a row so the sweep total still accounts for it.</summary>
        private static FrameOutcome ErrorOutcome(string path, Exception ex) => new FrameOutcome(
            SessionOf(path), Path.GetFileName(path), "", "", double.NaN, 0, 0, double.NaN, false,
            0, float.NaN, float.NaN, false, "error:" + ex.GetType().Name, 0, 0, 0, double.NaN,
            double.NaN, double.NaN, double.NaN);

        /// <summary>
        /// Session label: the last two path segments above the file. One segment is not enough --
        /// this archive holds the same pointing folder in two places (a panel both at the project
        /// root and under its Panel group), and a bare folder name would silently merge the copies
        /// into one row with duplicated frames.
        /// </summary>
        private static string SessionOf(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null)
            {
                return "";
            }
            var parent = Path.GetDirectoryName(dir);
            return parent is null ? Path.GetFileName(dir) : Path.Combine(Path.GetFileName(parent), Path.GetFileName(dir));
        }

        /// <summary>Great-circle separation between two pointings, in degrees.</summary>
        private static double Separation(WCS a, WCS b)
        {
            var ra1 = a.CenterRA * (Math.PI / 12.0);
            var ra2 = b.CenterRA * (Math.PI / 12.0);
            var (sinD1, cosD1) = Math.SinCos(double.DegreesToRadians(a.CenterDec));
            var (sinD2, cosD2) = Math.SinCos(double.DegreesToRadians(b.CenterDec));
            var cos = sinD1 * sinD2 + cosD1 * cosD2 * Math.Cos(ra1 - ra2);
            return double.RadiansToDegrees(Math.Acos(Math.Clamp(cos, -1.0, 1.0)));
        }

        private static int ParseInt(string envVar, int fallback) =>
            Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } s
                && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
                ? v
                : fallback;
    }
}

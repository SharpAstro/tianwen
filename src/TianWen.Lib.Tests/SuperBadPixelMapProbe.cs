using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated survey of the Astro Pixel Processor bad-pixel maps archived beside years of
    /// processing runs, to decide how a PER-SENSOR super map should combine them.
    ///
    /// <para><b>Why this exists.</b> A dark-derived mask at any sigma is 15x short of APP's map for
    /// the same sensor (sigma 2 reaches 16,570 px against APP's 253,249), so the fix is a population
    /// one and the archive already contains the population, spread across one map per processing
    /// run. The obvious move is to OR them, and the obvious move is exactly what should be measured
    /// first: 28 maps at 2.8% each could union to barely more than 2.8% (they agree, and OR is
    /// nearly free) or to 40%+ (they disagree, and OR would mask a fifth of the sensor). Those two
    /// worlds want different designs, and the growth curve below separates them.</para>
    ///
    /// <para><b>What the N-of-M histogram decides.</b> A pixel flagged by 26 of 28 runs spanning
    /// five years is a real defect. A pixel flagged by exactly one is likelier a cosmic ray, a
    /// satellite trail through that session's calibration frames, or noise at that gain. So the
    /// combining rule is "flagged in at least K maps", and the histogram is what picks K. Union
    /// (K=1) is only correct if the tail is small.</para>
    ///
    /// <para><b>Two traps this probe is built around.</b> (1) The encoding is 127 linear / 255 hot /
    /// 0 cold, read back normalised, so "bad" is decoded as "not the dominant level" and never as
    /// <c>&gt; 0</c>, which selects the complement. (2) Identity is the physical SENSOR, not the
    /// geometry: SVBONY SV605CC maps are also 3008x3008 because it is the same IMX533 design, and
    /// merging them into an ASI533 map would mask another camera's defects on ours. The filename
    /// carries camera and geometry, and both must match.</para>
    ///
    /// <para>Set <c>TIANWEN_BPM_ROOT</c> to an archive root to scan and <c>TIANWEN_BPM_SENSOR</c> to
    /// the filename infix identifying one camera and geometry (e.g.
    /// <c>ZWO_ASI533MC_Pro-3008x3008</c>). Optional <c>TIANWEN_BPM_REPORT</c> writes the report to a
    /// file, which is the practical way to read it.</para>
    /// </summary>
    public sealed class SuperBadPixelMapProbe(ITestOutputHelper output)
    {
        private readonly System.Text.StringBuilder _log = new();

        private void Line(string text)
        {
            output.WriteLine(text);
            _log.AppendLine(text);
        }

        [Fact]
        public async Task SurveyArchivedAppMapsForOneSensor()
        {
            var root = Environment.GetEnvironmentVariable("TIANWEN_BPM_ROOT");
            var sensor = Environment.GetEnvironmentVariable("TIANWEN_BPM_SENSOR");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(root), "TIANWEN_BPM_ROOT not set");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(sensor), "TIANWEN_BPM_SENSOR not set");
            Directory.Exists(root).ShouldBeTrue($"missing archive root: {root}");

            var ct = TestContext.Current.CancellationToken;
            await Task.Yield();

            // Ordered by timestamp, NOT by path. Path order looks chronological because the archive
            // is foldered by year, but it is not: the "Unsorted" and project folders sort last while
            // dating from the middle and the end of the range, so a path-ordered growth curve
            // attributes new pixels to the wrong period. The K histogram below is order-independent
            // and unaffected; only the running-union column reads chronologically because of this.
            var maps = Directory
                .EnumerateFiles(root!, "BPM*.fits", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(sensor!, StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Line($"sensor filter : {sensor}");
            Line($"maps found    : {maps.Length}");
            maps.Length.ShouldBeGreaterThan(0, "no maps matched the sensor filter");

            byte[]? counts = null;
            int w = 0, h = 0;
            var accepted = 0;
            var duplicates = 0;
            var seen = new HashSet<ulong>();

            Line("");
            Line("  #  |    modified |    bad px |   % | new to union |  union px |  union % | file");

            var unionSoFar = 0;
            foreach (var path in maps)
            {
                ct.ThrowIfCancellationRequested();
                if (!Image.TryReadFitsFile(path, out var map))
                {
                    Line($"     | UNREADABLE                                              | {Rel(root!, path)}");
                    continue;
                }

                var (_, mw, mh) = map.Shape;
                if (counts is null)
                {
                    w = mw;
                    h = mh;
                    counts = new byte[(long)w * h <= int.MaxValue ? w * h : 0];
                    counts.Length.ShouldBeGreaterThan(0, "frame too large for a flat counter");
                }
                else if (mw != w || mh != h)
                {
                    // The sensor filter carries the geometry, so this should be unreachable; if it
                    // fires, the filter was too loose and merging would silently misalign.
                    Line($"     | GEOMETRY {mw}x{mh} != {w}x{h}, skipped                  | {Rel(root!, path)}");
                    continue;
                }

                // ROW ORDER. APP only began writing the card around mid-2024, so every older map in
                // this archive has NONE and reaches here as TopDown purely by the reader's default.
                // For a defect map that is the one assumption worth paying to check, because a
                // mirrored map masks good pixels, keeps every real defect, and leaves the pixel
                // count looking entirely plausible.
                //
                // It was checked, map against map, and the pre-2024 maps ARE top-down: a 2023 map
                // with no card overlaps a 2025 declared-TOP-DOWN map on 28.62% of its pixels
                // (10.27x chance) in the same orientation, against 2.87% (1.03x, i.e. chance) when
                // the later map is flipped. Note what made that work, since an earlier attempt at
                // the same question concluded nothing: compare two maps of SIMILAR density. Testing
                // a 7.5k-pixel dark-derived mask against a 253k-pixel map scored 99.8% one way and
                // 100.0% the other, because a small clustered set lands inside a 2.8%-dense set
                // whichever way up it is.
                //
                // So absent is accepted, and counted, and said out loud.
                if (map.ImageMeta.RowOrder != RowOrder.TopDown)
                {
                    Line($"     | ROWORDER {map.ImageMeta.RowOrder}, skipped (would need a flip)  | {Rel(root!, path)}");
                    continue;
                }

                var plane = map.GetChannelArray(0);
                var linear = DominantLevel(plane, w, h);

                // DEDUPLICATE BY CONTENT FIRST. A processing run copies its BPM into each output
                // folder, so the SAME map is on disk many times over (one appears eight times across
                // the Vela mosaic panels). Counting it once per copy corrupts the N-of-M histogram
                // in the worst possible way: every pixel in an eight-times-duplicated map scores
                // K=8 on its own, which manufactures exactly the "stable across many runs" signal
                // the histogram exists to detect. Hash the decoded defect SET rather than the file
                // bytes, so two copies differing only in a header card still collapse.
                var bad = 0;
                var hash = 1469598103934665603UL; // FNV-1a offset basis
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        if (plane[y, x] == linear)
                        {
                            continue;
                        }
                        bad++;
                        hash = (hash ^ (uint)(y * w + x)) * 1099511628211UL;
                    }
                }

                if (bad == 0)
                {
                    // A map that flags nothing is a failed or skipped APP run, not a clean sensor.
                    Line($"     | EMPTY (0 flagged), skipped                              | {Rel(root!, path)}");
                    continue;
                }
                if (!seen.Add(hash))
                {
                    duplicates++;
                    continue;
                }

                var fresh = 0;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        if (plane[y, x] == linear)
                        {
                            continue;
                        }
                        var i = y * w + x;
                        if (counts[i] == 0)
                        {
                            fresh++;
                            unionSoFar++;
                        }
                        if (counts[i] < byte.MaxValue)
                        {
                            counts[i]++;
                        }
                    }
                }

                accepted++;
                var frame = (double)w * h;
                Line($"  {accepted,2} | {File.GetLastWriteTime(path):yyyy-MM-dd} | {bad,9:N0} | {bad / frame * 100,3:F1} | " +
                     $"{fresh,12:N0} | {unionSoFar,9:N0} | {unionSoFar / frame * 100,7:F2}% | {Rel(root!, path)}");
            }

            if (counts is null || accepted == 0)
            {
                Line("no usable maps");
                await WriteReportAsync(ct);
                return;
            }

            // THE DESIGN QUESTION. How many pixels are flagged by at least K of the accepted maps.
            // A flat curve means the maps agree and union is safe; a curve dominated by K=1 means
            // most of the union is per-session noise and K must be raised.
            var histogram = new int[accepted + 1];
            foreach (var c in counts)
            {
                if (c > 0)
                {
                    histogram[Math.Min(c, accepted)]++;
                }
            }

            var totalPx = (double)w * h;
            Line("");
            Line($"distinct maps : {accepted} of {maps.Length} on disk ({duplicates} exact content duplicates dropped)");
            Line($"frame         : {w}x{h} = {totalPx:N0} px");
            Line("");
            Line("  K (flagged by >= K maps) |  pixels |  % of frame | exactly K");
            var atLeast = 0;
            for (var k = accepted; k >= 1; k--)
            {
                atLeast += histogram[k];
                Line($"  {k,24} | {atLeast,7:N0} | {atLeast / totalPx * 100,10:F3}% | {histogram[k],9:N0}");
            }

            Line("");
            Line("interpretation: K=1 is the plain union. The right K is where the 'exactly K' column");
            Line("stops looking like per-session noise and starts looking like a stable defect set.");

            // VALIDATION. A K threshold picked off the shape of a histogram is a guess until
            // something independent agrees with it. Our own dark-derived mask is that something: it
            // is conservative (sigma 8 on a real master dark) and its pixels are hot by a criterion
            // APP had no part in. If they concentrate at high K the core is genuine defects and the
            // threshold is sound; if they spread evenly across K the "core" is an artifact of how
            // often a map was regenerated and the whole approach is wrong.
            if (Environment.GetEnvironmentVariable("TIANWEN_BPM_DARK") is { Length: > 0 } darkPath
                && File.Exists(darkPath))
            {
                Image.TryReadFitsFile(darkPath, out var dark).ShouldBeTrue($"could not read {darkPath}");
                var (_, dw, dh) = dark.Shape;
                Line("");
                Line($"cross-check against our dark-derived mask: {Path.GetFileName(darkPath)} ({dw}x{dh})");
                if (dw != w || dh != h)
                {
                    Line("  geometry differs from the maps; not comparable");
                }
                else
                {
                    var ours = BadPixelDetection.BuildMaskFromDark(dark, 8f);
                    if (ours is { Length: > 0 })
                    {
                        var m = ours[0];
                        var oursTotal = 0;
                        var byK = new int[accepted + 1];
                        for (var y = 0; y < h; y++)
                        {
                            for (var x = 0; x < w; x++)
                            {
                                if (!m[y, x]) { continue; }
                                oursTotal++;
                                byK[Math.Min(counts[y * w + x], accepted)]++;
                            }
                        }
                        Line($"  ours@sigma8: {oursTotal:N0} px");
                        Line("    K | our px | % of ours   (high K = APP agrees repeatedly)");
                        var cumulative = 0;
                        for (var k = accepted; k >= 0; k--)
                        {
                            cumulative += byK[k];
                            Line($"  {k,3} | {byK[k],6:N0} | {(oursTotal > 0 ? byK[k] * 100.0 / oursTotal : 0),6:F2}%" +
                                 $"   (>= K: {cumulative,6:N0}, {(oursTotal > 0 ? cumulative * 100.0 / oursTotal : 0),6:F2}%)");
                        }
                    }

                    // HOW FAITHFULLY CAN WE REGENERATE THE MAP OURSELVES? Two reference sets fall
                    // out of the survey and neither depends on our detector, so sweeping sigma
                    // against them is an honest recall/contamination curve rather than a
                    // self-consistency check:
                    //
                    //   CORE  = flagged by every distinct map, across five years and two epochs,
                    //           and 395x enriched in our independent sigma-8 detections. Missing
                    //           one of these is a false negative by any reading.
                    //   NEVER = flagged by no map at all. Not proof of a good pixel, but a pixel
                    //           fifteen independent APP runs never once objected to is the best
                    //           available negative, and flagging it is the cost side of lowering
                    //           sigma.
                    //
                    // What this CANNOT settle: the middle. A pixel in some maps but not all may be
                    // a genuine defect APP missed at another gain, or run noise. So read recall as
                    // real and contamination as an upper bound on harm, never the reverse.
                    var coreSet = new bool[w * h];
                    var coreCount = 0;
                    var neverCount = 0;
                    for (var i = 0; i < counts.Length; i++)
                    {
                        if (counts[i] >= accepted) { coreSet[i] = true; coreCount++; }
                        else if (counts[i] == 0) { neverCount++; }
                    }

                    var sweep = (Environment.GetEnvironmentVariable("TIANWEN_BPM_SWEEP")
                                 ?? "8;6;5;4;3;2.5;2;1.5;1")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                        .ToArray();

                    Line("");
                    Line($"regeneration fidelity vs CORE ({coreCount:N0} px unanimous) " +
                         $"and NEVER ({neverCount:N0} px in no map)");
                    Line("  sigma |  flagged |  of CORE found |  recall | landed in NEVER | of flagged");
                    foreach (var sigma in sweep)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Capture the detector's own per-iteration median / MAD / threshold. The
                        // sweep showed the same nominal sigma behaving completely differently on
                        // two darks from the SAME sensor at different gain, so the question is no
                        // longer which sigma to pick but what scale sigma is being multiplied by.
                        var trace = Environment.GetEnvironmentVariable("TIANWEN_BPM_TRACE") is { Length: > 0 }
                            ? new CapturingLogger(Line, $"sigma {sigma:F2}")
                            : null;
                        var sweepMask = BadPixelDetection.BuildMaskFromDark(dark, sigma, trace);
                        if (sweepMask is not { Length: > 0 }) { continue; }
                        var sm = sweepMask[0];
                        int flagged = 0, inCore = 0, inNever = 0;
                        for (var y = 0; y < h; y++)
                        {
                            for (var x = 0; x < w; x++)
                            {
                                if (!sm[y, x]) { continue; }
                                flagged++;
                                var i = y * w + x;
                                if (coreSet[i]) { inCore++; }
                                else if (counts[i] == 0) { inNever++; }
                            }
                        }
                        Line($"  {sigma,5:F1} | {flagged,8:N0} | {inCore,14:N0} | " +
                             $"{(coreCount > 0 ? inCore * 100.0 / coreCount : 0),6:F2}% | " +
                             $"{inNever,15:N0} | {(flagged > 0 ? inNever * 100.0 / flagged : 0),6:F2}%");
                    }
                }
            }

            await WriteReportAsync(ct);
        }

        /// <summary>The most common sample value, which is the 'linear' (good) level. Decoded rather
        /// than assumed: the maps are BITPIX 8 and the reader normalises, so the literal 127 is not
        /// what comes back, and a guess at the polarity selects the complement of the defects.</summary>
        private static float DominantLevel(float[,] plane, int w, int h)
        {
            var histogram = new Dictionary<float, int>();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var v = plane[y, x];
                    histogram[v] = histogram.TryGetValue(v, out var n) ? n + 1 : 1;
                }
            }
            return histogram.OrderByDescending(kv => kv.Value).First().Key;
        }

        private static string Rel(string root, string path)
            => Path.GetRelativePath(root, path);

        /// <summary>Relays the detector's own Debug/Information lines into the report, so the
        /// converged median / MAD / threshold can be read per sigma instead of inferred from the
        /// flagged count.</summary>
        private sealed class CapturingLogger(Action<string> sink, string prefix) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink($"      [{prefix}] {formatter(state, exception)}");
        }

        private async Task WriteReportAsync(CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_BPM_REPORT") is { Length: > 0 } path)
            {
                await File.WriteAllTextAsync(path, _log.ToString(), ct);
            }
        }
    }
}

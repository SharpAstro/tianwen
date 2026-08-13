using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated sweep of <see cref="BadPixelDetection.BuildMaskFromDark"/> across sigma, optionally
    /// cross-checked against an Astro Pixel Processor bad-pixel map for the same sensor.
    ///
    /// <para><b>This costs seconds, not minutes.</b> Building the mask is one pass over ONE master
    /// dark; it has nothing to do with registering or integrating frames. An earlier plan to sweep
    /// sigma by re-running the whole register-and-integrate A/B was simply wrong about where the
    /// cost is.</para>
    ///
    /// <para><b>The question it settles.</b> A dark-derived mask at sigma 8 removed only a third of
    /// the drizzled hot-pixel clusters from a real session, and six survived byte-identically,
    /// meaning they were never flagged. Two explanations: the threshold is too high (fixable by
    /// lowering sigma) or those pixels are simply not hot in THAT dark, which is 120s/-10C against
    /// 60s/-5C lights (not fixable by any threshold). Sweeping sigma separates them. APP's own map
    /// for this sensor uses kappa 3.00 and flags 2.80% of pixels from 110 darks plus 40 flats, so it
    /// is a useful upper reference for how aggressive is reasonable.</para>
    ///
    /// <para><b>APP row order is a trap.</b> Its maps carry <c>ROWORDER = 'TOP-DOWN'</c>. Compared
    /// without accounting for that, a map lands vertically mirrored, which masks good pixels and
    /// misses every real defect while looking superficially plausible. The comparison below reports
    /// overlap BOTH ways round precisely so that a flip cannot pass unnoticed.</para>
    ///
    /// <para>Set <c>TIANWEN_BPM_DARK</c> to a master dark FITS. Optional: <c>TIANWEN_BPM_APP</c> to
    /// an APP <c>BPM-*.fits</c> for the same sensor, and <c>TIANWEN_BPM_SIGMAS</c> (default
    /// "8;5;4;3;2").</para>
    /// </summary>
    public sealed class BadPixelSigmaProbe(ITestOutputHelper output)
    {
        private readonly System.Text.StringBuilder _log = new();

        /// <summary>Writes to the xUnit sink AND to <c>TIANWEN_BPM_REPORT</c> when set. The sink
        /// alone is impractical here: at the verbosity that surfaces it, the SDK build log buries
        /// the result under hundreds of kilobytes of compiler command lines.</summary>
        private void Line(string text)
        {
            output.WriteLine(text);
            _log.AppendLine(text);
        }

        [Fact]
        public async Task SweepSigmaAndCompareAgainstAppMap()
        {
            var darkPath = Environment.GetEnvironmentVariable("TIANWEN_BPM_DARK");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(darkPath), "TIANWEN_BPM_DARK not set");
            File.Exists(darkPath).ShouldBeTrue($"missing dark: {darkPath}");

            var ct = TestContext.Current.CancellationToken;
            await Task.Yield();

            Image.TryReadFitsFile(darkPath!, out var dark).ShouldBeTrue($"could not read {darkPath}");
            var (channels, w, h) = dark.Shape;
            var totalPx = (long)w * h * channels;
            Line($"dark {Path.GetFileName(darkPath)}  {w}x{h}x{channels}  {totalPx:N0} px");

            var sigmas = (Environment.GetEnvironmentVariable("TIANWEN_BPM_SIGMAS") ?? "8;5;4;3;2")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();

            Line("");
            Line("  sigma |    flagged |  % of frame");
            foreach (var sigma in sigmas)
            {
                ct.ThrowIfCancellationRequested();
                var mask = BadPixelDetection.BuildMaskFromDark(dark, sigma);
                var count = BadPixelDetection.CountMaskedPixels(mask, w, h);
                Line($"  {sigma,5:F1} | {count,10:N0} | {count / (double)totalPx * 100,10:F3}%");
            }

            if (Environment.GetEnvironmentVariable("TIANWEN_BPM_APP") is { Length: > 0 } appPath && File.Exists(appPath))
            {
                Image.TryReadFitsFile(appPath, out var app).ShouldBeTrue($"could not read {appPath}");
                var (appCh, appW, appH) = app.Shape;
                Line("");
                Line($"APP map {Path.GetFileName(appPath)}  {appW}x{appH}x{appCh}");
                if (appW != w || appH != h)
                {
                    Line("  geometry differs from the dark; not comparable");
                    await WriteReportAsync(ct);
                    return;
                }

                // DECODE THE ENCODING RATHER THAN ASSUME IT. A first pass took non-zero to mean
                // "bad" and reported 100% of the plane flagged; the 14 pixels it excluded turned out
                // to match the header's NCOLDPIX exactly, so 0 means COLD and the bulk of the frame
                // sits at some mid value. BITPIX is 8, and the reader normalises, so histogram the
                // distinct levels and let the header's NHOTPIX / NCOLDPIX / NLINPIX identify which
                // level is which. Guessing here would mask the COMPLEMENT of the real defects, which
                // is both catastrophic and invisible.
                var plane = app.GetChannelArray(0);
                var histogram = new System.Collections.Generic.Dictionary<float, int>();
                for (var y = 0; y < appH; y++)
                {
                    for (var x = 0; x < appW; x++)
                    {
                        var v = plane[y, x];
                        histogram[v] = histogram.TryGetValue(v, out var n) ? n + 1 : 1;
                    }
                }
                Line($"  distinct levels: {histogram.Count}");
                foreach (var kv in histogram.OrderByDescending(k => k.Value).Take(8))
                {
                    Line($"    value {kv.Key,10:F6} x {kv.Value,10:N0}  ({kv.Value / (double)(appW * appH) * 100,7:F3}%)");
                }
                Line("  header says NBADPIX 253,249 / NHOTPIX 253,235 / NCOLDPIX 14 / NLINPIX 8,794,815");

                // "Bad" is whatever level is NOT the dominant (linear) one.
                var linearLevel = histogram.OrderByDescending(k => k.Value).First().Key;
                var appSet = 0;
                for (var y = 0; y < appH; y++)
                {
                    for (var x = 0; x < appW; x++)
                    {
                        if (plane[y, x] != linearLevel) { appSet++; }
                    }
                }
                Line($"  APP flagged: {appSet:N0} ({appSet / (double)(appW * appH) * 100:F3}% of one plane)");

                var ours = BadPixelDetection.BuildMaskFromDark(dark, sigmas[0]);
                if (ours is { Length: > 0 })
                {
                    var m = ours[0];
                    int same = 0, flipped = 0, oursSet = 0;
                    for (var y = 0; y < h; y++)
                    {
                        for (var x = 0; x < w; x++)
                        {
                            if (!m[y, x]) { continue; }
                            oursSet++;
                            if (plane[y, x] > 0f) { same++; }
                            if (plane[h - 1 - y, x] > 0f) { flipped++; }
                        }
                    }
                    Line($"  ours@sigma{sigmas[0]:F0} ch0: {oursSet:N0} flagged");
                    Line($"    also flagged by APP, same row order : {same:N0} ({(oursSet > 0 ? same * 100.0 / oursSet : 0):F1}%)");
                    Line($"    also flagged by APP, rows flipped   : {flipped:N0} ({(oursSet > 0 ? flipped * 100.0 / oursSet : 0):F1}%)");
                    Line("    (the LARGER of those two is the correct orientation)");
                }
            }

            await WriteReportAsync(ct);
        }

        private async Task WriteReportAsync(System.Threading.CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_BPM_REPORT") is { Length: > 0 } path)
            {
                await File.WriteAllTextAsync(path, _log.ToString(), ct);
            }
        }
    }
}

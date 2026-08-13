using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated census of sub-PSF sharp sources in the RETAINED SESSION MASTERS, i.e. the
    /// surviving bad-pixel residue measured where it actually costs something.
    ///
    /// <para><b>Why measure in the stack rather than in the dark.</b> Everything upstream is a
    /// proxy: a dark says which pixels look hot at one gain, temperature and exposure, and an APP
    /// map says which pixels some other program flagged. The stacked master is the artifact the
    /// training set is built from, so a residue that survives INTO it is the only failure that is
    /// definitionally real, and a fix that does not reduce this count has not worked no matter how
    /// good its recall looked against a reference set.</para>
    ///
    /// <para><b>What the signature is.</b> A star is a PSF: several pixels wide, and the same width
    /// as its neighbours across the frame. A hot pixel is a delta. After registration it lands at a
    /// different canvas position in every sub, so integration WITHOUT per-cell rejection (which is
    /// exactly what Bayer drizzle has none of, by design) smears one defective sensor pixel into a
    /// small constellation of sharp specks whose relative offsets are the DITHER PATTERN and are
    /// therefore identical everywhere in the frame. So the count of sources measurably sharper than
    /// the frame's own PSF is the residue, and it is per CHANNEL, because a defect sits at one
    /// Bayer position and surfaces in one colour (the observed clusters were red-only and
    /// green-only).</para>
    ///
    /// <para><b>The threshold is relative to the frame, never absolute.</b> These masters span
    /// several optical trains, from a 24 mm lens to a 250 mm scope, so a fixed HFD cut would call
    /// an entire well-sampled frame defective and find nothing in an undersampled one. The cut is a
    /// fraction of the frame's own median HFD.</para>
    ///
    /// <para>Set <c>TIANWEN_RESIDUAL_MASTERS</c> to a directory of masters (e.g. the dataset's
    /// <c>session-masters</c>). Optional: <c>TIANWEN_RESIDUAL_MAX</c> to cap how many are scanned,
    /// <c>TIANWEN_RESIDUAL_FRAC</c> for the sharpness cut (default 0.55 of median HFD), and
    /// <c>TIANWEN_RESIDUAL_REPORT</c> for a report file.</para>
    /// </summary>
    public sealed class StackedResidualProbe(ITestOutputHelper output)
    {
        private readonly System.Text.StringBuilder _log = new();

        private void Line(string text)
        {
            output.WriteLine(text);
            _log.AppendLine(text);
        }

        [Fact]
        public async Task CountSubPsfResidueInRetainedMasters()
        {
            var dir = Environment.GetEnvironmentVariable("TIANWEN_RESIDUAL_MASTERS");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "TIANWEN_RESIDUAL_MASTERS not set");
            Directory.Exists(dir).ShouldBeTrue($"missing master directory: {dir}");

            var ct = TestContext.Current.CancellationToken;

            var frac = float.Parse(
                Environment.GetEnvironmentVariable("TIANWEN_RESIDUAL_FRAC") ?? "0.55",
                CultureInfo.InvariantCulture);
            var max = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_RESIDUAL_MAX"), out var m)
                ? m : int.MaxValue;
            var concentration = Environment.GetEnvironmentVariable("TIANWEN_RESIDUAL_OFFSETS") is { Length: > 0 };

            var files = Directory.EnumerateFiles(dir!, "*.fits", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToArray();
            files.Length.ShouldBeGreaterThan(0, "no masters found");

            Line($"masters   : {files.Length}   sharpness cut: HFD < {frac:F2} x frame median HFD");
            Line("");
            Line("  sharp |  total | share |  medHFD | ch | session");

            var perFile = new List<(string Name, int Sharp, int Total, double Share)>();

            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!Image.TryReadFitsFile(path, out var master))
                {
                    Line($"  UNREADABLE                                            | {Path.GetFileName(path)}");
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                var (channels, frameW, frameH) = master.Shape;
                for (var c = 0; c < channels; c++)
                {
                    ct.ThrowIfCancellationRequested();
                    var stars = await master.FindStarsAsync(channel: c, snrMin: 10f, cancellationToken: ct);
                    if (stars.Count < 20)
                    {
                        continue;
                    }

                    // Median HFD of THIS channel of THIS frame is the PSF scale everything is
                    // judged against, so an undersampled wide-field and an oversampled long
                    // focal length are both measured against their own optics.
                    var medianHfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
                    if (!(medianHfd > 0f))
                    {
                        continue;
                    }

                    var cut = medianHfd * frac;
                    var sharp = 0;
                    var sharpPts = new List<(float X, float Y)>();
                    var normalPts = new List<(float X, float Y)>();
                    foreach (var star in stars)
                    {
                        if (star.HFD < cut)
                        {
                            sharp++;
                            sharpPts.Add((star.XCentroid, star.YCentroid));
                        }
                        else
                        {
                            normalPts.Add((star.XCentroid, star.YCentroid));
                        }
                    }

                    // IS THIS RESIDUE, OR JUST FAINT SOURCES? A sharp source being sharp proves
                    // nothing on its own. One defective sensor pixel lands at a different canvas
                    // position in every sub, so integration without per-cell rejection smears it
                    // into a tight GROUP of specks. The measurable consequence is CLUSTERING, and
                    // the baseline is analytic: N points spread uniformly at random over the frame
                    // yield N(N-1)/2 * (2r)^2/Area near pairs. Measured over the worst channels,
                    // sharp sources run ~98x that baseline while a size-matched sample of ordinary
                    // stars from the same frames sits at ~0.5x, i.e. at random. So the sharp
                    // population is real residue and not faint stars, cosmic rays or noise.
                    //
                    // The size-matched star control is reported too, and needed: the sharp set is
                    // its whole population while the control is a subsample of a much larger one,
                    // so density is NOT matched between them and only the analytic baseline makes
                    // the two comparable.
                    //
                    // WHAT DOES NOT WORK, recorded so it is not retried: the offsets themselves do
                    // NOT repeat across the frame, so the most-common-offset share is useless here
                    // (1.3% for sharp against 33% for a control on one or two pairs). The intuition
                    // that they should repeat assumes registration is pure translation. It is a
                    // full affine, and under any field rotation the displacement between two specks
                    // of one defect depends on where in the frame that defect sits. There is
                    // therefore no global dither vector to find, which also means a
                    // registration-derived defect map has to back-project through the PER-FRAME
                    // transforms and cannot shortcut through one offset.
                    if (concentration)
                    {
                        var sharpConc = OffsetConcentration(sharpPts);
                        var normalConc = OffsetConcentration(Sample(normalPts, sharpPts.Count));
                        var n = sharpPts.Count;
                        var expected = n * (n - 1) / 2.0 * (2 * PairRadius * 2 * PairRadius) / (frameW * (double)frameH);
                        var ratio = expected > 0 ? sharpConc.Pairs / expected : 0;
                        var controlRatio = expected > 0 ? normalConc.Pairs / expected : 0;
                        Line($"        clustering  sharp {sharpConc.Pairs,6:N0} pairs = {ratio,7:F1}x random" +
                             $"   control stars {normalConc.Pairs,5:N0} = {controlRatio,5:F1}x random" +
                             $"   (top offset {sharpConc.Top:P1} on {sharpConc.Vector}, not meaningful)");
                    }

                    var share = stars.Count > 0 ? sharp * 100.0 / stars.Count : 0;
                    Line($"  {sharp,5:N0} | {stars.Count,6:N0} | {share,4:F1}% | {medianHfd,7:F2} | {c,2} | {Truncate(name, 58)}");
                    perFile.Add((name + $" ch{c}", sharp, stars.Count, share));
                }
            }

            if (perFile.Count == 0)
            {
                Line("no channel yielded enough sources to measure");
                await WriteReportAsync(ct);
                return;
            }

            var totalSharp = perFile.Sum(p => p.Sharp);
            var totalSources = perFile.Sum(p => p.Total);
            var shares = perFile.Select(p => p.Share).OrderBy(v => v).ToArray();

            Line("");
            Line($"channels measured : {perFile.Count}");
            Line($"sharp sources     : {totalSharp:N0} of {totalSources:N0} ({totalSharp * 100.0 / totalSources:F2}%)");
            Line($"per-channel share : median {shares[shares.Length / 2]:F2}%, " +
                 $"p90 {shares[(int)(shares.Length * 0.9)]:F2}%, max {shares[^1]:F2}%");
            Line("");
            Line("worst 15 channels (these are the masters to look at first):");
            foreach (var p in perFile.OrderByDescending(p => p.Sharp).Take(15))
            {
                Line($"  {p.Sharp,5:N0} sharp of {p.Total,6:N0} ({p.Share,4:F1}%)  {Truncate(p.Name, 70)}");
            }

            await WriteReportAsync(ct);
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

        /// <summary>Radius within which two specks can plausibly belong to the same smeared sensor
        /// pixel. The observed clusters spanned roughly 36x25 px, so 60 comfortably contains one
        /// while excluding most unrelated pairs.</summary>
        private const float PairRadius = 60f;

        /// <summary>
        /// Fraction of near pairs sharing the single most common integer offset. High means the
        /// points repeat a fixed displacement, which is the dither signature; near-zero means the
        /// points are positionally independent.
        /// </summary>
        private static (double Top, string Vector, int Pairs) OffsetConcentration(List<(float X, float Y)> pts)
        {
            if (pts.Count < 8)
            {
                return (0, "n/a", 0);
            }

            var histogram = new Dictionary<(int, int), int>();
            var pairs = 0;
            for (var i = 0; i < pts.Count; i++)
            {
                for (var j = i + 1; j < pts.Count; j++)
                {
                    var dx = pts[j].X - pts[i].X;
                    var dy = pts[j].Y - pts[i].Y;
                    if (MathF.Abs(dx) > PairRadius || MathF.Abs(dy) > PairRadius)
                    {
                        continue;
                    }
                    // Canonical direction, so A->B and B->A are the same displacement.
                    if (dy < 0 || (dy == 0 && dx < 0)) { dx = -dx; dy = -dy; }
                    var key = ((int)MathF.Round(dx), (int)MathF.Round(dy));
                    if (key is (0, 0)) { continue; }
                    histogram[key] = histogram.TryGetValue(key, out var n) ? n + 1 : 1;
                    pairs++;
                }
            }

            if (pairs == 0)
            {
                return (0, "none", 0);
            }
            var best = histogram.OrderByDescending(kv => kv.Value).First();
            return (best.Value / (double)pairs, $"({best.Key.Item1,3},{best.Key.Item2,3})", pairs);
        }

        /// <summary>Deterministic even-stride subsample, so the control set matches the sharp set in
        /// SIZE (pair counts scale quadratically, so an unmatched control is not comparable) without
        /// a random source, which workflow scripts cannot use anyway.</summary>
        private static List<(float X, float Y)> Sample(List<(float X, float Y)> pts, int n)
        {
            if (n <= 0 || pts.Count <= n)
            {
                return pts;
            }
            var stride = pts.Count / (double)n;
            var result = new List<(float X, float Y)>(n);
            for (var i = 0; i < n; i++)
            {
                result.Add(pts[(int)(i * stride)]);
            }
            return result;
        }

        private async Task WriteReportAsync(CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("TIANWEN_RESIDUAL_REPORT") is { Length: > 0 } path)
            {
                await File.WriteAllTextAsync(path, _log.ToString(), ct);
            }
        }
    }
}

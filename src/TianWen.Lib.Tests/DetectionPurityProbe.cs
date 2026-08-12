using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated diagnostic measuring <b>detection purity</b>: the fraction of a frame's top-K
    /// detections that reproduce on the next sub. Skips with no env var set, so a bare
    /// <c>dotnet test</c> stays green; same posture as <see cref="PhotometricRepeatabilityProbe"/>.
    ///
    /// <para><b>Why this exists.</b> The dataset builder moved star detection off a colour-debayered
    /// channel and onto the pre-debayer luminance, and capped its quad set at the bright end, on the
    /// strength of exactly this statistic: on Helix 2025-08-09 only 7.8% of red-plane detections
    /// reproduced on the next sub against 31.8% mono, and the session registered 0 of 316 subs. The
    /// stacking pipeline still detects the old way and nobody has measured whether it has the same
    /// problem. Star and quad COUNTS are the symptom; this is the cause, and it is what decides
    /// whether porting is worth doing.</para>
    ///
    /// <para><b>Why depth matters and the table has columns.</b> A quad matches only when the same
    /// four stars form it in BOTH frames, so with a fraction p of the top-K detections real, at most
    /// about p^4 of quads can match. p falls with depth (measured on Helix: 68% at top-50, 59% at
    /// top-100, 41% at top-200, 32% over all 601), which is why the fix was as much about capping
    /// QuadStars as about where to detect. Read the p^4 column, not p: that is the quantity
    /// registration actually spends.</para>
    ///
    /// <para><b>ALWAYS RUN A NEGATIVE CONTROL, or the deep rows are meaningless.</b> These are
    /// uncalibrated subs, so fixed-pattern noise sits at identical sensor positions in every frame,
    /// reproduces at ~100% by construction, and being the majority of a faint-end detection list it
    /// pins the median offset at exactly (0.00, 0.00) no matter how far the sky moved. Measured on
    /// HIP 42861 (ASI533MC, 2025-12-28): pairing two frames of DIFFERENT TARGETS, where nothing real
    /// can correspond, still scored mono p = 90.5% at "all" depth against 94.7% for genuine
    /// consecutive subs. Those are indistinguishable, so the "all" row carries no information.
    /// <b>The same control separates cleanly at the bright end</b> (control 21% / 17% / 23% at
    /// top-100 / 200 / 500 against 92% / 93% / 93% genuine), because the contamination lives in the
    /// faint tail. So read the top-K rows, treat roughly 20% as the false-match floor, and discard
    /// "all". A near-zero offset is the tell that this is happening; over a 43-minute baseline it
    /// stayed exactly zero, which no real field does.</para>
    ///
    /// <para>Set <c>TIANWEN_PURITY_DIR</c> to a folder of session subs (first
    /// <c>TIANWEN_PURITY_COUNT</c> by name, default 6), or <c>TIANWEN_PURITY_SUBS</c> to an explicit
    /// semicolon-separated list. Optionally <c>TIANWEN_PURITY_OUT</c> to write the markdown table.</para>
    /// </summary>
    public sealed class DetectionPurityProbe(ITestOutputHelper output)
    {
        private const string DirVar = "TIANWEN_PURITY_DIR";
        private const string SubsVar = "TIANWEN_PURITY_SUBS";
        private const string CountVar = "TIANWEN_PURITY_COUNT";
        private const string OutVar = "TIANWEN_PURITY_OUT";

        /// <summary>Depths to report p at. The last entry means "every detection".</summary>
        private static readonly int[] Depths = [50, 100, 200, 500, int.MaxValue];

        [Fact]
        public async Task MeasureDetectionPurityBothWays()
        {
            var paths = ResolvePaths();
            Assert.SkipWhen(paths.Length == 0, $"neither {DirVar} nor {SubsVar} set");
            paths.Length.ShouldBeGreaterThanOrEqualTo(2, "need at least two subs");

            var ct = TestContext.Current.CancellationToken;
            var sb = new StringBuilder();
            sb.AppendLine("# Detection purity: pre-debayer luminance vs debayered channel 0");
            sb.AppendLine();
            sb.AppendLine("`p` = fraction of the top-K detections that reproduce on the next sub.");
            sb.AppendLine("`p^4` is the ceiling on quad match rate, which is what registration spends.");
            sb.AppendLine();

            var mono = new List<ImagedStar[]>(paths.Length);
            var colour = new List<ImagedStar[]>(paths.Length);

            foreach (var path in paths)
            {
                File.Exists(path).ShouldBeTrue($"missing: {path}");
                Image.TryReadFitsFile(path, out var image).ShouldBeTrue($"could not read {path}");

                // ORDER IS LOAD-BEARING and mirrors both pipelines: DebayerAsync can rescale its input
                // in place, so it participates in what a subsequent pre-debayer detection sees. The
                // dataset path debayers first and then detects on the pre-debayer image, and its
                // 314/314 result was measured with the debayer present; doing it the other way round
                // here would measure something neither pipeline runs.
                var debayered = await image.DebayerAsync(DebayerAlgorithm.VNG, cancellationToken: ct);
                var monoStars = await Collect(image, ct);      // dataset today (BilinearMono path)
                var colourStars = await Collect(debayered, ct); // stacking today (interpolated red)

                mono.Add(monoStars);
                colour.Add(colourStars);
                output.WriteLine($"{Path.GetFileName(path)}: mono={monoStars.Length} colour={colourStars.Length}");
                // HFD separates a star from a hot pixel, and on UNCALIBRATED subs that is the whole
                // ballgame: fixed-pattern noise sits at identical sensor positions in every frame, so
                // it reproduces at ~100% and drags the offset estimate to zero, manufacturing a high
                // purity score out of nothing. A hot pixel is 1-2 px and piles up against
                // FindStarsAsync's HFD > 0.8 floor; a real star in these fields runs 2.5-3 px. If the
                // p50 sits near the floor, the p above is measuring the sensor, not the sky.
                sb.AppendLine($"- `{Path.GetFileName(path)}`: mono {monoStars.Length} detections (HFD p50 {Hfd(monoStars):F2}, share under 1.5 px {NarrowShare(monoStars) * 100:F0}%), " +
                    $"debayered-ch0 {colourStars.Length} (HFD p50 {Hfd(colourStars):F2}, under 1.5 px {NarrowShare(colourStars) * 100:F0}%)");
            }

            sb.AppendLine();
            var measured = false;

            for (var i = 0; i + 1 < paths.Length; i++)
            {
                var label = $"{Path.GetFileName(paths[i])} -> {Path.GetFileName(paths[i + 1])}";
                sb.AppendLine($"## {label}");
                sb.AppendLine();
                sb.AppendLine("| mode | depth | stars | matched | p | p^4 | dither px |");
                sb.AppendLine("|---|---|---|---|---|---|---|");

                foreach (var (name, lists) in new[] { ("pre-debayer mono", mono), ("debayered ch0", colour) })
                {
                    foreach (var depth in Depths)
                    {
                        var a = TopByFlux(lists[i], depth);
                        var b = TopByFlux(lists[i + 1], depth);
                        if (a.Length < 2 || b.Length < 2)
                        {
                            continue;
                        }

                        var result = PhotometricRepeatability.Compare(a, b);
                        var depthLabel = depth == int.MaxValue ? "all" : depth.ToString();
                        if (result is null)
                        {
                            sb.AppendLine($"| {name} | {depthLabel} | {a.Length} | - | no match | - | - |");
                            continue;
                        }

                        measured = true;
                        // Denominator is the SMALLER list: p asks what fraction of what was detected
                        // could reproduce, and a frame that detected fewer cannot be blamed for the
                        // other frame's extras.
                        var denom = Math.Min(a.Length, b.Length);
                        var p = denom > 0 ? result.MatchedStars / (double)denom : double.NaN;
                        var dither = $"({result.OffsetX:F2}, {result.OffsetY:F2})";
                        sb.AppendLine(
                            $"| {name} | {depthLabel} | {denom} | {result.MatchedStars} | {p * 100:F1}% | {Math.Pow(p, 4) * 100:F1}% | {dither} |");
                        output.WriteLine($"  {name,-18} depth={depthLabel,-4} p={p * 100:F1}% p^4={Math.Pow(p, 4) * 100:F1}% dither={dither}");
                    }
                }
                sb.AppendLine();
            }

            measured.ShouldBeTrue("no pair produced a measurement");

            if (Environment.GetEnvironmentVariable(OutVar) is { Length: > 0 } outPath)
            {
                await File.WriteAllTextAsync(outPath, sb.ToString(), ct);
                output.WriteLine($"wrote {outPath}");
            }
        }

        /// <summary>Same detection parameters both pipelines pass, so the probe measures the detect
        /// SITE and nothing else.</summary>
        private static async Task<ImagedStar[]> Collect(Image image, System.Threading.CancellationToken ct)
        {
            var stars = await image.FindStarsAsync(channel: 0, snrMin: 5f, minStars: 2000, cancellationToken: ct);
            var collected = new List<ImagedStar>();
            foreach (var star in stars)
            {
                collected.Add(star);
            }
            return [.. collected];
        }

        private static float Hfd(ImagedStar[] stars)
        {
            if (stars.Length == 0) { return float.NaN; }
            var hfds = stars.Select(s => s.HFD).OrderBy(v => v).ToArray();
            return hfds[hfds.Length / 2];
        }

        /// <summary>Share of detections tight enough to be fixed-pattern rather than a star.</summary>
        private static double NarrowShare(ImagedStar[] stars)
            => stars.Length == 0 ? double.NaN : stars.Count(s => s.HFD < 1.5f) / (double)stars.Length;

        private static ImagedStar[] TopByFlux(ImagedStar[] stars, int depth)
            => depth >= stars.Length ? stars : [.. stars.OrderByDescending(s => s.Flux).Take(depth)];

        private static string[] ResolvePaths()
        {
            if (Environment.GetEnvironmentVariable(SubsVar) is { Length: > 0 } spec)
            {
                return spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            if (Environment.GetEnvironmentVariable(DirVar) is { Length: > 0 } dir && Directory.Exists(dir))
            {
                var count = int.TryParse(Environment.GetEnvironmentVariable(CountVar), out var c) && c > 1 ? c : 6;
                return [.. Directory.EnumerateFiles(dir, "*.fits", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Take(count)];
            }
            return [];
        }
    }
}

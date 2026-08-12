using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated diagnostic that turns <see cref="PhotometricRepeatability"/> loose on real subs, to
    /// produce the numbers the AI photometric gate is calibrated from. Skips with no env var set, so a
    /// bare <c>dotnet test</c> stays green; same posture as <see cref="MasterPsfProbe"/>.
    ///
    /// <para>Set <c>TIANWEN_REPEATABILITY_SUBS</c> to two or more semicolon-separated FITS paths from
    /// the SAME session (consecutive subs are ideal, since anything that drifts over a night is a
    /// confound rather than pipeline scatter). Optionally set <c>TIANWEN_REPEATABILITY_OUT</c> to
    /// write the table to a file. Every consecutive pair is compared, so N paths give N-1 pairs.</para>
    ///
    /// <para><b>These are raw subs, so the number is an upper bound on the pipeline's scatter</b>, and
    /// deliberately so: a gate calibrated on an upper bound is permissive rather than falsely strict,
    /// and a gate that blocks a good model is the worse failure. Flat and dark correction divide out
    /// of a flux RATIO between two frames of one session anyway, since both frames carry the same
    /// calibration.</para>
    /// </summary>
    public sealed class PhotometricRepeatabilityProbe(ITestOutputHelper output)
    {
        private const string SubsVar = "TIANWEN_REPEATABILITY_SUBS";
        private const string OutVar = "TIANWEN_REPEATABILITY_OUT";

        [Fact]
        public async Task MeasureSubToSubRepeatability()
        {
            var spec = Environment.GetEnvironmentVariable(SubsVar);
            Assert.SkipWhen(string.IsNullOrWhiteSpace(spec), $"{SubsVar} not set");

            var paths = spec!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            paths.Length.ShouldBeGreaterThanOrEqualTo(2, $"{SubsVar} needs at least two paths");

            var ct = TestContext.Current.CancellationToken;
            var sb = new StringBuilder();
            sb.AppendLine("# Sub-to-sub photometric repeatability");
            sb.AppendLine();

            var starLists = new List<ImagedStar[]>(paths.Length);
            foreach (var path in paths)
            {
                File.Exists(path).ShouldBeTrue($"missing: {path}");
                Image.TryReadFitsFile(path, out var image).ShouldBeTrue($"could not read {path}");

                // Channel 0 for both frames: the comparison only has to be CONSISTENT between the two
                // frames, and a Bayer mosaic's channel 0 is a real photometric plane. Cross-channel
                // comparison is a different question (see SessionPsf.MasterProfiles).
                var stars = await image.FindStarsAsync(
                    channel: 0, snrMin: 5f, maxStars: 3000, cancellationToken: ct);
                var collected = new List<ImagedStar>();
                foreach (var star in stars)
                {
                    collected.Add(star);
                }
                var array = collected.ToArray();
                starLists.Add(array);
                output.WriteLine($"{Path.GetFileName(path)}: {array.Length} stars");
                sb.AppendLine($"- `{Path.GetFileName(path)}`: {array.Length} stars detected");
            }

            sb.AppendLine();

            var anyPair = false;
            for (var i = 0; i + 1 < starLists.Count; i++)
            {
                var result = PhotometricRepeatability.Compare(starLists[i], starLists[i + 1]);
                var label = $"{Path.GetFileName(paths[i])} vs {Path.GetFileName(paths[i + 1])}";

                if (result is null)
                {
                    output.WriteLine($"{label}: no match (non-overlapping or too sparse)");
                    sb.AppendLine($"## {label}");
                    sb.AppendLine();
                    sb.AppendLine("No match: frames do not overlap, or the field is too sparse to measure.");
                    sb.AppendLine();
                    continue;
                }

                anyPair = true;
                output.WriteLine(
                    $"{label}: matched {result.MatchedStars}, dither ({result.OffsetX:F2}, {result.OffsetY:F2}) px");
                sb.AppendLine($"## {label}");
                sb.AppendLine();
                sb.AppendLine($"- Matched stars: {result.MatchedStars}");
                sb.AppendLine($"- Dither removed: ({result.OffsetX:F2}, {result.OffsetY:F2}) px");
                sb.AppendLine();
                sb.AppendLine("| SNR band | stars | flux dp50 | flux dp95 | flux bias p50 | shift p50 px | shift p95 px |");
                sb.AppendLine("|---|---|---|---|---|---|---|");

                foreach (var band in result.Bands)
                {
                    var high = float.IsPositiveInfinity(band.SnrHigh) ? "inf" : band.SnrHigh.ToString("F0");
                    var row = $"| {band.SnrLow:F0}-{high} | {band.Stars} | {Pct(band.FluxDeltaP50)} | {Pct(band.FluxDeltaP95)} | {Pct(band.FluxBiasP50)} | {Px(band.CentroidShiftP50)} | {Px(band.CentroidShiftP95)} |";
                    sb.AppendLine(row);
                    output.WriteLine(
                        $"  SNR {band.SnrLow:F0}-{high}: n={band.Stars} fluxD50={Pct(band.FluxDeltaP50)} fluxD95={Pct(band.FluxDeltaP95)} shift50={Px(band.CentroidShiftP50)} shift95={Px(band.CentroidShiftP95)}");
                }

                var o = result.Overall;
                sb.AppendLine($"| **all** | {o.Stars} | {Pct(o.FluxDeltaP50)} | {Pct(o.FluxDeltaP95)} | {Pct(o.FluxBiasP50)} | {Px(o.CentroidShiftP50)} | {Px(o.CentroidShiftP95)} |");
                sb.AppendLine();
            }

            anyPair.ShouldBeTrue("no pair produced a measurement");

            if (Environment.GetEnvironmentVariable(OutVar) is { Length: > 0 } outPath)
            {
                await File.WriteAllTextAsync(outPath, sb.ToString(), ct);
                output.WriteLine($"wrote {outPath}");
            }
        }

        private static string Pct(float v) => float.IsNaN(v) ? "n/a" : $"{v * 100f:F2}%";

        private static string Px(float v) => float.IsNaN(v) ? "n/a" : $"{v:F3}";
    }
}

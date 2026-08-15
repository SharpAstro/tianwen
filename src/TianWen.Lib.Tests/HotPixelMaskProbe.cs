using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Env-gated A/B proving that the hot-pixel mask removes the drizzled hot-pixel clusters from a
    /// dataset session master. Skips with no env var set, so a bare <c>dotnet test</c> stays green.
    ///
    /// <para><b>Why not just re-run the CLI.</b> <c>dataset build</c> resolves calibration by
    /// scanning the whole archive, and this archive keeps per-session BIAS/DARK/FLAT folders
    /// scattered across the tree, so a narrowed <c>--archive-root</c> fails to resolve a dark and
    /// <c>--require-dark</c> turns that into a skip; while the full roots would rebake everything.
    /// <c>--exclude-object</c> is a single wildcard, not a list, so it cannot express "all but one".
    /// Driving <see cref="SessionRegistrar.RegisterAsync"/> directly with a hand-built
    /// <see cref="Calibrator"/> isolates exactly the parameter under test and needs no scan.</para>
    ///
    /// <para>Set <c>TIANWEN_BPM_LIGHTS</c> to the session's lights folder and
    /// <c>TIANWEN_BPM_DARK</c> to a master dark FITS. Optional: <c>TIANWEN_BPM_OUT</c> (output dir,
    /// default a temp dir), <c>TIANWEN_BPM_MAXLIGHTS</c> (cap the frame count for a quick run).</para>
    ///
    /// <para><b>Measured 2026-08-15</b> on 2025-12-28 Segaull+Thors_Helmet (ZWO ASI533MC Pro, g121,
    /// 60s) against <c>master_dark_120s_-10C_g121_ZWOASI533MCPro.fits</c>, capped at 100 lights of
    /// which 90 registered, both runs BayerDrizzle. The mask changed <b>2119 px, 0.0077% of the
    /// frame</b>, in 617 clusters spread across all three channels, and the extreme-outlier count
    /// fell 162618 -> 161334. So it is surgical and it moves in the right direction, which is what
    /// the assertions below pin. Point it at a dark for the RIGHT gain: sigma is not portable
    /// between darks, and a mismatched one changes what gets flagged rather than failing loudly.</para>
    /// </summary>
    public sealed class HotPixelMaskProbe(ITestOutputHelper output)
    {
        private const string LightsVar = "TIANWEN_BPM_LIGHTS";
        private const string DarkVar = "TIANWEN_BPM_DARK";
        private const string OutVar = "TIANWEN_BPM_OUT";
        private const string MaxVar = "TIANWEN_BPM_MAXLIGHTS";

        [Fact]
        public async Task MaskedAndUnmaskedMastersDifferOnlyByTheHotPixels()
        {
            var lightsDir = Environment.GetEnvironmentVariable(LightsVar);
            var darkPath = Environment.GetEnvironmentVariable(DarkVar);
            Assert.SkipWhen(string.IsNullOrWhiteSpace(lightsDir) || string.IsNullOrWhiteSpace(darkPath),
                $"{LightsVar} / {DarkVar} not set");

            var ct = TestContext.Current.CancellationToken;
            Directory.Exists(lightsDir).ShouldBeTrue($"missing lights dir: {lightsDir}");
            File.Exists(darkPath).ShouldBeTrue($"missing dark: {darkPath}");

            var outDir = Environment.GetEnvironmentVariable(OutVar) is { Length: > 0 } o
                ? o
                : Path.Combine(Path.GetTempPath(), "tianwen-bpm-probe");
            Directory.CreateDirectory(outDir);

            Image.TryReadFitsFile(darkPath, out var dark).ShouldBeTrue($"could not read dark {darkPath}");
            var calibrator = new Calibrator(Dark: dark);

            var lights = new List<FrameInfo>();
            await foreach (var f in new FitsFolderFrameSource(lightsDir!, recursive: true).EnumerateAsync(ct))
            {
                lights.Add(f);
            }
            lights.Count.ShouldBeGreaterThan(1, "need frames to register");
            if (int.TryParse(Environment.GetEnvironmentVariable(MaxVar), out var cap) && cap > 1 && cap < lights.Count)
            {
                lights = [.. lights.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(cap)];
            }
            output.WriteLine($"{lights.Count} lights, dark {Path.GetFileName(darkPath)}");

            var session = new ImagingSession(
                SessionDir: lightsDir!,
                RelativeDir: "bpm-probe",
                Camera: "probe",
                Target: "probe",
                FilterName: "",
                Lights: [.. lights]);

            // Two runs differing ONLY in hotPixelSigma. Everything else -- frames, calibration,
            // gate, reference pick, strategy -- is identical, so any difference in the masters is
            // attributable to the mask and nothing else.
            var masters = new Dictionary<string, Image>();
            foreach (var (sigma, label) in new[] { (0f, "nomask"), (8f, "masked") })
            {
                var scratch = Path.Combine(outDir, $"scratch_{label}");
                Directory.CreateDirectory(scratch);
                var reg = await SessionRegistrar.RegisterAsync(
                    session, calibrator, scratch,
                    hotPixelSigma: sigma,
                    cancellationToken: ct);
                reg.ShouldNotBeNull($"{label}: session failed to register");

                // Without this the whole probe is vacuous. Only the drizzle strategies read
                // IntegrationJob.BadPixelMask; the staged path's sigma-clip washes hot pixels out
                // across frames on its own, so an AHD fallback would show no difference and the
                // test would "pass" by proving nothing. A capped TIANWEN_BPM_MAXLIGHTS below
                // DrizzleStrategy.AutoSelectMinFrameCount matched frames is the way that happens.
                reg.MasterStrategy.ShouldBe(IntegrationStrategyKind.BayerDrizzle,
                    $"{label}: fell back off drizzle, so the mask is never consulted and this " +
                    $"comparison cannot say anything. Raise {MaxVar} or drop it.");

                var path = Path.Combine(outDir, $"master_{label}.fits");
                reg.Master.WriteToFitsFile(path);
                masters[label] = reg.Master;
                output.WriteLine($"{label}: strategy={reg.MasterStrategy} subs={reg.Subs.Length} -> {path}");
            }

            var nomask = masters["nomask"];
            var masked = masters["masked"];
            masked.Width.ShouldBe(nomask.Width, "canvas differs, so a pixelwise diff is meaningless");
            masked.Height.ShouldBe(nomask.Height);
            masked.ChannelCount.ShouldBe(nomask.ChannelCount);

            var w = nomask.Width;
            var h = nomask.Height;
            var totalFlagged = 0;
            var totalClusters = 0;
            var brightBefore = 0;
            var brightAfter = 0;

            for (var c = 0; c < nomask.ChannelCount; c++)
            {
                var (flagged, clusters, before, after) = CompareChannel(nomask, masked, c, w, h);
                output.WriteLine($"  ch{c}: {flagged} px changed in {clusters} cluster(s); " +
                                 $"bright outliers {before} -> {after}");
                totalFlagged += flagged;
                totalClusters += clusters;
                brightBefore += before;
                brightAfter += after;
            }

            var fraction = totalFlagged / (double)(w * (long)h * nomask.ChannelCount);
            output.WriteLine($"total: {totalFlagged} px ({fraction * 100:F4}% of frame) in " +
                             $"{totalClusters} clusters; bright outliers {brightBefore} -> {brightAfter}");

            // The mask must actually DO something on a session known to carry drizzled hot pixels.
            totalClusters.ShouldBeGreaterThan(0,
                "the masked and unmasked masters are identical, so the mask reached nothing");

            // ...and it must be surgical. A mask that rewrote a large part of the frame would be
            // removing signal, which is the failure mode that matters more than missing a defect.
            fraction.ShouldBeLessThan(0.02,
                $"the mask changed {fraction * 100:F2}% of the frame, which is a blunt instrument, " +
                "not a hot-pixel fix");

            // The direction has to be right: masking removes bright non-stellar residue, so the
            // count of extreme positive outliers can only fall. Stars are identical in both runs
            // and cancel out of the comparison.
            brightAfter.ShouldBeLessThanOrEqualTo(brightBefore,
                "masking INCREASED the extreme-outlier count, which inverts the whole premise");

            output.WriteLine($"both masters in {outDir}");
        }

        /// <summary>
        /// Per-channel diff of the two masters. Returns the changed-pixel count, how many connected
        /// clusters those form, and the extreme-outlier count before and after.
        ///
        /// <para>Thresholds are derived from the MASKED master's own robust scale rather than from
        /// an absolute number, because a drizzled master's units depend on the session. The changed
        /// bar is deliberately well above the noise, because masking a pixel perturbs far more than
        /// that pixel. Drizzle normalises by accumulated weight, so removing ~22k input pixels moves
        /// the flux/weight ratio of every output cell any of them landed in across all the dithered
        /// frames -- 2.9M cells at some magnitude here. A bar at a few MAD would count all of those
        /// and the surgical-ness assertion would be meaningless.</para>
        ///
        /// <para>An earlier version of this comment blamed that spread on drizzle not being
        /// bit-identical run to run. It is bit-identical: two full 5-hour bakes on different commits
        /// produced pixel-identical masters (max |diff| 0.0 over 27.7M finite px, NaN masks equal).
        /// The pipeline is deterministic to the bit, so every difference measured here is mask
        /// consequence and nothing else.</para>
        /// </summary>
        private static (int Flagged, int Clusters, int BrightBefore, int BrightAfter) CompareChannel(
            Image nomask, Image masked, int channel, int w, int h)
        {
            var a = nomask.GetChannelSpan(channel);
            var b = masked.GetChannelSpan(channel);

            // FINITE SAMPLES ONLY. A drizzled master carries NaN wherever a cell got no weight and
            // across the union-canvas margin -- 27,359 px on the measured session. .NET sorts NaN
            // FIRST, ahead of negative infinity, so a median read at length/2 of the raw array is
            // displaced by the NaN fraction and lands below the true median. The bias hits both
            // arms equally so it does not change this probe's verdict, but it makes the reported
            // threshold wrong, and a statistic that silently means a different percentile than its
            // name says is worth more than the two lines it costs to fix.
            var finite = new float[w * h];
            var n = 0;
            for (var i = 0; i < b.Length; i++)
            {
                if (float.IsFinite(b[i]))
                {
                    finite[n++] = b[i];
                }
            }
            if (n == 0)
            {
                return (0, 0, 0, 0);
            }
            var samples = finite.AsSpan(0, n);
            samples.Sort();
            var median = samples[n / 2];
            for (var i = 0; i < n; i++)
            {
                samples[i] = Math.Abs(samples[i] - median);
            }
            samples.Sort();
            var mad = samples[n / 2] + float.Epsilon;

            var changedBar = 8f * mad;
            var brightBar = median + 20f * mad;

            var changed = new bool[w * h];
            var flagged = 0;
            var brightBefore = 0;
            var brightAfter = 0;
            for (var i = 0; i < changed.Length; i++)
            {
                // Every comparison below is false when either operand is NaN, which is the wanted
                // behaviour: an uncovered cell is neither a change nor a bright outlier. Relying on
                // that is fine, but it is deliberate rather than accidental, hence this note.
                if (a[i] - b[i] > changedBar)
                {
                    changed[i] = true;
                    flagged++;
                }
                if (a[i] > brightBar)
                {
                    brightBefore++;
                }
                if (b[i] > brightBar)
                {
                    brightAfter++;
                }
            }

            return (flagged, CountClusters(changed, w, h), brightBefore, brightAfter);
        }

        /// <summary>4-connected component count over a boolean mask, iterative so a frame-sized
        /// blob cannot blow the stack.</summary>
        private static int CountClusters(bool[] mask, int w, int h)
        {
            var seen = new bool[mask.Length];
            var stack = new Stack<int>();
            var clusters = 0;
            for (var start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start])
                {
                    continue;
                }
                clusters++;
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    var x = p % w;
                    var y = p / w;
                    if (x > 0) { Visit(p - 1); }
                    if (x < w - 1) { Visit(p + 1); }
                    if (y > 0) { Visit(p - w); }
                    if (y < h - 1) { Visit(p + w); }
                }

                void Visit(int q)
                {
                    if (mask[q] && !seen[q])
                    {
                        seen[q] = true;
                        stack.Push(q);
                    }
                }
            }
            return clusters;
        }
    }
}

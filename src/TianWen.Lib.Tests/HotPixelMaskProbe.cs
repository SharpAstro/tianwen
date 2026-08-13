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
            foreach (var (sigma, label) in new[] { (0f, "nomask"), (8f, "masked") })
            {
                var scratch = Path.Combine(outDir, $"scratch_{label}");
                Directory.CreateDirectory(scratch);
                var reg = await SessionRegistrar.RegisterAsync(
                    session, calibrator, scratch,
                    hotPixelSigma: sigma,
                    cancellationToken: ct);
                reg.ShouldNotBeNull($"{label}: session failed to register");

                var path = Path.Combine(outDir, $"master_{label}.fits");
                reg.Master.WriteToFitsFile(path);
                output.WriteLine($"{label}: strategy={reg.MasterStrategy} subs={reg.Subs.Length} -> {path}");
            }

            output.WriteLine($"wrote both masters to {outDir}; diff them for hot-pixel clusters");
        }
    }
}

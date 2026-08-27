using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>Probes, not assertions (env-gated on TIANWEN_COMET_PROBE_ROOT): debayer one raw frame with every
/// algorithm and write the planes out so their star-colour spread can be measured, and report how the
/// normaliser's per-frame gain wanders across a session per debayer (the 2026-08-27 x3.7 finding).</summary>
public class DebayerColourProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task WriteEveryDebayerOfOneRawFrame()
    {
        var root = Environment.GetEnvironmentVariable("TIANWEN_COMET_PROBE_ROOT");
        var outDir = Environment.GetEnvironmentVariable("TIANWEN_DEBAYER_PROBE_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(root) || string.IsNullOrEmpty(outDir), "set TIANWEN_COMET_PROBE_ROOT and TIANWEN_DEBAYER_PROBE_OUT to run this probe");
        var raw = Path.Combine(root!, "c2025r2-swan/raw/LIGHT/2025-10-18_21-54-37__3.80_30.00s_0044.fits");
        Assert.SkipWhen(!File.Exists(raw), "raw frame not present");
        var ct = TestContext.Current.CancellationToken;
        foreach (var alg in new[] { DebayerAlgorithm.BilinearMono, DebayerAlgorithm.VNG, DebayerAlgorithm.AHD, DebayerAlgorithm.MHC })
        {
            Image.TryReadFitsFile(raw, out var image).ShouldBeTrue();
            image.ShouldNotBeNull();
            var deb = await image.DebayerAsync(alg, normalizeToUnit: false, ct);
            var path = Path.Combine(outDir!, $"debayer_{alg}.fits");
            deb.WriteToFitsFile(path);
            output.WriteLine($"{alg}: {deb.ChannelCount} channel(s) {deb.Width}x{deb.Height} -> {path}");
        }
    }

    /// <summary>How much the normaliser's (median - min) anchor wanders frame to frame, per channel, per debayer.</summary>
    [Fact]
    public async Task ReportNormaliserAnchorAcrossFrames()
    {
        var root = Environment.GetEnvironmentVariable("TIANWEN_COMET_PROBE_ROOT");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "set TIANWEN_COMET_PROBE_ROOT to run this probe");
        var dir = Path.Combine(root!, "c2025r2-swan/raw/LIGHT");
        Assert.SkipWhen(!Directory.Exists(dir), "raw frames not present");
        var ct = TestContext.Current.CancellationToken;
        var files = Directory.GetFiles(dir, "*.fits");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var alg in new[] { DebayerAlgorithm.AHD, DebayerAlgorithm.MHC, DebayerAlgorithm.VNG })
        {
            output.WriteLine($"== {alg}: per frame, per channel: min | p0.01% | median | pedestal | scale=(0.5/(median-min)) relative to frame 0");
            float[]? scale0 = null;
            for (var i = 0; i < files.Length; i += Math.Max(1, files.Length / 8))
            {
                Image.TryReadFitsFile(files[i], out var image).ShouldBeTrue();
                image.ShouldNotBeNull();
                var deb = await image.DebayerAsync(alg, normalizeToUnit: false, ct);
                var stats = TianWen.Lib.Imaging.Stacking.Normalizer.ComputeStats(deb);
                var line = $"  {Path.GetFileName(files[i])[..30]} ped {deb.Pedestal:F0}:";
                scale0 ??= new float[3];
                for (var c = 0; c < 3; c++)
                {
                    var arr = deb.GetChannelArray(c);
                    var flat = new float[arr.Length];
                    Buffer.BlockCopy(arr, 0, flat, 0, arr.Length * sizeof(float));
                    Array.Sort(flat);
                    var p = flat[(int)(flat.Length * 0.0001)];
                    var scale = 0.5f / (stats.PerChannelMedian[c] - stats.PerChannelFloor[c]);
                    if (i == 0) scale0[c] = scale;
                    line += $"  ch{c} floor {stats.PerChannelFloor[c],8:F0} min {flat[0],8:F0} p0.01% {p,6:F0} med {stats.PerChannelMedian[c],6:F0} gain x{scale / scale0[c]:F3}";
                }
                output.WriteLine(line);
            }
        }
    }
}

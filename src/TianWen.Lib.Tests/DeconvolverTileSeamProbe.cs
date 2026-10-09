using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated: whether <see cref="OperatorDeconvolutionRunner.TileMargin"/> covers the shipped graph's reach (#1401). The
/// margin was inherited with a claim that it covers Richardson-Lucy's border, while twenty iterations of a blur and its
/// adjoint, with the prior after each, reach further. A centre crop of a real master is deconvolved at the default tile
/// and as ONE tile holding the whole zoomed frame, which has no seam, and their difference is read at the native rows and
/// columns where a core boundary falls against everywhere else (the second is what two tile sizes differ by with no seam
/// at all: the execution provider's arithmetic). A wide kernel can make the one tile too large for memory (a 2.5 px
/// kernel's single 1744 px tile asked the CPU for 21.9 GB), so the second run can be another tiling instead, whose
/// boundaries fall elsewhere: a seam then shows at either run's boundaries.
/// <para>Set <c>TIANWEN_DECONV_SEAM_FRAME</c> to a three-channel master in [0, 1]. Optional: <c>TIANWEN_DECONV_SEAM_CROP</c>
/// (the crop's edge, default 1200, past one core of 1120 zoomed px so a seam falls inside it),
/// <c>TIANWEN_DECONV_SEAM_KERNEL</c> (default the published row, <c>0.77,0.91,0.98</c>),
/// <c>TIANWEN_DECONV_SEAM_AGAINST</c> (the second run's tile edge, default one tile holding the frame) and
/// <c>TIANWEN_DECONV_SEAM_REPORT</c> (a file the report is also written to).</para>
/// </summary>
public sealed class DeconvolverTileSeamProbe(ITestOutputHelper output)
{
    private readonly System.Text.StringBuilder _log = new();

    private void Line(string text)
    {
        output.WriteLine(text);
        _log.AppendLine(text);
    }

    [Fact]
    public async Task TiledAgainstAnotherTilingOnTheShippedGraph()
    {
        var path = Environment.GetEnvironmentVariable("TIANWEN_DECONV_SEAM_FRAME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "TIANWEN_DECONV_SEAM_FRAME not set");
        Assert.SkipUnless(new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out _), $"{OnnxTianWenDeconvolver.ModelFileName} not found");
        var ct = TestContext.Current.CancellationToken;
        var edge = int.Parse(Environment.GetEnvironmentVariable("TIANWEN_DECONV_SEAM_CROP") ?? "1200", CultureInfo.InvariantCulture);
        DeconvolutionKernel.TryParse(Environment.GetEnvironmentVariable("TIANWEN_DECONV_SEAM_KERNEL") ?? "0.77,0.91,0.98", null, null, out var kernel, out var error)
            .ShouldBeTrue(error);
        var stated = kernel.ShouldNotBeNull();

        Image.TryReadFitsFile(path, out var master).ShouldBeTrue($"could not read {path}");
        var (channels, width, height) = master.Shape;
        channels.ShouldBe(3);
        var crop = master.Crop(new PixelRect((width - edge) / 2, (height - edge) / 2, edge, edge));
        Line($"{Path.GetFileName(path)} {width}x{height}, centre crop {edge} px, kernel {stated.FwhmRed}/{stated.FwhmGreen}/{stated.FwhmBlue} beta {stated.Beta} at {stated.Resample}x");

        var zoomed = SplineZoom.OutputSize(edge, stated.Resample);
        var defaultTile = OperatorDeconvolutionRunner.TileFor(stated.Resample);
        var core = defaultTile - (2 * OperatorDeconvolutionRunner.TileMargin);
        var oneTile = ((zoomed + (2 * OperatorDeconvolutionRunner.TileMargin) + 15) / 16) * 16;
        var againstTile = Environment.GetEnvironmentVariable("TIANWEN_DECONV_SEAM_AGAINST") is { Length: > 0 } against
            ? int.Parse(against, CultureInfo.InvariantCulture)
            : oneTile;
        var againstCore = againstTile - (2 * OperatorDeconvolutionRunner.TileMargin);

        // On the CPU, through the runner itself: the arithmetic is the same at both tile sizes, so what differs is the seam,
        // and the GPU stays with whatever else holds it.
        new ModelResolver().TryResolve(OnnxTianWenDeconvolver.ModelFileName, out var modelPath).ShouldBeTrue();
        using var session = new InferenceSession(modelPath);
        var names = new OperatorGraphNames("image", "kernel", "stretch_min", "stretch_balance", "output");
        Task<Image> Deconvolve(int tile) => Task.Run(() => OperatorDeconvolutionRunner.Run(crop, session, names, stated, tile, null, ct).Output, ct);

        var started = DateTime.UtcNow;
        var tiled = await Deconvolve(defaultTile);
        Line($"tiled: {defaultTile} px tiles, core {core} px, {(zoomed + core - 1) / core} x {(zoomed + core - 1) / core} over the {zoomed} px zoomed frame ({(DateTime.UtcNow - started).TotalSeconds:F0} s)");
        started = DateTime.UtcNow;
        var single = await Deconvolve(againstTile);
        Line(againstCore >= zoomed
            ? $"against one tile: {againstTile} px ({(DateTime.UtcNow - started).TotalSeconds:F0} s)"
            : $"against {againstTile} px tiles, core {againstCore} px ({(DateTime.UtcNow - started).TotalSeconds:F0} s)");

        // The native rows and columns a core boundary maps to, through the way down's i * (n_in - 1) / (n_out - 1), for
        // both runs (one tile has none).
        var seams = new List<double>();
        foreach (var step in (int[])[core, againstCore])
        {
            for (var b = step; b < zoomed; b += step)
            {
                seams.Add(b * (edge - 1) / (double)(zoomed - 1));
            }
        }
        Line($"core boundaries at native {string.Join(", ", seams.ConvertAll(static s => s.ToString("F1", CultureInfo.InvariantCulture)))}");

        foreach (var band in (int[])[2, 8, 32])
        {
            var near = new List<float>();
            var far = new List<float>();
            var moved = new List<float>();
            for (var c = 0; c < 3; c++)
            {
                var t = tiled.GetChannelSpan(c);
                var s = single.GetChannelSpan(c);
                var input = crop.GetChannelSpan(c);
                for (var y = 0; y < edge; y++)
                {
                    var nearY = seams.Exists(seam => Math.Abs(y - seam) <= band);
                    for (var x = 0; x < edge; x++)
                    {
                        var i = (y * edge) + x;
                        var d = Math.Abs(t[i] - s[i]);
                        if (nearY || seams.Exists(seam => Math.Abs(x - seam) <= band))
                        {
                            near.Add(d);
                        }
                        else if (band == 32)
                        {
                            far.Add(d);
                        }
                        if (band == 2)
                        {
                            moved.Add(Math.Abs(s[i] - input[i]));
                        }
                    }
                }
            }
            Line($"within {band,2} px of a boundary: {Summary(near)}");
            if (band == 2)
            {
                Line($"what the deconvolution moved (the second run against its input): {Summary(moved)}");
            }
            if (band == 32)
            {
                Line($"farther than 32 px: {Summary(far)}");
            }
        }

        if (Environment.GetEnvironmentVariable("TIANWEN_DECONV_SEAM_REPORT") is { Length: > 0 } report)
        {
            await File.WriteAllTextAsync(report, _log.ToString(), ct);
        }
    }

    private static string Summary(List<float> values)
    {
        values.Sort();
        float At(double q) => values[Math.Min(values.Count - 1, (int)(q * values.Count))];
        return string.Create(CultureInfo.InvariantCulture,
            $"n {values.Count:N0}, median {At(0.5):E2}, p99 {At(0.99):E2}, p99.9 {At(0.999):E2}, max {values[^1]:E2}");
    }
}

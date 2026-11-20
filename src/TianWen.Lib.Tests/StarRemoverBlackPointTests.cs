using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="OnnxTianWenStarRemover"/> stretches as its models' contracts state (#2309): the depth travels from the
/// contract through <c>Open</c> into the whole-frame pass, models that disagree are refused, and a depth asked for at
/// <c>Open</c> is the one used. The stand-in is the denoiser's graph, as in <see cref="StarRemoverRunnerTests"/>, copied
/// beside a contract that states a depth.
/// </summary>
public sealed class StarRemoverBlackPointTests : IDisposable
{
    private readonly TempFolders _temp = new TempFolders();

    public void Dispose() => _temp.Dispose();

    /// <summary>A copy of the stand-in model in a folder of its own, its contract stating <paramref name="depth"/> as the
    /// stretch's black point (null leaves the contract silent, which is the minimum).</summary>
    private string CopyWithBlackPoint(string modelPath, double? depth)
    {
        var folder = _temp.Create("blackpoint");
        var copy = Path.Combine(folder.FullName, Path.GetFileName(modelPath));
        File.Copy(modelPath, copy);
        var text = File.ReadAllText(ModelContract.PathBeside(modelPath));
        if (depth is { } sigma)
        {
            const string median = "\"stretchMedianTarget\": 0.25,";
            text.ShouldContain(median);
            text = text.Replace(median, $"{median} \"stretchBlackPointSigma\": {sigma.ToString(CultureInfo.InvariantCulture)},", StringComparison.Ordinal);
        }
        File.WriteAllText(ModelContract.PathBeside(copy), text);
        return copy;
    }

    /// <summary>A flat noisy sky inside a canvas ring, and with <paramref name="hole"/> one pixel far under it.</summary>
    private static Image SkyWithHole(bool hole)
    {
        const int width = 300, height = 280, ring = 6;
        var rng = new Random(7);
        var data = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            data[c] = new float[height, width];
            for (var y = ring; y < height - ring; y++)
            {
                for (var x = ring; x < width - ring; x++)
                {
                    data[c][y, x] = 0.05f + 0.0015f * (float)rng.NextDouble();
                }
            }
            if (hole)
            {
                data[c][230, 250] = 0.001f;
            }
        }
        return new Image(data, BitDepth.Float32, 1f, 0f, 0f, default);
    }

    /// <summary>The largest difference between two frames over the sky far from the hole of <see cref="SkyWithHole"/>.</summary>
    private static float FarFromTheHole(Image a, Image b)
    {
        var (_, width, _) = a.Shape;
        var worst = 0f;
        for (var c = 0; c < 3; c++)
        {
            var left = a.GetChannelSpan(c);
            var right = b.GetChannelSpan(c);
            for (var y = 20; y < 120; y++)
            {
                for (var x = 20; x < 120; x++)
                {
                    worst = MathF.Max(worst, MathF.Abs(left[y * width + x] - right[y * width + x]));
                }
            }
        }
        return worst;
    }

    private static bool TryStandIn(out string modelPath)
    {
        if (new ModelResolver().TryResolve(N2nDenoiser.ModelFileName, out var found) && found is not null)
        {
            modelPath = found;
            return true;
        }
        modelPath = string.Empty;
        return false;
    }

    /// <summary>
    /// One pixel far under the sky moves the minimum's black point and with it the whole frame's stretch, so a model
    /// whose contract is silent answers differently 100 px away. A model whose contract states a depth is stretched from
    /// the sky noise, which one pixel does not move.
    /// </summary>
    [Fact(Timeout = 600_000)]
    public async Task AContractThatStatesADepthIsStretchedFromTheSkyNoiseAndNotTheMinimum()
    {
        if (!TryStandIn(out var modelPath))
        {
            Assert.Skip($"{N2nDenoiser.ModelFileName} not found (or is an LFS pointer); run 'git lfs pull' to enable this test.");
            return;
        }
        var ct = TestContext.Current.CancellationToken;
        using var silent = OnnxTianWenStarRemover.Open([CopyWithBlackPoint(modelPath, depth: null)], views: 1);
        using var stated = OnnxTianWenStarRemover.Open([CopyWithBlackPoint(modelPath, depth: 40)], views: 1);
        silent.BlackPointSigma.ShouldBeNull();
        stated.BlackPointSigma.ShouldBe(40);

        var byTheMinimum = FarFromTheHole(await silent.EnhanceAsync(SkyWithHole(false), ct), await silent.EnhanceAsync(SkyWithHole(true), ct));
        var byTheSkyNoise = FarFromTheHole(await stated.EnhanceAsync(SkyWithHole(false), ct), await stated.EnhanceAsync(SkyWithHole(true), ct));

        byTheSkyNoise.ShouldBeLessThan(1e-5f, "one pixel does not move a black point set by the sky noise");
        byTheMinimum.ShouldBeGreaterThan(10f * MathF.Max(byTheSkyNoise, 1e-6f), "and it does move one set by the minimum");
    }

    [Fact]
    public void ModelsWhoseContractsDisagreeOnTheBlackPointAreRefused()
    {
        if (!TryStandIn(out var modelPath))
        {
            Assert.Skip($"{N2nDenoiser.ModelFileName} not found (or is an LFS pointer); run 'git lfs pull' to enable this test.");
            return;
        }
        var silent = CopyWithBlackPoint(modelPath, depth: null);
        var at40 = CopyWithBlackPoint(modelPath, depth: 40);
        var at60 = CopyWithBlackPoint(modelPath, depth: 60);

        Should.Throw<NotSupportedException>(() => OnnxTianWenStarRemover.Open([silent, at40], views: 1)).Message.ShouldContain("one black point");
        Should.Throw<NotSupportedException>(() => OnnxTianWenStarRemover.Open([at40, at60], views: 1)).Message.ShouldContain("one black point");

        // Two models that agree open, and a depth asked for at Open is the one used, over the contracts'.
        using var agreed = OnnxTianWenStarRemover.Open([at40, CopyWithBlackPoint(modelPath, depth: 40)], views: 1);
        agreed.BlackPointSigma.ShouldBe(40);
        using var asked = OnnxTianWenStarRemover.Open([silent], views: 1, blackPointSigma: 60);
        asked.BlackPointSigma.ShouldBe(60);
    }

    /// <summary>A contract's depth is refused with the contract, not at the first frame's stretch.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AContractWhoseDepthIsNotOverZeroIsRefusedAtOpen(double depth)
    {
        if (!TryStandIn(out var modelPath))
        {
            Assert.Skip($"{N2nDenoiser.ModelFileName} not found (or is an LFS pointer); run 'git lfs pull' to enable this test.");
            return;
        }
        var stated = CopyWithBlackPoint(modelPath, depth);

        var refused = Should.Throw<Exception>(() => OnnxTianWenStarRemover.Open([stated], views: 1));

        refused.Message.ShouldContain("a depth is a finite number over 0");
    }

    /// <summary>A folder of stars tiles holds one black point's rule; rows written before the option read as the minimum.</summary>
    [Fact]
    public void AnExportIntoAFolderWrittenWithAnotherBlackPointIsRefused()
    {
        IReadOnlyList<double?> none = [];
        IReadOnlyList<double?> minimum = [null];
        IReadOnlyList<double?> at40 = [40];

        DatasetDegradationExporter.AnotherBlackPointRefusal(none, 40).ShouldBeNull();
        DatasetDegradationExporter.AnotherBlackPointRefusal(none, null).ShouldBeNull();
        DatasetDegradationExporter.AnotherBlackPointRefusal(minimum, null).ShouldBeNull();
        DatasetDegradationExporter.AnotherBlackPointRefusal(at40, 40).ShouldBeNull();

        DatasetDegradationExporter.AnotherBlackPointRefusal(minimum, 40).ShouldNotBeNull().ShouldContain("at each channel's minimum");
        DatasetDegradationExporter.AnotherBlackPointRefusal(at40, null).ShouldNotBeNull().ShouldContain("40 sky-noise sigma");
        DatasetDegradationExporter.AnotherBlackPointRefusal(at40, 60).ShouldNotBeNull().ShouldContain("export into an empty folder");
    }
}

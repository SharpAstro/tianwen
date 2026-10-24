using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An enhance records how much quieter it left a frame (<see cref="ImageMeta.NoiseReduction"/>, FITS <c>NOISERED</c>), and
/// every auto stretch reads the frame's noise back up by it, so a denoised frame is stretched by the data's real depth and
/// not by what the denoiser left: the enhanced Sculptor master's midtones were 0.000127 and threw the galaxy to white.
/// </summary>
public sealed class NoiseReductionStretchTests : System.IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void TheSolveReadsTheNoiseScaledByTheFactor()
    {
        ChannelStretchStats[] stats = [new(0f, 0.0104f, 6e-6f), new(0f, 0.0104f, 6e-6f), new(0f, 0.0104f, 1.1e-5f)];
        ChannelStretchStats[] scaled = [new(0f, 0.0104f, 2.4e-5f), new(0f, 0.0104f, 2.4e-5f), new(0f, 0.0104f, 4.4e-5f)];

        var withFactor = StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, stats, null, 1f, noiseScale: 4.0);
        var byHand = StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, scaled, null, 1f);

        withFactor.ShouldBe(byHand);
        StretchSolver.ComputeStretchUniforms(StretchMode.Linked, StretchParameters.Default, stats, null, 1f, noiseScale: null)
            .Midtones.R.ShouldBeLessThan(withFactor.Midtones.R, "without the factor the curve is steeper");
    }

    [Fact]
    public void TheFactorTravelsInTheFile()
    {
        var path = Path.Combine(_folders.Create("noisered-").FullName, "enhanced.fits");
        var plane = new float[8, 8];
        new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { Instrument = "synth", NoiseReduction = 4.2 }).WriteToFitsFile(path);

        Image.TryReadFitsHeader(path, out var read).ShouldBeTrue();
        read.Meta.NoiseReduction.ShouldNotBeNull().ShouldBe(4.2, 1e-9);
    }

    [Fact]
    public async Task AnEnhanceThatQuietensTheFrameSaysByHowMuchAndCompounds()
    {
        var source = Noisy(new ImageMeta { Instrument = "synth", NoiseReduction = 2.0 });
        var pipeline = new SharpenPipeline(denoiser: new HalvingDenoiser(), gradientCorrector: null);

        var result = await pipeline.ProcessAsync(new SharpenRequest(source, [new DenoiseFrameStep()]), EnhanceOptions.Default,
            cancellationToken: TestContext.Current.CancellationToken);

        // The denoiser halves the noise, on top of a frame an earlier enhance had already made twice as quiet.
        result.Final.ShouldNotBeNull().ImageMeta.NoiseReduction.ShouldNotBeNull().ShouldBe(4.0, 0.4);
    }

    [Fact]
    public async Task ADenoiseOutsideTheProgramSaysByHowMuchToo()
    {
        // image denoise runs the denoiser on its own (NarrowbandCombination.DenoiseAsync), and its plates were stamped with
        // nothing: a second NXT pass on the Sculptor's starless plate came back three times cleaner and was stretched three
        // times harder by every auto stretch, the galaxy thrown to white and the sky green.
        var source = Noisy(new ImageMeta { Instrument = "synth", NoiseReduction = 2.0 });

        var denoised = await NarrowbandCombination.DenoiseAsync([source], new HalvingDenoiser(), EnhanceOptions.Default,
            TestContext.Current.CancellationToken);

        denoised[0].ImageMeta.NoiseReduction.ShouldNotBeNull().ShouldBe(4.0, 0.4);
    }

    [Theory]
    [InlineData(1e-4f, 5e-5f, null, 2.0)]
    [InlineData(1e-4f, 5e-5f, 3.0, 6.0)]
    [InlineData(5e-5f, 1e-4f, 3.0, 3.0)]
    public void TheRuleIsNoiseInOverNoiseOutNeverBelowOneAndCompounding(float before, float after, double? earlier, double expected)
    {
        // A step that adds noise (a deblur) leaves the factor as it was.
        ImageMeta.CompoundNoiseReduction([before, before, before], [after, after, after], earlier)
            .ShouldNotBeNull().ShouldBe(expected, 1e-6);
    }

    private static Image Noisy(ImageMeta meta)
    {
        var plane = new float[64, 64];
        var random = new System.Random(7);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                plane[y, x] = 0.1f + (0.01f * (float)(random.NextDouble() - 0.5));
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, meta);
    }

    /// <summary>Halves every sample's distance from 0.1, which halves the noise and keeps the level.</summary>
    private sealed class HalvingDenoiser : IDenoiseEnhancer
    {
        public string Name => "Test/Halving";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input.Affine(0.5, 0.05));
    }
}

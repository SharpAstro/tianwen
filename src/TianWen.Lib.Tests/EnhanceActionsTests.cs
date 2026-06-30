using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="EnhanceActions.EnhanceAsync"/> -- the viewer's route-only AI-enhance helper.
/// Drives a <see cref="SharpenPipeline"/> built from trivial fakes (no model files) so the
/// request-build (factory selection) + adopt-into-document + status-line wiring is exercised
/// without any ONNX / RC-Astro dependency.
/// </summary>
[Collection("Imaging")]
public class EnhanceActionsTests
{
    /// <summary>Returns a fresh copy of the input for every role. A copy (not the input instance)
    /// so the pipeline's intermediate-plate release logic never aliases the source -- the same
    /// reason ProcessAsync_CanonicalWithIdentityEnhancers uses a non-identity star remover.</summary>
    private sealed class CloneEnhancer : IStarRemover, IStellarSharpener, INonStellarDeconvolver, IDenoiseEnhancer, IGradientCorrector
    {
        public string Name => "Test/CloneEnhancer";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                var src = input.GetChannelSpan(c);
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        plane[y, x] = src[y * w + x];
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta));
        }
    }

    private static Image SyntheticRgb(int w, int h, float fill)
    {
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                r[y, x] = fill;
                g[y, x] = fill;
                b[y, x] = fill;
            }
        return new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });
    }

    private static async Task<AstroImageDocument> BuildDocAsync(CancellationToken ct)
        => await AstroImageDocument.AdoptImageAsync(SyntheticRgb(16, 16, 0.4f), DebayerAlgorithm.None, wcs: null, filePath: "test.fits", ct);

    [Fact]
    public async Task EnhanceAsync_NoDeblurrer_RunsCanonicalAndReturnsAdoptedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var clone = new CloneEnhancer();
        // No deblurrer -> SupportsDeblur false -> SAS-shaped Canonical request.
        var pipeline = new SharpenPipeline(
            starRemover: clone, stellarSharpener: clone, nonStellarDeconvolver: clone,
            denoiser: clone, gradientCorrector: clone);
        var source = await BuildDocAsync(ct);
        var state = new ViewerState();

        var result = await EnhanceActions.EnhanceAsync(
            source, state, pipeline, EnhanceOptions.Default, DebayerAlgorithm.None, ct);

        result.ShouldNotBeNull();
        result.UnstretchedImage.Width.ShouldBe(16);
        result.UnstretchedImage.Height.ShouldBe(16);
        state.StatusMessage.ShouldBe("Enhanced (SAS)");
        // EnhanceActions does NOT own the in-progress flag (the controller does) -- it must be untouched.
        state.IsEnhancing.ShouldBeFalse();
    }

    [Fact]
    public async Task EnhanceAsync_PipelineThrows_ReturnsNullAndSetsErrorStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        // No enhancers registered -> ValidateRequest throws on RemoveStarsStep -> EnhanceActions
        // catches and surfaces the reason on the status line rather than propagating.
        var pipeline = new SharpenPipeline();
        var source = await BuildDocAsync(ct);
        var state = new ViewerState();

        var result = await EnhanceActions.EnhanceAsync(
            source, state, pipeline, EnhanceOptions.Default, DebayerAlgorithm.None, ct);

        result.ShouldBeNull();
        var msg = state.StatusMessage.ShouldNotBeNull();
        msg.ShouldStartWith("Enhance failed:");
    }
}

using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Tests.Helpers;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The white-balance-reaches-the-display check, on a REAL three-channel frame and through the real
/// render path (<see cref="Image.RenderStretchedRgba"/>).
///
/// <para>This is the measurement that was originally taken by hand against an SMC master and written
/// into <c>docs/known-limitations.md</c> as a table: three very different white balances handed to
/// the renderer produced mean channel values identical to 0.02 of a byte, because the linked stretch
/// divided the multipliers back out. Nothing in CI could have caught it -- the synthetic
/// <see cref="StretchLinkedWhiteBalanceTests"/> pin the uniforms and the arithmetic, but the symptom
/// was only ever observed end-to-end on real pixels, so the end-to-end form is worth its own test.</para>
///
/// <para>It goes through <see cref="StretchSolver"/> plus <c>RenderStretchedRgba</c> rather than
/// <c>AstroImageDocument</c> because the document derives its calibration by measuring the frame and
/// exposes no setter -- and the point here is to hand the renderer a CHOSEN triple and see whether it
/// survives.</para>
/// </summary>
[Collection("Imaging")]
public class StretchLinkedRealImageWhiteBalanceTests(ITestOutputHelper output)
{
    // A real 3-channel narrowband colour crop, the same frame StretchTests_ColorImagelinked renders.
    private const string Frame = "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop";

    private static readonly StretchParameters Params = new(0.15, -5.0);

    private static (double R, double G, double B) RenderMeans(
        Image image, StretchMode mode, (float R, float G, float B) wb)
    {
        var stats = StretchSolver.CollectPerChannelStats(image, image.ChannelCount);
        var uniforms = StretchSolver.ComputeStretchUniforms(
            mode, Params, stats, lumaStats: null, imageMaxValue: image.MaxValue, whiteBalance: wb);

        var rgba = new byte[image.Width * image.Height * 4];
        image.RenderStretchedRgba(uniforms, rgba);

        double r = 0, g = 0, b = 0;
        var pixels = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            r += rgba[i];
            g += rgba[i + 1];
            b += rgba[i + 2];
        }
        return (r / pixels, g / pixels, b / pixels);
    }

    [Fact]
    public async Task GivenLinkedModeWhenTheWhiteBalanceChangesThenTheRenderedMeansMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(Frame, cancellationToken: ct);
        image.ChannelCount.ShouldBe(3);

        // The three triples from the original hand measurement: near-neutral, then two strong and
        // opposite calibrations. Under the replicated-stats form all three rendered identically.
        var neutral = RenderMeans(image, StretchMode.Linked, (1.003f, 1f, 0.999f));
        var redPoor = RenderMeans(image, StretchMode.Linked, (0.341f, 1f, 0.850f));
        var redRich = RenderMeans(image, StretchMode.Linked, (1.350f, 1f, 0.536f));

        output.WriteLine($"neutral  R={neutral.R:F2} G={neutral.G:F2} B={neutral.B:F2}");
        output.WriteLine($"redPoor  R={redPoor.R:F2} G={redPoor.G:F2} B={redPoor.B:F2}");
        output.WriteLine($"redRich  R={redRich.R:F2} G={redRich.G:F2} B={redRich.B:F2}");

        // The measurement that mattered: the original failure held every one of these to within 0.02
        // of a byte. A whole byte is far outside that, and a modest bar for a 4x multiplier spread.
        Math.Abs(redPoor.R - neutral.R).ShouldBeGreaterThan(1.0);
        Math.Abs(redRich.R - neutral.R).ShouldBeGreaterThan(1.0);
        Math.Abs(redPoor.B - redRich.B).ShouldBeGreaterThan(1.0);

        // The assertions are on the R:B BALANCE WITHIN each render, not on one channel across two
        // renders -- and that distinction is the whole character of a shared curve. Comparing
        // redPoor.B against neutral.B looks obvious (0.850 < 0.999, so blue should darken) and is
        // wrong: the shared curve is positioned on the MEAN of the WB-applied channel stats, so a
        // triple that drops the mean makes the common curve more aggressive and lifts every channel
        // sitting above that mean. Blue at 0.850 is above redPoor's ~0.73 mean and renders BRIGHTER
        // (47.6 vs 39.1) while red at 0.341 is far below it and collapses (9.2). Absolute level is a
        // property of the joint anchor; only the ratios carry the colour.
        (redPoor.R < redPoor.B).ShouldBeTrue("red 0.341 must sit below blue 0.850");
        (redRich.R > redRich.B).ShouldBeTrue("red 1.350 must sit above blue 0.536");

        // Which makes this the sharpest statement of all: the two calibrations INVERT the balance.
        // No absorbing stretch can produce that, whatever it does to the overall level.
        var poorRatio = redPoor.R / redPoor.B;
        var richRatio = redRich.R / redRich.B;
        poorRatio.ShouldBeLessThan(1.0);
        richRatio.ShouldBeGreaterThan(1.0);
        output.WriteLine($"R:B balance  redPoor={poorRatio:F3}  neutral={neutral.R / neutral.B:F3}  redRich={richRatio:F3}");
    }

    [Fact]
    public async Task GivenUnlinkedModeThenTheSameChangeIsAbsorbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(Frame, cancellationToken: ct);

        // The counterpart, and the reason Linked was the thing to fix: an unlinked stretch
        // re-normalises each channel against its own stats, so the same three triples land in the
        // same place. Asserted, not merely stated, so a future "fix" to Unlinked shows up here as a
        // deliberate decision rather than a silent change of meaning.
        var neutral = RenderMeans(image, StretchMode.Unlinked, (1.003f, 1f, 0.999f));
        var redPoor = RenderMeans(image, StretchMode.Unlinked, (0.341f, 1f, 0.850f));

        output.WriteLine($"unlinked neutral R={neutral.R:F2} G={neutral.G:F2} B={neutral.B:F2}");
        output.WriteLine($"unlinked redPoor R={redPoor.R:F2} G={redPoor.G:F2} B={redPoor.B:F2}");

        Math.Abs(redPoor.R - neutral.R).ShouldBeLessThan(1.0,
            "an unlinked stretch normalises each channel against its own stats, absorbing the gain");
    }
}

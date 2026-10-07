using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The histogram overlay's LOG toggle is a declared node: the engine measures its box from its label and pins it to the histogram's
/// corner, so the label fits its box by construction. What is left to break is the unit: its box was once summed by hand in device
/// pixels around a label the engine scales from DESIGN units, the label was given the already scaled size, and it read "L..." on a 2x
/// display (reported 2026-10-02). These read the button as PAINTED, which a measuring seam could not.
/// </summary>
[Collection("Viewer")]
public class ViewerHistogramLogLabelTests
{
    // In design units the button is DpiScale times its 1x size. A size given in device pixels is scaled twice, DpiScale squared.
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task TheLogButtonScalesWithTheChrome(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var baseline = await PaintedLogButtonAsync(1f, ct);
        var scaled = await PaintedLogButtonAsync(dpi, ct);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1x: {baseline.Width:0.0} x {baseline.Height:0.0} px; {dpi}x: {scaled.Width:0.0} x {scaled.Height:0.0} px");

        baseline.Width.ShouldBeGreaterThan(0f);
        scaled.Width.ShouldBe(baseline.Width * dpi, 2f, "glyph advances round to the pixel, nothing else may differ");
        scaled.Height.ShouldBe(baseline.Height * dpi, 2f);
    }

    // The node binds its click from the rect it was drawn in, so a press where it is drawn toggles the scale, and a second toggles it back.
    [Fact]
    public async Task APressOnThePaintedButtonTogglesTheLogScale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(1.5f);
        var button = await OpenWithTheHistogramAsync(e2e, ct);
        var before = e2e.State.HistogramLogScale;

        e2e.Click(button);
        e2e.State.HistogramLogScale.ShouldBe(!before);

        e2e.Frame();
        e2e.Click(LogButton(e2e));
        e2e.State.HistogramLogScale.ShouldBe(before);
    }

    private static async Task<RectF32> PaintedLogButtonAsync(float dpi, CancellationToken ct)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        return await OpenWithTheHistogramAsync(e2e, ct);
    }

    private static async Task<RectF32> OpenWithTheHistogramAsync(ViewerE2E e2e, CancellationToken ct)
    {
        e2e.Viewer.PaintsHistogram = true;
        await e2e.OpenAsync(e2e.WriteColourFits("log.fits"), ct);
        e2e.Frame();
        return LogButton(e2e);
    }

    private static RectF32 LogButton(ViewerE2E e2e)
        => e2e.Region(hit => hit is HitResult.ButtonHit { Action: "HistogramLog" }, "the histogram's LOG toggle");
}

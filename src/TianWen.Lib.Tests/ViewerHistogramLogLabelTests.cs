using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The histogram overlay's LOG toggle reads "LOG" at every scale: its box is measured in device pixels and its label drawn by the
/// layout engine, which takes design units, so a label given the already scaled size was drawn DpiScale times too large and read
/// "L..." on a 2x display (reported 2026-10-02).
/// </summary>
[Collection("Viewer")]
public class ViewerHistogramLogLabelTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TheLogLabelFitsItsBoxAtEveryScale(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var (label, box) = e2e.Viewer.HistogramLogLabelFit(e2e.State);
        TestContext.Current.TestOutputHelper?.WriteLine($"DPI {dpi}: label {label:0.0} px in a box of {box:0.0} px");
        label.ShouldBeGreaterThan(0f);
        label.ShouldBeLessThanOrEqualTo(box);
    }
}

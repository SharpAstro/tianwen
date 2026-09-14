using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins what the default auto-stretch actually RENDERS, against statistics measured on a real sub.
/// </summary>
/// <remarks>
/// <para>The numbers are from a 120 s light of the Sculptor Galaxy (SVBONY SV605CC, gain 120,
/// Optolong L-Quad Enhance, 3008x3008 RGGB): median 0.052003, MAD 0.003296 of full scale. Frozen here
/// rather than read from a file, like the other real-field regressions in this suite -- the point is
/// the rendered result for a REAL sky, and a synthetic frame cannot supply a defensible median/MAD
/// pair.</para>
/// <para><b>Asserting the parameters would pin nothing.</b> What went wrong was not that the numbers
/// were unusual, it was what they did to faint signal, and that is two steps downstream of the pair
/// through the midtones transfer function. So these assert the rendered background AND the separation
/// just above it.</para>
/// </remarks>
public class StretchDefaultInspectionTests
{
    private const float RealSubMedian = 0.052003f;
    private const float RealSubMad = 0.003296f;

    /// <summary>Renders one value through the stretch the given parameters solve for this sub.</summary>
    private static double Render(float value, StretchParameters p)
    {
        var (shadows, midtones, _, rescale) =
            Image.ComputeStretchParameters(RealSubMedian, RealSubMad, p.Factor, p.ShadowsClipping);
        return Image.MidtonesTransferFunction(midtones, (value - shadows) * rescale);
    }

    /// <summary>
    /// The midtones transfer function maps the median to Factor by construction, so this is really a
    /// statement about which Factor ships: 0.2, which is N.I.N.A.'s per-light preview exactly.
    /// </summary>
    [Fact]
    public void TheDefaultPutsTheSkyWhereNinasPerLightPreviewPutsIt()
    {
        Render(RealSubMedian, StretchParameters.Default).ShouldBe(0.2, 0.005);
    }

    /// <summary>
    /// <b>The half that actually matters.</b> A pixel 3 MAD above the sky is faint nebulosity -- the
    /// outskirts of the galaxy this frame was taken of. Under the old (0.1, -5.0) default it rendered
    /// 0.036 above the background, which is why the target was barely visible while N.I.N.A. showed it
    /// plainly on the same sub. Anything much below 0.08 of separation is that failure returning.
    /// </summary>
    [Fact]
    public void FaintSignalSeparatesFromTheSkyFarEnoughToSee()
    {
        var bg = Render(RealSubMedian, StretchParameters.Default);
        var faint = Render(RealSubMedian + (3f * RealSubMad), StretchParameters.Default);

        (faint - bg).ShouldBeGreaterThan(0.08,
            "faint structure has to separate from the sky, which is the whole job of a preview stretch");
        faint.ShouldBeLessThan(0.6, "and it must not be so aggressive that the frame washes out");
    }

    /// <summary>
    /// The comparison that motivated the change, kept as a test so the regression is legible: the old
    /// default is strictly worse on this sub for both brightness and separation, and the viewer's 150%
    /// Boost could not make up the difference -- 0.1 x 1.5 is still under 0.2 before contrast enters.
    /// </summary>
    [Fact]
    public void TheOldDefaultIsWorseOnBothCountsEvenBoosted()
    {
        var old = new StretchParameters(0.1, -5.0);

        var oldBg = Render(RealSubMedian, old);
        var oldFaint = Render(RealSubMedian + (3f * RealSubMad), old);
        var newBg = Render(RealSubMedian, StretchParameters.Default);
        var newFaint = Render(RealSubMedian + (3f * RealSubMad), StretchParameters.Default);

        (oldFaint - oldBg).ShouldBeLessThan(0.05, "the old default crushed faint signal into the sky");
        (newFaint - newBg).ShouldBeGreaterThan(2.0 * (oldFaint - oldBg), "and the new one roughly trebles it");
        (oldBg * 1.5).ShouldBeLessThan(newBg, "Boost 150% on the old default still fell short of the new background");
    }

    /// <summary>
    /// <b>The default has to BE the first preset.</b> <c>ViewerState.StretchPresetIndex</c> starts at
    /// zero, so position zero is the entry a freshly opened viewer claims to be sitting on, and a
    /// default anywhere else makes that claim false before a key is ever pressed.
    /// <para>This held before only by coincidence -- the old default happened to equal the first entry
    /// -- and changing the default broke it, which is what this exists to catch.</para>
    /// <para>It used to carry more weight than that: <c>ViewerActions.CycleStretchPreset</c> stepped
    /// off the stored index without reconciling it, so the first press jumped to <c>Presets[1]</c>
    /// whatever was loaded, and a default off position zero could never be returned to. The cycler
    /// reconciles against the parameters in hand now (<c>ViewerActionsTests</c>), so this pins the
    /// weaker claim, and is kept because the two are still derived from one another.</para>
    /// </summary>
    [Fact]
    public void TheDefaultIsTheFirstPresetSoTheCyclerCanReturnToIt()
    {
        StretchParameters.Presets[0].ShouldBe(StretchParameters.Default);
    }

    /// <summary>
    /// Every preset stays a stretch a person would want: the cycler is the escape hatch when a frame
    /// wants something other than the default, so none of its entries may be degenerate.
    /// </summary>
    [Fact]
    public void EveryPresetRendersASaneSky()
    {
        foreach (var p in StretchParameters.Presets)
        {
            var bg = Render(RealSubMedian, p);
            bg.ShouldBeInRange(0.05, 0.45, $"preset {p} renders the sky at {bg:F3}");

            var faint = Render(RealSubMedian + (3f * RealSubMad), p);
            faint.ShouldBeGreaterThan(bg, $"preset {p} does not separate faint signal from the sky");
        }
    }
}

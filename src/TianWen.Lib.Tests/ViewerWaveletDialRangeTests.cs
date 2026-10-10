using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The wavelet dials hold every gain a derivation gives (#1435): a gain past ten or below zero (12.7 and -1.4 on the 12-inch SCT Jupiter,
/// the Auto view of A4) sits where it is on its dial, and a press there gives it back, where the fixed 0 to 10 track drew it pinned at an
/// end and a touch clamped it. Driven through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>), on a planetary master, whose
/// dials are the stacked view's.
/// </summary>
[Collection("Viewer")]
public class ViewerWaveletDialRangeTests
{
    // The 12-inch SCT Jupiter's derived gains at the truth, and a stop past it reaching further both ways.
    private static readonly ImmutableArray<double> AtTruth = [1.0, 12.7, -1.4, 1.4, 1.0, 1.0];
    private static readonly ImmutableArray<double> AtTwoAndAHalf = [1.0, 25.3, -3.2, 2.1, 1.0, 1.0];

    [Fact]
    public void WithNoDerivationTheDialsSpanZeroToTen()
    {
        new ViewerState().WaveletDialRange.ShouldBe((0f, ViewerState.WaveletDialMax));
    }

    [Fact]
    public void ADerivationInsideZeroToTenLeavesTheRangeAlone()
    {
        var state = new ViewerState { DerivedWaveletGains = Derived([0.8, 3.2, 1.6, 1.0, 1.0, 1.0]) };

        state.WaveletDialRange.ShouldBe((0f, ViewerState.WaveletDialMax));
    }

    [Fact]
    public void TheRangeHoldsEveryStopsGainsAndFollowsTheDerivationNotTheDials()
    {
        var state = new ViewerState { DerivedWaveletGains = Derived(AtTruth, AtTwoAndAHalf) };

        state.WaveletDialRange.ShouldBe((-4f, 26f));

        // A dial moved anywhere moves no end of the track.
        state.WaveletGains = state.WaveletGains.SetItem(1, 2f);
        state.WaveletDialRange.ShouldBe((-4f, 26f));
    }

    [Theory(Timeout = 180_000)]
    [MemberData(nameof(ViewerE2ETests.Scales), MemberType = typeof(ViewerE2ETests))]
    public async Task ADerivedGainOutsideZeroToTenSitsOnItsDialAndAPressThereKeepsIt(float dpi)
    {
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await ViewerPlanetaryMasterTests.OpenMasterAsync(e2e, ct);
        e2e.State.ShowDerivedAt(Derived(AtTruth, AtTwoAndAHalf), 1);
        e2e.Frame();
        var (min, max) = e2e.State.WaveletDialRange;

        // Band 2 (index 1) at 12.7 and band 3 at -1.4 are drawn where they are, not at the ends.
        var band2 = e2e.Viewer.WaveletSliderState(1);
        band2.Value.ShouldBe((12.7f - min) / (max - min), 1e-4f);
        e2e.Viewer.WaveletSliderState(2).Value.ShouldBe((-1.4f - min) / (max - min), 1e-4f);

        // A press on band 2's handle gives its gain back, to the track's own resolution, where a 0 to 10 track clamped it to ten.
        var track = Track(e2e, 1);
        var gainPerPixel = (max - min) / track.Width;
        e2e.Click(track.X + (band2.Value * track.Width), track.Y + (track.Height / 2f));
        e2e.State.WaveletGains[1].ShouldBe(12.7f, gainPerPixel * 1.5f, $"one pixel of the track is {gainPerPixel:0.00} of gain");

        // A drag on band 3 to the track's left end reaches below zero, where a 0 to 10 track could not.
        var low = Track(e2e, 2);
        var y = low.Y + (low.Height / 2f);
        e2e.Drag(low.X + (e2e.Viewer.WaveletSliderState(2).Value * low.Width), y, low.X - 20f, y);
        e2e.State.WaveletGains[2].ShouldBe(min, 1e-3f);

        // The stop past the truth puts its gains on the same track: nothing moved its ends.
        e2e.State.ChooseStrength(2.5);
        e2e.Frame();
        e2e.State.WaveletDialRange.ShouldBe((min, max));
        e2e.Viewer.WaveletSliderState(1).Value.ShouldBe((25.3f - min) / (max - min), 1e-4f);
    }

    private static RectF32 Track(ViewerE2E e2e, int band)
    {
        var dial = e2e.Viewer.WaveletSliderState(band);
        return e2e.Region(hit => hit is HitResult.SliderStateHit { State: var state } && ReferenceEquals(state, dial),
            $"band {band + 1}'s dial");
    }

    private static DerivedGains Derived(ImmutableArray<double> atTruth, ImmutableArray<double>? pastIt = null)
    {
        var stops = ImmutableArray.CreateBuilder<GainStop>();
        stops.Add(new GainStop(1, [atTruth]));
        if (pastIt is { } past)
        {
            stops.Add(new GainStop(2.5, [past]));
        }
        return new DerivedGains([.. System.Linq.Enumerable.Select(atTruth, g => (float)g)], "derived for the test", null)
        {
            Stops = stops.ToImmutable(),
        };
    }
}

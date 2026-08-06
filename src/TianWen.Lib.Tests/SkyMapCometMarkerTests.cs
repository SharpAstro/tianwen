using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The comet layer is the ONLY path a comet has onto the sky map: comets are deliberately absent from
/// <c>ICelestialObjectDB</c>, so the object-overlay pass that draws every other pinned landmark never
/// sees one. That makes the draw predicate load-bearing rather than cosmetic, which is why it is pure
/// and pinned here: a pinned comet that fails it is invisible with no other route onto the map.
/// </summary>
public class SkyMapCometMarkerTests
{
    private const double Limit = 10.0;

    [Theory]
    // Layer on: the magnitude limit decides, and the boundary is inclusive.
    [InlineData(true, false, 8.0, true)]
    [InlineData(true, false, 10.0, true)]
    [InlineData(true, false, 12.8, false)]
    // Layer off: an unpinned comet is hidden however bright it is.
    [InlineData(false, false, 2.0, false)]
    public void AnUnpinnedCometNeedsTheLayerAndTheMagnitudeLimit(bool layerOn, bool pinned, double vmag, bool drawn)
        => SkyMapState.ShouldDrawCometMarker(layerOn, pinned, vmag, Limit).ShouldBe(drawn);

    [Theory]
    // A pin overrides BOTH gates, in every combination. 12.8 is 10P's real predicted magnitude while
    // two days from perihelion at 0.41 AU, which is the case that reported this: the prediction comes
    // from an element set two apparitions old, and it hid the user's own pinned target.
    [InlineData(true, 12.8)]
    [InlineData(false, 12.8)]
    [InlineData(false, 30.0)]
    [InlineData(false, double.NaN)]
    public void APinnedCometIsAlwaysDrawn(bool layerOn, double vmag)
        => SkyMapState.ShouldDrawCometMarker(layerOn, isPinned: true, vmag, Limit).ShouldBeTrue();

    [Fact]
    public void ANaNMagnitudeDrawsBecauseTheGateIsAnExclusionNotAnInclusion()
    {
        // A comet with no SBDB photometric model predicts NaN, and every comparison with NaN is false,
        // so "not brighter-than-limit" admits it while the equivalent-looking "vmag <= limit" would
        // reject it. Recorded as the deliberate reading rather than tightened: the predicate keeps the
        // original gate's exact semantics, and NaN cannot reach it anyway because
        // GetCometPositionsCached drops a comet whose magnitude does not solve before it becomes a
        // marker. Anyone tempted to "fix" the asymmetry should move the check there, not here.
        SkyMapState.ShouldDrawCometMarker(cometLayerOn: true, isPinned: false, double.NaN, Limit).ShouldBeTrue();
    }
}

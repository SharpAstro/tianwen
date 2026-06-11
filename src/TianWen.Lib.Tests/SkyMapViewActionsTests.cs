using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="SkyMapViewActions"/> — the single source of truth for sky-map
/// viewport mutation (centre + FOV clamp + overlay toggles), shared by the search-commit path
/// and the DEBUG inspector's <c>SkyMapSetViewSignal</c> handler.
/// </summary>
public class SkyMapViewActionsTests
{
    private static SkyMapState NewState() => new()
    {
        CenterRA = 6.0,
        CenterDec = 30.0,
        FieldOfViewDeg = 60.0,
        NeedsRedraw = false, // start "clean" so we can assert a redraw was flagged
    };

    [Fact]
    public void CenterOn_SetsCentreNormalisesAndFlagsRedraw()
    {
        var s = NewState();

        SkyMapViewActions.CenterOn(s, 12.5, -10.0);

        s.CenterRA.ShouldBe(12.5);
        s.CenterDec.ShouldBe(-10.0);
        s.NeedsRedraw.ShouldBeTrue();
    }

    [Fact]
    public void CenterOn_WrapsRaIntoRangeAndClampsDecAwayFromPole()
    {
        var s = NewState();

        SkyMapViewActions.CenterOn(s, 26.0, 95.0);

        s.CenterRA.ShouldBe(2.0, 1e-9);   // 26h wraps to 2h
        s.CenterDec.ShouldBe(89.5, 1e-9); // clamped off the projection pole
    }

    [Fact]
    public void SetView_PartialFovOnly_LeavesCentreUnchanged()
    {
        var s = NewState();

        var changed = SkyMapViewActions.SetView(s, fieldOfViewDeg: 5.0);

        changed.ShouldBeTrue();
        s.FieldOfViewDeg.ShouldBe(5.0);
        s.CenterRA.ShouldBe(6.0);   // untouched
        s.CenterDec.ShouldBe(30.0); // untouched
        s.NeedsRedraw.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0.1, SkyMapViewActions.MinFieldOfViewDeg)]
    [InlineData(500.0, SkyMapViewActions.MaxFieldOfViewDeg)]
    [InlineData(45.0, 45.0)]
    public void SetView_ClampsFovToScrollZoomBounds(double requested, double expected)
    {
        var s = NewState();

        SkyMapViewActions.SetView(s, fieldOfViewDeg: requested);

        s.FieldOfViewDeg.ShouldBe(expected);
    }

    [Fact]
    public void SetView_SingleAxisCentre_KeepsOtherAxis()
    {
        var s = NewState();

        SkyMapViewActions.SetView(s, centerRaHours: 18.0); // RA only

        s.CenterRA.ShouldBe(18.0);
        s.CenterDec.ShouldBe(30.0); // Dec preserved from current value
    }

    [Fact]
    public void SetView_TogglesOverlayLayers()
    {
        var s = NewState();
        s.ShowObjectOverlay = false;
        s.ShowDarkNebulae = false;

        SkyMapViewActions.SetView(s, showObjectOverlay: true, showDarkNebulae: true);

        s.ShowObjectOverlay.ShouldBeTrue();
        s.ShowDarkNebulae.ShouldBeTrue();
    }

    [Fact]
    public void SetView_AllNull_IsNoOpAndDoesNotForceRedraw()
    {
        var s = NewState();

        var changed = SkyMapViewActions.SetView(s);

        changed.ShouldBeFalse();
        s.NeedsRedraw.ShouldBeFalse(); // nothing changed -> no redraw forced
        s.CenterRA.ShouldBe(6.0);
        s.CenterDec.ShouldBe(30.0);
        s.FieldOfViewDeg.ShouldBe(60.0);
    }
}

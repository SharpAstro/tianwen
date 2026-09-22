using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The hover highlight and the "only with photo" object filter, which ship together because they are
/// two halves of the same complaint: the atlas could not tell you what a click would take, and it
/// could not narrow itself to the objects it can show you a picture of.
/// </summary>
/// <remarks>
/// The load-bearing property of the hover half is that the highlight and the click come from ONE
/// resolver, so the tests here assert the two agree rather than asserting the highlight in isolation
/// -- an isolated assertion would still pass if the two drifted apart, which is the whole failure
/// mode. The picture half is tested at its three consumers (the shared predicate, the click gate, the
/// gather cache key) for the same reason: it is one rule that three call sites have to ask.
/// </remarks>
[Collection("Astrometry")]
public class SkyMapHoverAndPictureTests
{
    private static readonly CelestialObject Nebula = new(
        CatalogIndex.NGC7331, ObjectType.HIIReg, 12.0, 0.0, Constellation.Pegasus,
        Half.NaN, Half.NaN, Half.NaN, new HashSet<string> { "Photogenic" });

    private static readonly CelestialObject Star = new(
        CatalogIndex.HIP025281, ObjectType.Star, 12.0, 0.1, Constellation.Pegasus,
        Half.NaN, Half.NaN, Half.NaN, new HashSet<string> { "PlainStar" });

    // 60' major axis -> a hit radius of ~250 px at the 2 deg FOV below, which is what lets the
    // nebula's ellipse claim a click that lands on the star inside it.
    private static readonly CelestialObjectShape NebulaShape = new((Half)60.0, (Half)60.0, (Half)0.0);

    private const float SurfaceSize = 1000f;

    private static SkyMapState NewState(bool onlyWithPicture = false) => new()
    {
        Mode = SkyMapMode.Equatorial,
        CenterRA = 12.0,
        CenterDec = 0.0,
        FieldOfViewDeg = 2.0,
        ShowObjectOverlay = true,
        ShowOnlyObjectsWithPicture = onlyWithPicture,
        LastContentRect = new RectF32(0, 0, SurfaceSize, SurfaceSize),
    };

    private static (float X, float Y) Project(SkyMapState state, double ra, double dec)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(SurfaceSize, state.FieldOfViewDeg);
        SkyMapProjection.ProjectWithMatrix(ra, dec, state.CurrentViewMatrix, ppr,
            SurfaceSize * 0.5f, SurfaceSize * 0.5f, out var x, out var y).ShouldBeTrue();
        return (x, y);
    }

    // The whole point of routing both through TryResolveHit. A highlight over one object and a panel
    // about another is worse than no highlight, and two hit tests written the same way is exactly how
    // that happens -- so this asserts the agreement, not the highlight.
    [Fact]
    public void TheHoverHighlightNamesTheObjectAClickWouldSelect()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var state = NewState();
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var viewingUtc = DateTimeOffset.UtcNow;
        var (starX, starY) = Project(state, Star.RA, Star.Dec);

        var hover = SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, viewingUtc, starX, starY, pinnedCatalogIndices: null).ShouldNotBeNull();

        SkyMapSearchActions.SelectAtScreenPoint(
            state, db, 0, 0, viewingUtc, starX, starY, InputModifier.None, []).ShouldBeTrue();

        // Both land on the nebula: its shape radius swallows a click on the star inside it.
        hover.Index.ShouldBe(Nebula.Index);
        state.Search.InfoPanel.ShouldNotBeNull().Name.ShouldBe("Photogenic");
        hover.IsEphemeris.ShouldBeFalse();
        hover.HitRadiusPx.ShouldBeGreaterThan(20f, "the ellipse, not the plain click tolerance, claimed it");
    }

    // Ctrl is read at the moment of the press and a hover carries no press. Guessing at it would make
    // the wash wrong precisely when the user is holding Ctrl to pick a star out of a nebula.
    [Fact]
    public void HoverShowsThePlainAnswerEvenWhereCtrlWouldPickDifferently()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var state = NewState();
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var viewingUtc = DateTimeOffset.UtcNow;
        var (starX, starY) = Project(state, Star.RA, Star.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, viewingUtc, starX, starY, pinnedCatalogIndices: null)
            .ShouldNotBeNull().Index.ShouldBe(Nebula.Index);

        SkyMapSearchActions.SelectAtScreenPoint(
            state, db, 0, 0, viewingUtc, starX, starY, InputModifier.Ctrl, []).ShouldBeTrue();
        state.Search.InfoPanel.ShouldNotBeNull().Name.ShouldBe("PlainStar");
    }

    [Fact]
    public void HoverOverEmptySkyResolvesToNothing()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var state = NewState();
        state.CurrentViewMatrix = state.ComputeViewMatrix();

        // Far outside the nebula's ~250 px hit radius, still inside the surface.
        SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, DateTimeOffset.UtcNow, 980f, 20f, pinnedCatalogIndices: null).ShouldBeNull();
    }

    // A pointer off the map is not over anything, and the projection would happily answer for a point
    // outside the rect -- so the bound is stated rather than inherited.
    [Fact]
    public void HoverOutsideTheContentRectResolvesToNothing()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var state = NewState();
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var (nebX, nebY) = Project(state, Nebula.RA, Nebula.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, DateTimeOffset.UtcNow, nebX, nebY, pinnedCatalogIndices: null)
            .ShouldNotBeNull();

        state.LastContentRect = new RectF32(0, 0, 200, 200);
        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, DateTimeOffset.UtcNow, nebX, nebY, pinnedCatalogIndices: null)
            .ShouldBeNull();
    }

    // The failure the click gate exists for, one filter further on: an object the overlay is not
    // drawing must not stay selectable through apparently-empty sky.
    [Fact]
    public void OnlyWithPictureTakesAnObjectOutOfTheClickAndTheHoverTogether()
    {
        // The nebula has no verified article; the star does. Contrived the "wrong" way round on
        // purpose, so a rule that quietly applied to extended objects only would fail here.
        var db = new ArticleDb(Nebula, Star, NebulaShape, withPicture: Star.Index);
        var state = NewState(onlyWithPicture: true);
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var viewingUtc = DateTimeOffset.UtcNow;
        var (nebX, nebY) = Project(state, Nebula.RA, Nebula.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, viewingUtc, nebX, nebY, pinnedCatalogIndices: null)
            .ShouldBeNull("the nebula has no verified picture and the filter is on");
        SkyMapSearchActions.SelectAtScreenPoint(
            state, db, 0, 0, viewingUtc, nebX, nebY, InputModifier.None, []).ShouldBeFalse();

        // Switch the filter off and the same pixel resolves again.
        state.ShowOnlyObjectsWithPicture = false;
        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, viewingUtc, nebX, nebY, pinnedCatalogIndices: null)
            .ShouldNotBeNull().Index.ShouldBe(Nebula.Index);
    }

    [Fact]
    public void AnObjectWithAVerifiedPictureSurvivesTheFilter()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape, withPicture: Nebula.Index);
        var state = NewState(onlyWithPicture: true);
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var (nebX, nebY) = Project(state, Nebula.RA, Nebula.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, DateTimeOffset.UtcNow, nebX, nebY, pinnedCatalogIndices: null)
            .ShouldNotBeNull().Index.ShouldBe(Nebula.Index);
    }

    // Every other filter on this path exempts a pinned landmark; this one has to as well, or turning
    // the mode on would hide the targets the user explicitly asked to see.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APinnedTargetSurvivesEveryLayerFilter(bool onlyWithPicture)
    {
        OverlayEngine.PassesLayerFilter(
            ObjectType.HIIReg, isPinned: true, hasPicture: false,
            showObjects: false, showDarkNebulae: false, onlyWithPicture: onlyWithPicture)
            .ShouldBeTrue();
    }

    // [D] is its OWN layer and the picture filter does not reach it. This asserted the opposite at
    // first, on the reasoning that "only with photo" is a statement about the whole overlay -- and
    // that contradicted the row's own shape, since it is presented as a sub-setting of [O] and is
    // unavailable while [O] is off.
    //
    // Worse, the two rules together were a trap reachable in two keystrokes: with [O] on turn [I]
    // on, then turn [O] off and [D] on. The filter kept applying (almost no dust lane has a
    // verified article, so the layer emptied) while the row was unavailable, its Toggle answered
    // false and the palette drew it dimmed and OFF. A filter in force, reported as off, reachable
    // only by turning [O] back on. Both halves had a passing test and neither saw it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFilterDoesNotReachTheDarkNebulaLayer(bool hasPicture)
        => OverlayEngine.PassesLayerFilter(
            ObjectType.DarkNeb, isPinned: false, hasPicture: hasPicture,
            showObjects: false, showDarkNebulae: true, onlyWithPicture: true).ShouldBeTrue();

    // The other half of the same rule: with [O] off the filter changes nothing at all, which is
    // what makes the row's availability rule and its effect the same rule.
    [Fact]
    public void WithTheObjectLayerOffTheFilterIsInert()
    {
        foreach (var onlyWithPicture in new[] { false, true })
        {
            OverlayEngine.PassesLayerFilter(
                ObjectType.HIIReg, isPinned: false, hasPicture: false,
                showObjects: false, showDarkNebulae: true, onlyWithPicture: onlyWithPicture)
                .ShouldBeFalse("an object of an [O] type is hidden by [O] being off, filter or not");
        }
    }

    // And it still narrows what [O] admits, which is the whole point of the row.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void TheFilterNarrowsTheObjectLayer(bool hasPicture, bool expected)
        => OverlayEngine.PassesLayerFilter(
            ObjectType.HIIReg, isPinned: false, hasPicture: hasPicture,
            showObjects: true, showDarkNebulae: false, onlyWithPicture: true).ShouldBe(expected);

    // The filter strips the CACHED candidate list, so a key that does not carry it would keep serving
    // the list gathered before the toggle -- and, switching back, never restore what it removed.
    [Fact]
    public void TogglingTheFilterChangesTheOverlayGatherKey()
    {
        using var renderer = new RgbaImageRenderer(64, 64);
        var tab = new HoverTestSkyMapTab(renderer);
        var plannerState = new PlannerState();
        var rect = new RectF32(0, 0, 64, 64);
        tab.State.CurrentViewMatrix = tab.State.ComputeViewMatrix();

        object KeyNow() => tab.BuildOverlayKeyForTest(rect, 30.0, 32f, 32f, 1000.0, plannerState);

        var before = KeyNow();
        KeyNow().ShouldBe(before, "nothing changed");

        tab.State.ShowOnlyObjectsWithPicture = true;
        KeyNow().ShouldNotBe(before);
    }

    // A sub-setting, not a layer: with [O] off there is nothing for it to narrow, so the palette
    // draws it dimmed and its key stays unhandled (SkyMapLayer.Toggle returns false).
    [Fact]
    public void TheOnlyWithPhotoRowIsUnavailableWhileTheObjectOverlayIsOff()
    {
        var layer = SkyMapLayers.All.Single(l => l.KeyLabel == "I");
        var state = new SkyMapState { ShowObjectOverlay = false };

        layer.Available(state).ShouldBeFalse();
        layer.Toggle(state).ShouldBeFalse();
        state.ShowOnlyObjectsWithPicture.ShouldBeFalse();

        state.ShowObjectOverlay = true;
        layer.Available(state).ShouldBeTrue();
        layer.Toggle(state).ShouldBeTrue();
        state.ShowOnlyObjectsWithPicture.ShouldBeTrue();
    }

    [Fact]
    public void TheOnlyWithPhotoRowSitsDirectlyUnderTheObjectsRow()
    {
        var labels = SkyMapLayers.All.Select(l => l.KeyLabel).ToArray();
        Array.IndexOf(labels, "I").ShouldBe(Array.IndexOf(labels, "O") + 1);
        // The palette has no nesting, so the indent is in the label itself.
        SkyMapLayers.All.Single(l => l.KeyLabel == "I").Label.ShouldStartWith("  ");
    }

    // Counting resolves, not pixels: a re-resolve that lands on the same object draws the identical
    // frame, so only a count can tell "resolved once and reused" from "resolved on every move". A
    // frame is rendered between the moves, because otherwise the per-frame budget below would block
    // the second one and the slop would be untested.
    [Fact]
    public void ASmallPointerMoveDoesNotReResolveButAMoveToAnotherObjectDoes()
    {
        using var renderer = new RgbaImageRenderer(200, 200);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, 200, 200);

        tab.State.ShowObjectOverlay = true;
        tab.Render(plannerState, rect, time);

        tab.HandleInput(new InputEvent.MouseMove(100f, 100f));
        tab.HoverResolves.ShouldBe(1);

        // Inside the slop: the answer cannot have changed enough to matter.
        tab.Render(plannerState, rect, time);
        tab.HandleInput(new InputEvent.MouseMove(101f, 100f));
        tab.HoverResolves.ShouldBe(1);

        // Well outside it.
        tab.Render(plannerState, rect, time);
        tab.HandleInput(new InputEvent.MouseMove(140f, 140f));
        tab.HoverResolves.ShouldBe(2);
    }

    // The bound that makes hover affordable at a deep zoom, where one resolve was measured at 2 ms
    // against the real catalogue: a mouse delivers moves at well over frame rate, and nothing can
    // SHOW a second answer before the next paint. Asserted as a count for the same reason as above --
    // every one of these moves draws the identical frame.
    [Fact]
    public void ManyMovesBetweenTwoFramesCostExactlyOneResolve()
    {
        using var renderer = new RgbaImageRenderer(200, 200);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, 200, 200);

        tab.State.ShowObjectOverlay = true;
        tab.Render(plannerState, rect, time);

        for (var i = 0; i < 20; i++)
        {
            tab.HandleInput(new InputEvent.MouseMove(20f + i * 8f, 20f + i * 8f));
        }

        tab.HoverResolves.ShouldBe(1);

        // And the budget re-opens with the frame, so the highlight cannot get stuck on a stale answer.
        tab.Render(plannerState, rect, time);
        tab.HandleInput(new InputEvent.MouseMove(60f, 60f));
        tab.HoverResolves.ShouldBe(2);
    }

    // Every resolve asks for a frame, including one that landed on the same object: the budget above
    // is released by a PAINT, so a resolve that scheduled none would be the last one until something
    // else repainted.
    [Fact]
    public void AResolveAlwaysAsksForTheFrameThatWouldShowIt()
    {
        using var renderer = new RgbaImageRenderer(200, 200);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, 200, 200);

        tab.State.ShowObjectOverlay = true;
        tab.Render(plannerState, rect, time);

        tab.State.NeedsRedraw = false;
        tab.HandleInput(new InputEvent.MouseMove(150f, 150f));
        tab.State.NeedsRedraw.ShouldBeTrue();
    }

    // The view moved since the resolve, so the target is no longer known to be an answer about
    // where the cursor is. It used to be DROPPED; it is now re-tested at the pointer's last position
    // against the new view (one resolve, inside the draw), which keeps the wash where the object is
    // still under the pointer and clears it where it is not. Dropping unconditionally is what made
    // the wash flash in Horizon mode, whose centre moves with time on every frame.
    [Fact]
    public void AZoomReTestsTheHoverTargetAgainstTheNewView()
    {
        using var renderer = new RgbaImageRenderer(200, 200);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, 200, 200);

        tab.State.ShowObjectOverlay = true;
        tab.State.CenterRA = Nebula.RA;
        tab.State.CenterDec = Nebula.Dec;
        tab.State.FieldOfViewDeg = 2.0;
        tab.Render(plannerState, rect, time);

        tab.HandleInput(new InputEvent.MouseMove(100f, 100f));
        tab.State.HoverTarget.ShouldNotBeNull();
        var resolves = tab.HoverResolves;

        // Zoomed out with the nebula still under the pointer: re-tested once, still the nebula.
        tab.State.FieldOfViewDeg = 4.0;
        tab.Render(plannerState, rect, time);
        tab.HoverResolves.ShouldBe(resolves + 1, "the moved view is re-tested at the pointer, once");
        tab.State.HoverTarget.ShouldNotBeNull().Index.ShouldBe(Nebula.Index);

        // Panned so that nothing is under the pointer any more: re-tested, and cleared.
        tab.State.CenterRA = Nebula.RA + 0.5;
        tab.Render(plannerState, rect, time);
        tab.State.HoverTarget.ShouldBeNull("nothing is under the pointer in the moved view");
    }

    // The wash has to reach the PIXELS, and on a surface with no GPU: FillEllipse is the one
    // primitive it uses precisely because all three renderers implement it natively. Rendered twice
    // over the same view, so the only difference between the frames IS the highlight.
    [Fact]
    public void TheHoverWashIsPaintedUnderTheObjectItNames()
    {
        const int size = 400;
        using var renderer = new RgbaImageRenderer(size, size);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, size, size);

        tab.State.ShowObjectOverlay = true;
        tab.State.CenterRA = Nebula.RA;
        tab.State.CenterDec = Nebula.Dec;
        tab.State.FieldOfViewDeg = 2.0;
        tab.Render(plannerState, rect, time);
        var without = (byte[])renderer.Surface.Pixels.Clone();

        // Park the pointer on the nebula's own screen position (the view is centred on it).
        tab.HandleInput(new InputEvent.MouseMove(size / 2f, size / 2f));
        tab.State.HoverTarget.ShouldNotBeNull().Index.ShouldBe(Nebula.Index);

        tab.Render(plannerState, rect, time);
        var with = renderer.Surface.Pixels;

        var changed = 0;
        for (var i = 0; i < with.Length; i += 4)
        {
            if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2])
            {
                changed++;
            }
        }

        // The wash is the object's own shape: a 60 arcmin disc at this field is a 100 px radius, so
        // about 31,000 px of a 160,000 px surface. A floor well under that catches "drawn as nothing"
        // (and the old 36 px spot, about 4,000 px); the ceiling catches a wash that repainted the frame.
        changed.ShouldBeGreaterThan(20_000);
        changed.ShouldBeLessThan(size * size / 4);
    }

    /// <summary>
    /// <b>The wash takes the hovered object's shape.</b> An elongated galaxy lights as its ellipse,
    /// not as a spot: inside along the major axis is washed, the same distance out along the minor
    /// axis is not, and the area is the ellipse's. It used to be a circle of the hit radius clamped
    /// to 36 px, which over M31 read as a mark on the galaxy rather than the galaxy lit.
    /// </summary>
    [Fact]
    public void TheHoverWashTakesTheObjectsShape()
    {
        const int size = 400;
        using var renderer = new RgbaImageRenderer(size, size);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };

        // 60 by 20 arcmin, position angle 0: the major axis runs north-south, which in an
        // equatorial view is screen-vertical. At a 2 degree field on 400 px that is a 100 px
        // semi-major and a 33 px semi-minor axis.
        var galaxyShape = new CelestialObjectShape((Half)60.0, (Half)20.0, (Half)0.0);
        var db = new ArticleDb(Nebula, Star, galaxyShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, size, size);

        tab.State.ShowObjectOverlay = true;
        tab.State.CenterRA = Nebula.RA;
        tab.State.CenterDec = Nebula.Dec;
        tab.State.FieldOfViewDeg = 2.0;
        tab.Render(plannerState, rect, time);
        var without = (byte[])renderer.Surface.Pixels.Clone();

        tab.HandleInput(new InputEvent.MouseMove(size / 2f, size / 2f));
        tab.State.HoverTarget.ShouldNotBeNull().Shape.ShouldNotBeNull();

        tab.Render(plannerState, rect, time);
        var with = renderer.Surface.Pixels;

        static bool Changed(byte[] a, byte[] b, int x, int y, int size)
        {
            var i = ((y * size) + x) * 4;
            return a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2];
        }

        const int centre = size / 2;
        Changed(with, without, centre, centre + 60, size).ShouldBeTrue("60 px along the major axis is inside the ellipse");
        Changed(with, without, centre, centre - 60, size).ShouldBeTrue("60 px along the major axis is inside the ellipse");
        Changed(with, without, centre + 60, centre, size).ShouldBeFalse("60 px along the minor axis is outside a 33 px semi-minor axis");
        Changed(with, without, centre - 60, centre, size).ShouldBeFalse("60 px along the minor axis is outside a 33 px semi-minor axis");

        var changed = 0;
        for (var i = 0; i < with.Length; i += 4)
        {
            if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2])
            {
                changed++;
            }
        }

        // pi * 100 * 33 is about 10,500 px. The old spot was about 4,000 and a 100 px disc 31,000.
        changed.ShouldBeGreaterThan(7_000);
        changed.ShouldBeLessThan(15_000);
    }

    // One press retires the wash, which is how three cases with no signal of their own are covered:
    // a DIR.Lib DragCapture (it begins with a press and then takes every move itself, so the tab
    // stops being told where the pointer is), a tab switch (which IS a press, on the tab button),
    // and a press on the sky, where the user has stopped asking what a click would take.
    [Fact]
    public void APressRetiresTheHoverWash()
    {
        using var renderer = new RgbaImageRenderer(200, 200);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var plannerState = new PlannerState { ObjectDb = db };
        var time = new FakeTimeProviderWrapper(DateTimeOffset.UtcNow);
        var rect = new RectF32(0, 0, 200, 200);

        tab.State.ShowObjectOverlay = true;
        tab.State.CenterRA = Nebula.RA;
        tab.State.CenterDec = Nebula.Dec;
        tab.State.FieldOfViewDeg = 2.0;
        tab.Render(plannerState, rect, time);

        tab.HandleInput(new InputEvent.MouseMove(100f, 100f));
        tab.State.HoverTarget.ShouldNotBeNull();

        tab.HandleInput(new InputEvent.MouseDown(100f, 100f));
        tab.State.HoverTarget.ShouldBeNull();
    }

    // Leaving the map needs no clearing code of its own: the resolver already answers null outside
    // the content rect, and the move that leaves writes that null through. Pinned so a later
    // "optimisation" that skips resolving off-rect does not quietly strand the wash on screen.
    [Fact]
    public void AMoveOffTheMapRetiresTheWashWithNoCodeOfItsOwn()
    {
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var state = NewState();
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var (nebX, nebY) = Project(state, Nebula.RA, Nebula.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, DateTimeOffset.UtcNow, nebX, nebY, pinnedCatalogIndices: null).ShouldNotBeNull();

        // Same object, pointer now outside the rect the map was given.
        SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, DateTimeOffset.UtcNow, SurfaceSize + 10f, nebY, pinnedCatalogIndices: null)
            .ShouldBeNull();
    }

    // A SkyMapTab over the CPU surface, matching the browser's wiring: the object overlay goes through
    // the shared primitive path and the view matrix is published each frame the way the GPU pipelines
    // do, so hit-testing and drawing agree on where things are.
    // ------------------------------------------------------------------------------------------
    // Against the real catalogue, on the Small Magellanic Cloud: what the pointer resolves to is
    // what is DRAWN, wherever inside the drawn shape the pointer is, for as long as it stays there.
    // ------------------------------------------------------------------------------------------

    private static async System.Threading.Tasks.Task<(HoverTestSkyMapTab Tab, PlannerState Planner, ICelestialObjectDB Db)>
        RealSkyAsync(RgbaImageRenderer renderer, SkyMapMode mode, System.Threading.CancellationToken ct)
    {
        var db = await SharedCatalogDB.InitAsync(ct);
        var tab = new HoverTestSkyMapTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        tab.State.Mode = mode;
        tab.State.ShowObjectOverlay = true;
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = -33.9,
            SiteLongitude = 18.4,
            SiteTimeZone = TimeSpan.Zero,
            PlanningDate = new DateTimeOffset(2026, 9, 22, 20, 0, 0, TimeSpan.Zero),
        };
        return (tab, planner, db);
    }

    /// <summary>
    /// <b>An object the field does not draw cannot take the hover or the click.</b> NGC 265 is a
    /// cluster inside the SMC below the six degree field's magnitude cutoff: the overlay does not
    /// draw it there, so the pointer on it resolves to the galaxy around it. At half a degree it is
    /// drawn, and then it is what the pointer takes, as the nearest centre.
    /// </summary>
    [Theory]
    [InlineData(6.0, false)]
    [InlineData(0.5, true)]
    public async System.Threading.Tasks.Task AnUndrawnClusterInsideTheSmcDoesNotTakeThePointer(double fovDeg, bool clusterIsDrawn)
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(800, 800);
        var (tab, planner, db) = await RealSkyAsync(renderer, SkyMapMode.Equatorial, ct);
        CatalogUtils.TryGetCleanedUpCatalogName("NGC265", out var ngc265).ShouldBeTrue();
        db.TryLookupByIndex(ngc265, out var cluster).ShouldBeTrue();
        ((double)cluster.V_Mag).ShouldBeGreaterThan(OverlayEngine.GetExtendedMagCutoff(6.0 * 60.0),
            "the fixture needs a cluster the six degree field does not draw");
        var time = new FakeTimeProviderWrapper(planner.PlanningDate.Value);
        var rect = new RectF32(0, 0, 800, 800);
        // The first frame with a site places the map's initial view and would override a centre set
        // before it: point the view after that frame.
        tab.Render(planner, rect, time);
        tab.State.CenterRA = cluster.RA;
        tab.State.CenterDec = cluster.Dec;
        tab.State.FieldOfViewDeg = fovDeg;
        tab.Render(planner, rect, time);
        tab.Render(planner, rect, time);

        tab.HandleInput(new InputEvent.MouseMove(401f, 401f));

        tab.State.HoverTarget.ShouldNotBeNull().Index.ShouldBe(clusterIsDrawn ? ngc265 : CatalogIndex.NGC0292);
    }

    /// <summary>
    /// <b>Inside the drawn ellipse, the object is found wherever the pointer is.</b> The resolver's
    /// cell window reaches a degree or so from the pointer, and the SMC's ellipse reaches 2.5; a
    /// pointer 1.8 degrees out along the major axis found nothing, though the outline was drawn
    /// around it. And just outside the ellipse, still inside the circle of its major radius, it
    /// finds nothing: the hit region is the shape that is drawn, not a disc around it.
    /// </summary>
    [Theory]
    [InlineData(1.8, 45.0, true)]
    [InlineData(2.2, 135.0, false)]
    public async System.Threading.Tasks.Task TheSmcIsFoundInsideItsDrawnEllipseAndNotBesideIt(double offsetDeg, double bearingDeg, bool found)
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(800, 800);
        var (tab, planner, db) = await RealSkyAsync(renderer, SkyMapMode.Equatorial, ct);
        db.TryLookupByIndex(CatalogIndex.NGC0292, out var smc).ShouldBeTrue();
        db.TryGetShape(CatalogIndex.NGC0292, out var shape).ShouldBeTrue();
        ((double)shape.PositionAngle).ShouldBe(45.0, 1e-6, "the fixture walks along and across the SMC's own position angle");

        // A point offsetDeg from the SMC's centre along a bearing measured from north through east.
        var (sinB, cosB) = Math.SinCos(double.DegreesToRadians(bearingDeg));
        var dec = smc.Dec + (offsetDeg * cosB);
        var ra = smc.RA + (offsetDeg * sinB / Math.Cos(double.DegreesToRadians(smc.Dec)) / 15.0);

        var time = new FakeTimeProviderWrapper(planner.PlanningDate.Value);
        var rect = new RectF32(0, 0, 800, 800);
        tab.Render(planner, rect, time); // the first frame with a site places the initial view
        tab.State.CenterRA = ra;
        tab.State.CenterDec = dec;
        tab.State.FieldOfViewDeg = 6.0;
        tab.Render(planner, rect, time);
        tab.Render(planner, rect, time);

        tab.HandleInput(new InputEvent.MouseMove(400f, 400f));

        if (found)
        {
            tab.State.HoverTarget.ShouldNotBeNull().Index.ShouldBe(CatalogIndex.NGC0292);
        }
        else
        {
            (tab.State.HoverTarget?.Index).ShouldNotBe(CatalogIndex.NGC0292,
                "outside the ellipse, though inside the circle of its major radius, the SMC is not what is drawn there");
        }
    }

    /// <summary>
    /// <b>A wash under a still pointer survives sidereal drift.</b> In Horizon mode the view centre
    /// moves with time on every frame; the wash used to be dropped whenever the centre was not
    /// bit-identical to the one it was resolved for, so it died a frame after every resolve. The
    /// target is now judged by its own movement on screen, and re-tested at the pointer rather than
    /// dropped when the drift adds up.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task TheWashSurvivesSiderealDriftInHorizonMode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var renderer = new RgbaImageRenderer(800, 800);
        var (tab, planner, db) = await RealSkyAsync(renderer, SkyMapMode.Horizon, ct);
        db.TryLookupByIndex(CatalogIndex.NGC0292, out var smc).ShouldBeTrue();
        var rect = new RectF32(0, 0, 800, 800);
        var t0 = planner.PlanningDate.Value;
        tab.Render(planner, rect, new FakeTimeProviderWrapper(t0)); // the first frame with a site places the initial view
        tab.State.FieldOfViewDeg = 6.0;
        SkyMapViewActions.CenterOn(tab.State, smc.RA, smc.Dec);
        tab.Render(planner, rect, new FakeTimeProviderWrapper(t0));
        tab.Render(planner, rect, new FakeTimeProviderWrapper(t0));

        tab.HandleInput(new InputEvent.MouseMove(400f, 400f));
        tab.State.HoverTarget.ShouldNotBeNull().Index.ShouldBe(CatalogIndex.NGC0292);

        // Frames a second apart for a minute: the sky turns a quarter of a degree, tens of pixels.
        for (var second = 1; second <= 60; second++)
        {
            tab.Render(planner, rect, new FakeTimeProviderWrapper(t0.AddSeconds(second)));
            tab.State.HoverTarget.ShouldNotBeNull($"the wash is still there {second} s after the resolve")
                .Index.ShouldBe(CatalogIndex.NGC0292);
        }
    }

    private sealed class HoverTestSkyMapTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site, phase);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }

        protected override void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
            => RenderObjectOverlayPrimitive(db, contentRect, baseFontSize,
                site, dimBelowHorizon, plannerState, showAllOverlays);
    }

    // Two objects in one cell, one of which may carry a verified article with a picture.
    // A pinned comet is DRAWN with its layer off and below the magnitude limit
    // (SkyMapState.ShouldDrawCometMarker: a planned target is a landmark, and a comet's predicted
    // magnitude is the least reliable number on the map). The resolver has to answer the same rule, or
    // the landmark on screen answers nothing to a click and lights no wash -- which is what the comet
    // pass did once it was gated on the layer and the limit with no pinned exception. The unpinned
    // half is the gate itself: an undrawn comet must not be selectable through apparently-empty sky.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APinnedCometResolvesWhereAnUnpinnedOneIsNotDrawn(bool layerOn)
    {
        // The stub's placeholder orbit sits years past a 2023 perihelion, so at this instant the comet
        // is far out and FAINT: with the layer ON the only thing hiding it is the magnitude gate, and
        // with it OFF the layer gate. It must still be a CANDIDATE (SkyMapState's candidacy filter is
        // on peak brightness, M1 + K1 log q, not on the current magnitude), so M1 stays modest.
        var faint = StubCometRepository.Comet("10P", "Tempel") with { AbsoluteMagnitudeM1 = 12.0 };
        var comets = new StubCometRepository(faint);
        var db = new ArticleDb(Nebula, Star, NebulaShape);
        var viewingUtc = new DateTimeOffset(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);

        var state = NewState();
        state.ShowComets = layerOn;
        var markers = state.GetCometPositionsCached(comets, viewingUtc);
        markers.Length.ShouldBe(1);
        var comet = markers[0];
        // The premise, asserted rather than assumed: the comet IS fainter than the limit in force.
        var limit = Math.Max(SkyMapState.CometBaseMagnitudeLimit, state.EffectiveMagnitudeLimit);
        comet.VMag.ShouldBeGreaterThan(limit);

        // Centre the view on the comet so its marker sits at the middle of the surface.
        state.CenterRA = comet.RA;
        state.CenterDec = comet.Dec;
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var (x, y) = Project(state, comet.RA, comet.Dec);

        SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, viewingUtc, x, y, pinnedCatalogIndices: null, comets)
            .ShouldBeNull();

        var pinned = new HashSet<CatalogIndex> { comet.Index };
        var hit = SkyMapSearchActions.ResolveHoverAtScreenPoint(state, db, viewingUtc, x, y, pinned, comets)
            .ShouldNotBeNull();
        hit.Index.ShouldBe(comet.Index);
        hit.IsEphemeris.ShouldBeTrue();
    }

    private sealed class ArticleDb(
        CelestialObject nebula, CelestialObject star, CelestialObjectShape nebulaShape,
        CatalogIndex withPicture = default) : ICelestialObjectDB
    {
        public IRaDecIndex CoordinateGrid => new FixedIndex(nebula.Index, star.Index);

        public IRaDecIndex DeepSkyCoordinateGrid => new FixedIndex(nebula.Index);

        public IReadOnlySet<CatalogIndex> AllObjectIndices => new HashSet<CatalogIndex> { nebula.Index, star.Index };

        public IReadOnlySet<Catalog> Catalogs => new HashSet<Catalog>();

        public IReadOnlyCollection<string> CommonNames => [];

        public int LastInitProcessed => 0;

        public int LastInitFailed => 0;

        public int HipStarCount => 0;

        public int Tycho2StarCount => 0;

        public bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject)
        {
            if (index == nebula.Index) { celestialObject = nebula; return true; }
            if (index == star.Index) { celestialObject = star; return true; }
            celestialObject = default;
            return false;
        }

        public bool TryGetShape(CatalogIndex index, out CelestialObjectShape shape)
        {
            if (index == nebula.Index) { shape = nebulaShape; return true; }
            shape = default;
            return false;
        }

        // The one method these tests are really about: the picture table the imagery bake produced.
        public bool TryGetArticle(CatalogIndex index, out ObjectArticle article)
        {
            if (withPicture != default && index == withPicture)
            {
                article = new ObjectArticle(1, "Test",
                    new ObjectArticleImage("t.jpg", "ab", "CC BY-SA 4.0", "A", "B", true, 100, 100));
                return true;
            }

            article = default;
            return false;
        }

        public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches)
        {
            matches = [];
            return false;
        }

        public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices)
        {
            crossIndices = new HashSet<CatalogIndex>();
            return false;
        }

        public bool TryLookupHIP(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
        {
            ra = 0;
            dec = 0;
            vMag = float.NaN;
            bv = float.NaN;
            return false;
        }

        public int CopyTycho2Stars(Span<Tycho2StarLite> destination, int startIndex = 0) => 0;

        public System.Threading.Tasks.Task InitDBAsync(
            bool waitForTycho2BulkLoad = false,
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task EnsureTycho2DataLoadedAsync(
            System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        private sealed class FixedIndex(params CatalogIndex[] items) : IRaDecIndex
        {
            public IReadOnlyCollection<CatalogIndex> this[double ra, double dec] => items;
        }
    }
}

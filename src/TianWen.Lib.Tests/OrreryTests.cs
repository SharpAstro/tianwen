using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas's solar system in 3D (docs/plans/atlas-planet-detail.md, A5, #1039): its geometry against the sky's own ephemeris, the
/// camera-viewpoint aspect against <see cref="PhysicalEphemeris.Compute"/>, the way in and out through the panel, Alt+S and Escape, the
/// clock, the cached orbits, and a comet on its own orbit.
/// </summary>
[Collection("Astrometry")]
public sealed class OrreryTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
    private const int Width = 900;
    private const int Height = 600;

    private static double Angle(double ax, double ay, double az, double bx, double by, double bz)
        => Math.Acos(Math.Clamp(((ax * bx) + (ay * by) + (az * bz))
            / Math.Sqrt(((ax * ax) + (ay * ay) + (az * az)) * ((bx * bx) + (by * by) + (bz * bz))), -1, 1)) * 180 / Math.PI;

    private static double Longitude(double x, double y)
    {
        var deg = Math.Atan2(y, x) * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    private static double Wrapped(double deg) => ((deg % 360) + 540) % 360 - 180;

    /// <summary>
    /// The Earth the orrery places, seen from the Sun, is opposite the Sun seen from the Earth: its heliocentric ecliptic longitude is
    /// the Sun's geocentric one (from VSOP87's own sky reduction, J2000 equatorial turned to the ecliptic) plus 180 degrees.
    /// </summary>
    [Fact]
    public void TheEarthsHeliocentricLongitudeIsTheSunsGeocentricPlusAHalfTurn()
    {
        var scene = new OrreryScene();
        scene.Update(Instant, null);
        scene.TryFind(CatalogIndex.Earth, out var earth).ShouldBeTrue();

        VSOP87a.ReduceJ2000(CatalogIndex.Sol, Instant, out var ra, out var dec, out _).ShouldBeTrue();
        Span<double> sun = [Math.Cos(dec * Math.PI / 180) * Math.Cos(ra * Math.PI / 12), Math.Cos(dec * Math.PI / 180) * Math.Sin(ra * Math.PI / 12), Math.Sin(dec * Math.PI / 180)];
        SolarSystemOrbits.EquatorialToEcliptic(sun);

        var difference = Wrapped(Longitude(earth.X, earth.Y) - Longitude(sun[0], sun[1]) - 180);
        output.WriteLine($"Earth {Longitude(earth.X, earth.Y):F4}, Sun {Longitude(sun[0], sun[1]):F4}, off by {difference * 3600:F1} arcsec");
        // The Sun's light time (8.3 minutes, 20 arcsec of the Earth's motion) is the only difference.
        Math.Abs(difference).ShouldBeLessThan(0.01);
    }

    /// <summary>
    /// The angle the view draws at a planet between the Sun and the Earth is <see cref="PlanetAspect.PhaseAngle"/> for the same
    /// instant, at true scale (a compressed scale keeps directions from the Sun only); and the camera-viewpoint aspect, built looking
    /// from the Earth's direction, gives the same phase angle.
    /// </summary>
    [Theory]
    [InlineData(CatalogIndex.Venus)]
    [InlineData(CatalogIndex.Mars)]
    [InlineData(CatalogIndex.Jupiter)]
    public void TheAngleDrawnAtAPlanetIsItsPhaseAngle(CatalogIndex planet)
    {
        var scene = new OrreryScene();
        scene.Update(Instant, null);
        scene.TryFind(planet, out var body).ShouldBeTrue();
        scene.TryFind(CatalogIndex.Earth, out var earthBody).ShouldBeTrue();
        var p = scene.Drawn(OrreryScale.True, body);
        var e = scene.Drawn(OrreryScale.True, earthBody);
        var drawn = Angle(-p.X, -p.Y, -p.Z, e.X - p.X, e.Y - p.Y, e.Z - p.Z);
        var phase = PhysicalEphemeris.Compute(planet, Instant).PhaseAngle;

        Span<double> toEarth = [e.X - p.X, e.Y - p.Y, e.Z - p.Z];
        var norm = Math.Sqrt((toEarth[0] * toEarth[0]) + (toEarth[1] * toEarth[1]) + (toEarth[2] * toEarth[2]));
        for (var k = 0; k < 3; k++)
        {
            toEarth[k] /= norm;
        }
        SolarSystemOrbits.EclipticToEquatorial(toEarth);
        var seen = PhysicalEphemeris.ComputeSeenAlong(planet, Instant, toEarth).PhaseAngle;

        output.WriteLine($"{planet}: drawn {drawn:F4}, aspect {phase:F4}, seen along {seen:F4}");
        // Light time moves the planet by a few arcseconds at most between the two.
        drawn.ShouldBe(phase, 0.01);
        seen.ShouldBe(phase, 0.01);
    }

    /// <summary>
    /// A body between the camera and the Sun shows its night side: seen from outside Jupiter's orbit, on the line from the Sun through
    /// it, the camera-viewpoint aspect's phase angle is 180 degrees (the Earth never sees Jupiter past 12); seen from between it and
    /// the Sun, nought.
    /// </summary>
    [Fact]
    public void ABodyBetweenTheCameraAndTheSunShowsItsNightSide()
    {
        Span<double> helio = stackalloc double[3];
        SolarSystemOrbits.TryGetPosition(CatalogIndex.Jupiter, Instant, helio).ShouldBeTrue();
        var r = Math.Sqrt((helio[0] * helio[0]) + (helio[1] * helio[1]) + (helio[2] * helio[2]));
        Span<double> outward = [helio[0] / r, helio[1] / r, helio[2] / r];
        SolarSystemOrbits.EclipticToEquatorial(outward);
        PhysicalEphemeris.ComputeSeenAlong(CatalogIndex.Jupiter, Instant, outward).PhaseAngle.ShouldBe(180, 0.01);
        for (var k = 0; k < 3; k++)
        {
            outward[k] = -outward[k];
        }
        PhysicalEphemeris.ComputeSeenAlong(CatalogIndex.Jupiter, Instant, outward).PhaseAngle.ShouldBe(0, 0.01);
    }

    /// <summary>A time scrub moves the planets: a day moves the Earth about one degree round the Sun (360 / 365.256), a week seven.</summary>
    [Fact]
    public void AScrubMovesTheEarthAboutADegreeADay()
    {
        var scene = new OrreryScene();
        scene.Update(Instant, null);
        scene.TryFind(CatalogIndex.Earth, out var before).ShouldBeTrue();
        scene.Update(Instant.AddDays(1), null);
        scene.TryFind(CatalogIndex.Earth, out var after).ShouldBeTrue();
        var day = Wrapped(Longitude(after.X, after.Y) - Longitude(before.X, before.Y));
        scene.Update(Instant.AddDays(7), null);
        scene.TryFind(CatalogIndex.Earth, out var week).ShouldBeTrue();
        var weekly = Wrapped(Longitude(week.X, week.Y) - Longitude(before.X, before.Y));
        output.WriteLine($"a day {day:F4} degrees, a week {weekly:F4}");
        // Faster near perihelion (January), slower in July: 0.95 to 1.02.
        day.ShouldBeInRange(0.95, 1.03);
        weekly.ShouldBeInRange(6.6, 7.2);
        scene.PositionBuilds.ShouldBe(3);
    }

    /// <summary>
    /// A comet sits on its own drawn orbit: the position the orrery gives it and the polyline it draws come from the same elements,
    /// so the comet is within a chord's sag of the line, for a closed orbit and an open one.
    /// </summary>
    [Theory]
    [InlineData(0.585, 3.08, 0.0, 12.0, 10.0)]
    [InlineData(0.92, 0.6, 1.0, 140.0, 70.0)]
    [InlineData(1.004, 1.1, 0.0, 30.0, 200.0)]
    public void ACometSitsOnItsOwnDrawnOrbit(double eccentricity, double q, double daysFromPerihelion, double inclination, double node)
    {
        CometDesignation.TryParse("10P", out var designation).ShouldBeTrue();
        var perihelion = 2461330.5;
        var elements = new CometElements(designation, "Test", q, eccentricity, inclination, node, 80, perihelion, perihelion, double.NaN, double.NaN);
        var scene = new OrreryScene();
        var orbit = scene.CometOrbit(elements);
        var sameAgain = scene.CometOrbit(elements);
        ReferenceEquals(orbit, sameAgain).ShouldBeTrue();
        scene.CometOrbitBuilds.ShouldBe(1);

        Span<double> at = stackalloc double[3];
        foreach (var days in new[] { daysFromPerihelion, daysFromPerihelion + 40, daysFromPerihelion - 75 })
        {
            CometEphemeris.TryGetHeliocentricEcliptic(elements, perihelion + days, at, out var r).ShouldBeTrue();
            var nearest = double.MaxValue;
            for (var i = 0; i + 1 < orbit.Length / 3; i++)
            {
                nearest = Math.Min(nearest, SegmentDistance(at, orbit.AsSpan(3 * i, 3), orbit.AsSpan(3 * (i + 1), 3)));
            }
            output.WriteLine($"e {eccentricity}, {days:+0;-0} d: r {r:F3} AU, off the line by {nearest:E2} AU");
            // A chord's sag at 361 points, relative to the distance: well under a thousandth.
            (nearest / r).ShouldBeLessThan(1e-3);
        }
    }

    /// <summary>
    /// An apparition upgrade replaces a comet's elements under the same index while the view is open: the comet's position follows
    /// the NEW elements at once, without a change of time, so it stays on the orbit drawn from them.
    /// </summary>
    [Fact]
    public void NewElementsForTheSameCometMoveItOntoItsNewOrbit()
    {
        CometDesignation.TryParse("12P", out var designation).ShouldBeTrue();
        var perihelion = 2461330.5;
        var before = new CometElements(designation, "Pons-Brooks", 0.9, 0.7, 20, 100, 80, perihelion, perihelion, 8, 10);
        var after = before with { ArgumentOfPerihelionDeg = 140, EpochJdTt = perihelion + 1 };
        var index = before.CatalogIndex.ShouldNotBeNull();

        var scene = new OrreryScene();
        scene.Update(Instant, before);
        scene.Update(Instant, after);
        scene.TryFind(index, out var comet).ShouldBeTrue();

        var orbit = scene.CometOrbit(after);
        Span<double> at = [comet.X, comet.Y, comet.Z];
        var nearest = double.MaxValue;
        for (var i = 0; i + 1 < orbit.Length / 3; i++)
        {
            nearest = Math.Min(nearest, SegmentDistance(at, orbit.AsSpan(3 * i, 3), orbit.AsSpan(3 * (i + 1), 3)));
        }
        var r = Math.Sqrt((comet.X * comet.X) + (comet.Y * comet.Y) + (comet.Z * comet.Z));
        output.WriteLine($"off the new orbit by {nearest:E2} AU at r {r:F3} AU");
        (nearest / r).ShouldBeLessThan(1e-3);
    }

    private static double SegmentDistance(ReadOnlySpan<double> p, ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var (abx, aby, abz) = (b[0] - a[0], b[1] - a[1], b[2] - a[2]);
        var (apx, apy, apz) = (p[0] - a[0], p[1] - a[1], p[2] - a[2]);
        var len = (abx * abx) + (aby * aby) + (abz * abz);
        var t = len == 0 ? 0 : Math.Clamp(((apx * abx) + (apy * aby) + (apz * abz)) / len, 0, 1);
        var (dx, dy, dz) = (apx - (t * abx), apy - (t * aby), apz - (t * abz));
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    // ── Through the tab ──

    private sealed record Rig(SkyMapTab<RgbaImage> Tab, RgbaImageRenderer Renderer, PlannerState Planner, InputRouter Router) : IDisposable
    {
        public RectF32 Content => new(0, 0, Width, Height);

        public void Render() => Tab.Render(Planner, Content, new FakeTimeProviderWrapper(Instant));

        public void Dispose() => Renderer.Dispose();
    }

    private static async Task<Rig> RigAsync(System.Threading.CancellationToken cancellationToken, float dpiScale = 1f)
    {
        var db = await SharedCatalogDB.InitAsync(cancellationToken);
        var renderer = new RgbaImageRenderer(Width, Height);
        var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont(), DpiScale = dpiScale };
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = Instant,
        };
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [tab] };
        var rig = new Rig(tab, renderer, planner, router);
        rig.Render();
        return rig;
    }

    private static void Select(Rig rig, CatalogIndex body)
    {
        rig.Tab.State.TryGetPlanetCached(body, Instant, out var marker).ShouldBeTrue();
        var site = TianWen.Lib.Astrometry.SOFA.SiteContext.Create(48.0, 11.0, Instant);
        rig.Tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(rig.Planner.ObjectDb.ShouldNotBeNull(), marker, 48.0, 11.0, Instant, site);
    }

    // A comet near the Earth's orbit, at perihelion a few days after the instant, with its index in the panel.
    private static CometElements SelectComet(Rig rig, double argumentOfPerihelion = 80)
    {
        CometDesignation.TryParse("12P", out var designation).ShouldBeTrue();
        var perihelion = 2461330.5;
        var elements = new CometElements(designation, "Pons-Brooks", 0.9, 0.7, 20, 100, argumentOfPerihelion, perihelion, perihelion, 8, 10);
        rig.Planner.Comets = new StubCometRepository(elements);
        var index = elements.CatalogIndex.ShouldNotBeNull();
        rig.Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromPosition("12P/Pons-Brooks", 3.0, 10.0, 48.0, 11.0, Instant, default) with { Index = index };
        return elements;
    }

    private static (float X, float Y) DrawnAt(Rig rig, CatalogIndex body)
    {
        var drawn = rig.Tab.OrreryDrawn.Single(d => d.Index == body);
        return (drawn.X, drawn.Y);
    }

    /// <summary>
    /// A click on another body moves the highlight and keeps the comet the view was opened on: Jupiter clicked, the comet and its
    /// orbit are still drawn, and the orbit was not built again.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AClickOnAnotherBodyKeepsTheComet()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        var comet = SelectComet(rig);
        rig.Tab.OpenOrrery();
        rig.Render();
        // Out far enough that Jupiter is on screen beside the comet.
        rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(Math.Sqrt(6));
        rig.Render();
        var cometIndex = comet.CatalogIndex.ShouldNotBeNull();
        rig.Tab.OrreryDrawn.Select(d => d.Index).ShouldContain(cometIndex);
        rig.Tab.OrreryScene.CometOrbitBuilds.ShouldBe(1);

        var (jx, jy) = DrawnAt(rig, CatalogIndex.Jupiter);
        rig.Tab.HandleInput(new InputEvent.MouseDown(jx, jy)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseUp(jx, jy)).ShouldBeTrue();
        rig.Render();

        rig.Tab.State.Orrery.Focus.ShouldBe(CatalogIndex.Jupiter);
        rig.Tab.OrreryDrawn.Select(d => d.Index).ShouldContain(cometIndex);
        rig.Tab.OrreryScene.CometOrbitBuilds.ShouldBe(1);
    }

    /// <summary>
    /// Play is a standing reason to draw (<see cref="SkyMapState.NeedsContinuousFrames"/>), which the GUI's redraw check asks: the
    /// one-shot <see cref="SkyMapState.NeedsRedraw"/> a played frame sets is cleared by the host after that frame, which left Play at
    /// about a frame a second. It holds while playing however often the flag is cleared, and stops with Pause and with Escape.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PlayIsAStandingReasonToDraw()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.NeedsContinuousFrames.ShouldBeFalse();

        var play = PaintedButtons(rig, "OrreryPlay")[0];
        rig.Router.Handle(new InputEvent.MouseDown(play.X + (play.Width / 2), play.Y + (play.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(play.X + (play.Width / 2), play.Y + (play.Height / 2)));
        rig.Tab.State.Orrery.Playing.ShouldBeTrue();
        for (var frame = 0; frame < 3; frame++)
        {
            rig.Render();
            // What the GUI's OnPostFrame does after every frame.
            rig.Tab.State.NeedsRedraw = false;
            rig.Tab.State.NeedsContinuousFrames.ShouldBeTrue();
        }

        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.NeedsContinuousFrames.ShouldBeFalse();
    }

    private static bool PanelOffersTheOrrery(Rig rig)
        => rig.Tab.GetCapturedLayout().Any(n => n.Node.Hit is HitResult.ButtonHit { Action: ObjectInfoPanel.OrreryAction });

    /// <summary>The panel offers the orrery for a planet, the Sun, the Moon and a comet, and not for a galaxy.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ThePanelOffersTheOrreryForTheSunsBodiesOnly()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        foreach (var body in new[] { CatalogIndex.Jupiter, CatalogIndex.Sol, CatalogIndex.Moon })
        {
            Select(rig, body);
            rig.Render();
            PanelOffersTheOrrery(rig).ShouldBeTrue($"{body} has no orrery button");
        }

        CometDesignation.TryParse("12P", out var comet).ShouldBeTrue();
        comet.TryToCatalogIndex(out var cometIndex).ShouldBeTrue();
        rig.Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromPosition("12P/Pons-Brooks", 3.0, 10.0, 48.0, 11.0, Instant, default) with { Index = cometIndex };
        rig.Render();
        PanelOffersTheOrrery(rig).ShouldBeTrue("a comet has no orrery button");

        rig.Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromPosition("M 51", 13.498, 47.195, 48.0, 11.0, Instant, default) with { Index = CatalogIndex.NGC5194, ObjType = ObjectType.Galaxy };
        rig.Render();
        PanelOffersTheOrrery(rig).ShouldBeFalse("a galaxy is not one of the Sun's");
    }

    /// <summary>
    /// Alt+S, through the router, opens the orrery and leaves the Milky Way layer as it was (the layer toggles ignore modifiers, so an
    /// arm in the key switch would have toggled it); Escape returns to the sky with the selection kept.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AltSOpensTheOrreryAndEscapeReturnsWithTheSelectionKept()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Render();
        var milkyWay = rig.Tab.State.ShowMilkyWay;
        var selection = rig.Tab.State.Search.InfoPanel;

        rig.Router.Handle(new InputEvent.KeyDown(InputKey.S, InputModifier.Alt)).ShouldBeTrue();
        rig.Tab.State.Orrery.IsOpen.ShouldBeTrue();
        rig.Tab.State.Orrery.Focus.ShouldBe(CatalogIndex.Mars);
        rig.Tab.State.ShowMilkyWay.ShouldBe(milkyWay);
        rig.Render();
        rig.Tab.OrreryFrames.ShouldBe(1);

        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.Orrery.IsOpen.ShouldBeFalse();
        rig.Tab.State.Search.InfoPanel.ShouldBe(selection);
        rig.Tab.State.ShowMilkyWay.ShouldBe(milkyWay);
    }

    private static RectF32[] PaintedButtons(Rig rig, params string[] actions)
        => [.. actions.Select(action => rig.Tab.GetCapturedLayout()
            .Single(n => n.Node.Hit is HitResult.ButtonHit { Action: var a } && a == action).Bounds)
            .Select(b => new RectF32(b.X, b.Y, b.Width, b.Height))];

    private static void SideBySide(RectF32[] rects, float left, float right)
    {
        foreach (var rect in rects)
        {
            rect.Width.ShouldBeGreaterThan(0);
            rect.X.ShouldBeGreaterThanOrEqualTo(left - 0.5f);
            (rect.X + rect.Width).ShouldBeLessThanOrEqualTo(right + 0.5f);
        }
        for (var i = 0; i + 1 < rects.Length; i++)
        {
            (rects[i].X + rects[i].Width).ShouldBeLessThanOrEqualTo(rects[i + 1].X + 0.5f, $"button {i} runs into button {i + 1}");
        }
    }

    /// <summary>
    /// At 1x and 1.5x: the panel's four buttons sit side by side inside the panel, the strip's Sky, scale and Play side by side
    /// inside the window's bottom row, and a press where True was PAINTED switches the scale.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ThePanelAndTheStripLayOutAtEitherScale(float dpiScale)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        Select(rig, CatalogIndex.Jupiter);
        rig.Render();
        var panelLeft = 12f * dpiScale;
        SideBySide(PaintedButtons(rig, "ObjectInfoGoto", "ObjectInfoViewInPlanner", "ObjectInfoPinToggle", ObjectInfoPanel.OrreryAction),
            panelLeft, panelLeft + (ObjectInfoPanel.DesignWidth * dpiScale));

        rig.Tab.OpenOrrery();
        rig.Render();
        var strip = PaintedButtons(rig, "OrreryClose", "OrreryScale:Compressed", "OrreryScale:True", "OrreryPlay");
        SideBySide(strip, 0, Width);
        foreach (var rect in strip)
        {
            rect.Y.ShouldBeGreaterThanOrEqualTo(Height - (24f * dpiScale) - 0.5f);
        }

        var trueButton = strip[2];
        rig.Router.Handle(new InputEvent.MouseDown(trueButton.X + (trueButton.Width / 2), trueButton.Y + (trueButton.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(trueButton.X + (trueButton.Width / 2), trueButton.Y + (trueButton.Height / 2)));
        rig.Tab.State.Orrery.Scale.ShouldBe(OrreryScale.True);
    }

    /// <summary>
    /// A drag orbits the camera and builds nothing: the planets' orbits are built once (more than none), the positions once for the
    /// instant, whatever the drag does; a scrub of a day computes the positions again and leaves the orbits alone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADragRebuildsNoOrbit()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        rig.Render();
        await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
        rig.Render();
        var scene = rig.Tab.OrreryScene;
        scene.OrbitBuilds.ShouldBe(1);
        scene.PositionBuilds.ShouldBe(1);

        var az = rig.Tab.State.Orrery.AzimuthDeg;
        rig.Tab.HandleInput(new InputEvent.MouseDown(400, 300, MouseButton.Left, InputModifier.None, 1)).ShouldBeTrue();
        for (var step = 1; step <= 20; step++)
        {
            rig.Tab.HandleInput(new InputEvent.MouseMove(400 + (step * 10), 300 + step)).ShouldBeTrue();
            rig.Render();
        }
        rig.Tab.HandleInput(new InputEvent.MouseUp(600, 320, MouseButton.Left)).ShouldBeTrue();
        rig.Tab.State.Orrery.AzimuthDeg.ShouldNotBe(az);
        scene.OrbitBuilds.ShouldBe(1);
        scene.PositionBuilds.ShouldBe(1);

        rig.Tab.HandleInput(new InputEvent.KeyDown(InputKey.Right)).ShouldBeTrue();
        rig.Render();
        scene.PositionBuilds.ShouldBe(2);
        scene.OrbitBuilds.ShouldBe(1);
        output.WriteLine($"orbit builds {scene.OrbitBuilds}, position builds {scene.PositionBuilds}, last frame {rig.Tab.LastOrreryFrameMs:F2} ms");
    }

    /// <summary>
    /// The orrery through the CPU renderer, true and compressed scale, Jupiter selected: the PNGs a person looks at, written to the
    /// test output, and every planet's sprite drawn once it has landed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOrreryDrawsOnTheCpuSurface()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryTests));
        Directory.CreateDirectory(folder);
        foreach (var scale in new[] { OrreryScale.Compressed, OrreryScale.True })
        {
            rig.Tab.State.Orrery.Scale = scale;
            rig.Tab.OpenOrrery();
            for (var pass = 0; pass < 3; pass++)
            {
                rig.Render();
                if (rig.Tab.PlanetTextures.AtlasLoad is { } atlas)
                {
                    await atlas.WaitAsync(TestContext.Current.CancellationToken);
                }
                await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
                await rig.Tab.OrrerySprites.WhenBuiltAsync();
            }
            rig.Render();

            // Timed over frames that change nothing but the camera, as a drag does; the sprites may rebuild as the angle crosses a bucket.
            var times = new double[20];
            for (var frame = 0; frame < times.Length; frame++)
            {
                rig.Tab.State.Orrery.AzimuthDeg += 0.5;
                rig.Render();
                times[frame] = rig.Tab.LastOrreryFrameMs;
            }
            Array.Sort(times);
            output.WriteLine($"{scale}: frame median {times[times.Length / 2]:F2} ms, max {times[^1]:F2} ms (CPU surface: the raster is the cost)");
            rig.Tab.State.Orrery.AzimuthDeg -= 10;
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
            rig.Render();
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
            rig.Render();

            var path = Path.Combine(folder, $"orrery-{scale.ToString().ToLowerInvariant()}.png");
            await File.WriteAllBytesAsync(path, PngWriter.Encode(rig.Renderer.Surface.Pixels, Width, Height), TestContext.Current.CancellationToken);
            output.WriteLine(path);
            rig.Tab.OrreryDrawn.Select(d => d.Index).ShouldContain(CatalogIndex.Jupiter);
        }
    }
}

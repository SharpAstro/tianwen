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
    internal static readonly DateTimeOffset Instant = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
    internal const int Width = 900;
    internal const int Height = 600;

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
        var p = scene.Drawn(new OrreryScaling(OrreryScale.True, 1), body);
        var e = scene.Drawn(new OrreryScaling(OrreryScale.True, 1), earthBody);
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

    private static double Radius((double X, double Y, double Z) p) => Math.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z));

    /// <summary>
    /// The auto scale (D1): at a reach of an AU it is the identity; at 6 and 45 AU the planets keep their order out from the Sun and
    /// none is drawn within a disc of the Sun on screen; True draws raw AU at every reach.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(6.0)]
    [InlineData(45.0)]
    public void TheAutoScaleIsTrueCloseInAndKeepsTheOrderFarOut(double reach)
    {
        var scene = new OrreryScene();
        scene.Update(Instant, null);
        var auto = new OrreryScaling(OrreryScale.Auto, reach);
        var planets = scene.Bodies.Where(b => SolarSystemOrbits.Planets.Contains(b.Index)).ToArray();
        foreach (var body in planets)
        {
            var truly = Radius((body.X, body.Y, body.Z));
            Radius(scene.Drawn(new OrreryScaling(OrreryScale.True, reach), body)).ShouldBe(truly, truly * 1e-12);
            if (reach <= 1)
            {
                Radius(scene.Drawn(auto, body)).ShouldBe(truly, truly * 1e-9, $"{body.Name} at a reach of an AU");
            }
        }
        planets.OrderBy(b => Radius((b.X, b.Y, b.Z))).Select(b => b.Index)
            .ShouldBe(planets.OrderBy(b => Radius(scene.Drawn(auto, b))).Select(b => b.Index));

        if (reach > 1)
        {
            var camera = new OrreryCamera(-90, 30, OrreryCamera.DistanceToFrame(reach * 1.15), new RectF32(0, 0, Width, Height));
            camera.TryProject(0, 0, 0, out var sunX, out var sunY).ShouldBeTrue();
            foreach (var body in planets)
            {
                var (x, y, z) = scene.Drawn(auto, body);
                if (camera.TryProject(x, y, z, out var bx, out var by))
                {
                    var apart = MathF.Sqrt(((bx - sunX) * (bx - sunX)) + ((by - sunY) * (by - sunY)));
                    output.WriteLine($"reach {reach}: {body.Name} drawn {Radius((x, y, z)):F2} for {Radius((body.X, body.Y, body.Z)):F2} AU, {apart:F0} px from the Sun");
                    apart.ShouldBeGreaterThan(SkyMapTab<RgbaImage>.OrreryRadius(CatalogIndex.Sol), $"{body.Name} sits on the Sun at a reach of {reach} AU");
                }
            }
        }
    }

    /// <summary>
    /// A zoom from 0.25 to 50 AU in 200 log-spaced steps moves no body by more than the zoom's own step on screen (4 % of its
    /// distance from the view's centre, or 3 px): the scale's exponent is continuous in the reach, so no body ever jumps.
    /// </summary>
    [Fact]
    public void AZoomMovesNoBodyByMoreThanTheZoomsOwnStep()
    {
        var scene = new OrreryScene();
        scene.Update(Instant, null);
        var rect = new RectF32(0, 0, Width, Height);
        var previous = new System.Collections.Generic.Dictionary<CatalogIndex, (float X, float Y)>();
        var worst = 0f;
        for (var step = 0; step < 200; step++)
        {
            var reach = 0.25 * Math.Pow(50 / 0.25, step / 199.0);
            var camera = new OrreryCamera(-90, 30, OrreryCamera.DistanceToFrame(reach * 1.15), rect);
            var scaling = new OrreryScaling(OrreryScale.Auto, reach);
            foreach (var body in scene.Bodies)
            {
                var (x, y, z) = scene.Drawn(scaling, body);
                if (!camera.TryProject(x, y, z, out var sx, out var sy) || sx < 0 || sx > Width || sy < 0 || sy > Height)
                {
                    previous.Remove(body.Index);
                    continue;
                }
                if (previous.TryGetValue(body.Index, out var was))
                {
                    var moved = MathF.Sqrt(((sx - was.X) * (sx - was.X)) + ((sy - was.Y) * (sy - was.Y)));
                    var fromCentre = MathF.Sqrt(((sx - (Width / 2f)) * (sx - (Width / 2f))) + ((sy - (Height / 2f)) * (sy - (Height / 2f))));
                    var allowed = MathF.Max(3f, 0.04f * fromCentre);
                    worst = MathF.Max(worst, moved / allowed);
                    moved.ShouldBeLessThanOrEqualTo(allowed, $"{body.Name} jumped {moved:F1} px at a reach of {reach:F2} AU");
                }
                previous[body.Index] = (sx, sy);
            }
        }
        output.WriteLine($"the largest step was {worst:P0} of what the zoom's own step allows");
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

    internal sealed record Rig(SkyMapTab<RgbaImage> Tab, RgbaImageRenderer Renderer, PlannerState Planner, InputRouter Router) : IDisposable
    {
        public RectF32 Content => new(0, 0, Width, Height);

        public void Render() => Tab.Render(Planner, Content, new FakeTimeProviderWrapper(Instant));

        public void Dispose() => Renderer.Dispose();
    }

    /// <summary>The app's own text face (BundledFonts' DejaVu Sans), the same file on every platform, copied beside the tests.</summary>
    internal static string AppFace => Path.Combine(AppContext.BaseDirectory, "TestFonts", "DejaVuSans.ttf");

    /// <param name="fontPath">The face the tab draws with; the platform's monospace default when null (Consolas on Windows,
    /// DejaVu Sans Mono on Linux, about a tenth wider).</param>
    internal static async Task<Rig> RigAsync(System.Threading.CancellationToken cancellationToken, float dpiScale = 1f, string? fontPath = null)
    {
        var db = await SharedCatalogDB.InitAsync(cancellationToken);
        var renderer = new RgbaImageRenderer(Width, Height);
        var tab = new SkyMapTab<RgbaImage>(renderer)
        {
            FontPath = fontPath ?? FontResolver.ResolveSystemFont(),
            DpiScale = dpiScale,
        };
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = Instant,
        };
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [tab] };
        var rig = new Rig(tab, renderer, planner, router);
        rig.Render();
        return rig;
    }

    internal static void Select(Rig rig, CatalogIndex body)
    {
        rig.Tab.State.TryGetPlanetCached(body, Instant, out var marker).ShouldBeTrue();
        var site = TianWen.Lib.Astrometry.SOFA.SiteContext.Create(48.0, 11.0, Instant);
        rig.Tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(rig.Planner.ObjectDb.ShouldNotBeNull(), marker, 48.0, 11.0, Instant, site);
    }

    // A comet near the Earth's orbit, at perihelion a few days after the instant, with its index in the panel.
    internal static CometElements SelectComet(Rig rig, double argumentOfPerihelion = 80)
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

        // The first Escape closes the panel the view opened with (#1656), the second the view, which stops Play.
        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.NeedsContinuousFrames.ShouldBeFalse();
    }

    /// <summary>
    /// A pinch is a zoom and nothing else, as on the sky: the first finger's press began a drag, which the pinch undoes; the touchpad's
    /// duplicate wheel event right after it is ignored, so the zoom is applied once; and the first finger's release is not a click.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APinchZoomsOnceAndCancelsTheDrag()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        rig.Render();
        var orrery = rig.Tab.State.Orrery;
        var (az, el, distance) = (orrery.AzimuthDeg, orrery.ElevationDeg, orrery.Distance);

        rig.Tab.HandleInput(new InputEvent.MouseDown(400, 300)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseMove(420, 310)).ShouldBeTrue();
        orrery.AzimuthDeg.ShouldNotBe(az);

        rig.Tab.HandleInput(new InputEvent.Pinch(1.25f, 420, 310)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.Scroll(1, 420, 310)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.PinchEnd()).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseUp(420, 310));

        orrery.AzimuthDeg.ShouldBe(az);
        orrery.ElevationDeg.ShouldBe(el);
        orrery.Distance.ShouldBe(distance / 1.25, distance * 1e-6);
        orrery.Focus.ShouldBe(CatalogIndex.Mars);
    }

    private static bool Intersect(RectF32 a, RectF32 b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static RectF32 Disc((CatalogIndex Index, float X, float Y, float Radius) d)
        => new(d.X - d.Radius, d.Y - d.Radius, 2 * d.Radius, 2 * d.Radius);

    /// <summary>
    /// The Earth and the Moon never overprint, opened on the Moon (framed on the Earth's distance) and on Jupiter (the inner system
    /// small): their discs apart, and no label of one on the other's disc or label. The focus body's label is always drawn.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Moon, 1f)]
    [InlineData(CatalogIndex.Moon, 1.5f)]
    [InlineData(CatalogIndex.Jupiter, 1f)]
    [InlineData(CatalogIndex.Jupiter, 1.5f)]
    [InlineData(CatalogIndex.Moon, 1f, 0.35)]
    [InlineData(CatalogIndex.Moon, 1.5f, 0.35)]
    public async Task TheEarthAndTheMoonDoNotOverprint(CatalogIndex focus, float dpiScale, double zoom = 1.0)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        Select(rig, focus);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.Orrery.Distance *= zoom;
        rig.Render();

        var earth = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Earth);
        if (!MoonDrawnOrCrowdedOut(rig, out var moon))
        {
            output.WriteLine($"{focus}: the Moon is not drawn, Venus being within twice its reach of the Earth");
            rig.Tab.OrreryLabels.Select(l => l.Index).ShouldContain(CatalogIndex.Earth);
            return;
        }
        output.WriteLine($"{focus}: Earth at ({earth.X:F1}, {earth.Y:F1}) r {earth.Radius}, Moon at ({moon.X:F1}, {moon.Y:F1}) r {moon.Radius}");
        Intersect(Disc(earth), Disc(moon)).ShouldBeFalse("the Earth's and the Moon's discs overlap");

        var labels = rig.Tab.OrreryLabels;
        labels.Select(l => l.Index).ShouldContain(focus);
        foreach (var (index, rect) in labels.Where(l => l.Index is CatalogIndex.Earth or CatalogIndex.Moon))
        {
            var other = index == CatalogIndex.Earth ? moon : earth;
            Intersect(rect, Disc(other)).ShouldBeFalse($"{index}'s label lies on the other's disc");
            Intersect(rect, Disc(index == CatalogIndex.Earth ? earth : moon)).ShouldBeFalse($"{index}'s label lies on its own disc");
            foreach (var (otherIndex, otherRect) in labels)
            {
                if (otherIndex != index)
                {
                    Intersect(rect, otherRect).ShouldBeFalse($"{index}'s label overprints {otherIndex}'s");
                }
            }
        }
    }

    /// <summary>
    /// Ten minutes on another tab is one frame's gap when the view comes back: Play steps at most
    /// <see cref="SkyMapTab{TSurface}.MaxPlayStep"/> of the wall clock a frame (seven tenths of a day at the default week a second), never years.
    /// A frame a sixtieth of a second on steps the sixtieth.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PlayCannotJumpAfterTimeAway()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        var clock = new FakeTimeProviderWrapper(Instant);
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        var play = PaintedButtons(rig, "OrreryPlay")[0];
        rig.Router.Handle(new InputEvent.MouseDown(play.X + (play.Width / 2), play.Y + (play.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(play.X + (play.Width / 2), play.Y + (play.Height / 2)));
        rig.Tab.Render(rig.Planner, rig.Content, clock);

        var orrery = rig.Tab.State.Orrery;
        var before = rig.Tab.State.TimeOffset;
        clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        (rig.Tab.State.TimeOffset - before).TotalDays.ShouldBe(orrery.DaysPerSecond / 60, 1e-3);

        before = rig.Tab.State.TimeOffset;
        clock.Advance(TimeSpan.FromMinutes(10));
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        var step = rig.Tab.State.TimeOffset - before;
        output.WriteLine($"ten minutes away stepped {step.TotalDays:F3} days");
        step.TotalDays.ShouldBeLessThanOrEqualTo(orrery.DaysPerSecond * 0.1 + 1e-9);
        step.TotalDays.ShouldBeGreaterThan(0);
    }

    internal static void PressAt(Rig rig, RectF32 rect)
    {
        rig.Router.Handle(new InputEvent.MouseDown(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));
    }

    // The painted row of the open drop-up at index, as a press finds it.
    internal static RectF32 DropUpRow(Rig rig, int index)
        => rig.Tab.GetRegisteredRegions()
            .Where(r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId, Index: var i } && i == index)
            .Select(r => new RectF32(r.X, r.Y, r.Width, r.Height))
            .Single();

    /// <summary>
    /// The fifteen speeds of #1573, "1 second = X" from six hours to ten years (TheSkyLive's orrery's), in order, each as many days
    /// as its name says (a month a twelfth of a Julian year).
    /// </summary>
    [Fact]
    public void ThePlaySpeedsAreTheFifteenOfTheIssue()
    {
        const double Year = 365.25;
        OrreryState.PlaySpeeds.Select(s => s.Name).ShouldBe(
            ["6 hours", "12 hours", "1 day", "2 days", "4 days", "1 week", "2 weeks", "1 month", "2 months", "4 months", "6 months",
             "1 year", "2 years", "5 years", "10 years"]);
        OrreryState.PlaySpeeds.Select(s => s.DaysPerSecond).ShouldBe(
            [0.25, 0.5, 1, 2, 4, 7, 14, Year / 12, Year / 6, Year / 3, Year / 2, Year, 2 * Year, 5 * Year, 10 * Year]);
        new OrreryState().DaysPerSecond.ShouldBe(7, "the default is one week a second, the entry nearest the old ten days");
    }

    /// <summary>
    /// The speed button beside Play opens a drop-up of the fifteen speeds ABOVE it; a press on a row through the router sets the
    /// rate, the button then names it, and a played second of the wall clock (ten frames of <see cref="SkyMapTab{TSurface}.MaxPlayStep"/>)
    /// moves the atlas's clock by exactly that span. Picked while playing, so Play keeps running across a pick.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(0, 1.5f)]
    [InlineData(7, 1.5f)]
    [InlineData(14, 1.5f)]
    public async Task EachSpeedRunsItsSpanInAPlayedSecond(int choice, float dpiScale = 1f)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        var clock = new FakeTimeProviderWrapper(Instant);
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        PressAt(rig, PaintedButtons(rig, "OrreryPlay")[0]);
        rig.Tab.Render(rig.Planner, rig.Content, clock);

        var speedButton = PaintedButtons(rig, SkyMapTab<RgbaImage>.OrrerySpeedAction)[0];
        PressAt(rig, speedButton);
        rig.Tab.SpeedMenu.IsOpen.ShouldBeTrue();
        rig.Tab.SpeedMenu.Items.Select(i => i.Label).ShouldBe(OrreryState.PlaySpeeds.Select(s => $"1 second = {s.Name}"));
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        // Every row's text, as PAINTED (the leaf the dropdown laid out inside its own padding), is at least as wide as that text
        // measures: the width is measured, never guessed (ten ems overran "1 second = 6 hours").
        foreach (var item in rig.Tab.SpeedMenu.Items)
        {
            var (node, bounds) = rig.Tab.GetCapturedLayout()
                .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value == item.Label);
            bounds.Width.ShouldBeGreaterThanOrEqualTo(rig.Tab.StripContentWidth(node) - 0.5f, $"\"{item.Label}\" overruns its row");
        }
        var row = DropUpRow(rig, choice);
        (row.Y + row.Height).ShouldBeLessThanOrEqualTo(speedButton.Y + 0.5f, "a drop-up opens above the strip");
        PressAt(rig, row);

        var speed = OrreryState.PlaySpeeds[choice];
        rig.Tab.SpeedMenu.IsOpen.ShouldBeFalse();
        rig.Tab.State.Orrery.DaysPerSecond.ShouldBe(speed.DaysPerSecond);
        rig.Tab.State.Orrery.Playing.ShouldBeTrue("a pick does not stop Play");
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        var speedLabel = $"{speed.Name}/s";
        var compactLabel = $"{speed.ShortName}/s";
        var speedLeaf = rig.Tab.GetCapturedLayout()
            .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && (text.Value == speedLabel || text.Value == compactLabel));
        if (dpiScale == 1f)
        {
            rig.Tab.StripShownText(speedLeaf).ShouldBe(speedLabel, "the speed's name is trimmed at 1x: the width sample is narrower than it");
        }

        var before = rig.Tab.State.TimeOffset;
        for (var frame = 0; frame < 10; frame++)
        {
            clock.Advance(SkyMapTab<RgbaImage>.MaxPlayStep);
            rig.Tab.Render(rig.Planner, rig.Content, clock);
        }
        output.WriteLine($"{speed.Name}: a played second moved the clock {(rig.Tab.State.TimeOffset - before).TotalDays:F4} days");
        (rig.Tab.State.TimeOffset - before).TotalDays.ShouldBe(speed.DaysPerSecond, 1e-6);
    }

    /// <summary>
    /// A played offset reads in years once it passes one (a Julian year, 365.25 days), so the strip's time stays short at ten years a
    /// second: "+5214w" was what a century read as.
    /// </summary>
    [Theory]
    [InlineData(365.25, "+1y")]
    [InlineData(-365.25, "-1y")]
    [InlineData(100 * 365.25, "+100y")]
    [InlineData((5 * 365.25) + 14, "+5y 2w")]
    [InlineData(1974 * 365.25, "+1974y")]
    [InlineData(364, "+52w")]
    public void APlayedOffsetReadsInYears(double days, string expected)
        => SkyMapState.FormatOffset(TimeSpan.FromDays(days)).ShouldBe(expected);

    /// <summary>
    /// At ten years a second the clock reaches the end of the ephemeris's range within minutes, and past DateTimeOffset's year 9999
    /// within hours, which threw in the render path: Play stops AT <see cref="OrreryState.PlayEnd"/>, the frame drawn there.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task PlayAtTenYearsASecondStopsAtTheEndOfTheEphemeris()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        var clock = new FakeTimeProviderWrapper(Instant);
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        var orrery = rig.Tab.State.Orrery;
        orrery.DaysPerSecond = OrreryState.PlaySpeeds[^1].DaysPerSecond;
        // Two years short of the end: a played second is ten.
        rig.Tab.State.TimeOffset = OrreryState.PlayEnd.AddYears(-2) - Instant;
        PressAt(rig, PaintedButtons(rig, "OrreryPlay")[0]);
        rig.Tab.Render(rig.Planner, rig.Content, clock);

        for (var frame = 0; frame < 10; frame++)
        {
            clock.Advance(SkyMapTab<RgbaImage>.MaxPlayStep);
            rig.Tab.Render(rig.Planner, rig.Content, clock);
        }
        orrery.Playing.ShouldBeFalse("Play ran past the end of the ephemeris");
        (Instant + rig.Tab.State.TimeOffset).ShouldBe(OrreryState.PlayEnd);

        // Scrubbed past the end and played: the first played frame brings the clock back to the end, never past year 9999.
        rig.Tab.State.TimeOffset = DateTimeOffset.MaxValue.AddDays(-1) - Instant;
        PressAt(rig, PaintedButtons(rig, "OrreryPlay")[0]);
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        clock.Advance(SkyMapTab<RgbaImage>.MaxPlayStep);
        rig.Tab.Render(rig.Planner, rig.Content, clock);
        orrery.Playing.ShouldBeFalse();
        (Instant + rig.Tab.State.TimeOffset).ShouldBe(OrreryState.PlayEnd);
    }

    /// <summary>
    /// Over a lunar month in six-hour steps, as Play runs it, the Earth's label is always drawn, and the Moon's, whenever drawn, lies
    /// on the far side of the Moon from the Earth: the labels never swap as the Moon circles.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Jupiter, 1.0)]
    [InlineData(CatalogIndex.Moon, 1.0)]
    [InlineData(CatalogIndex.Moon, 0.35)]
    [InlineData(CatalogIndex.Moon, 0.2)]
    public async Task TheEarthAndMoonLabelsNeverSwapOverALunarMonth(CatalogIndex focus, double zoom)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, focus);
        rig.Tab.OpenOrrery();
        rig.Render();
        // The Moon's reach fits the gap to the neighbouring orbits once they are built (before, the gap to the two planets).
        await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
        await rig.Tab.OrreryScene.WhenCometsRankedAsync();
        // Zoomed in by this factor on the framing the view opened with.
        rig.Tab.State.Orrery.Distance *= zoom;
        var moonDrawn = 0;
        var earthOut = 0;
        const int Frames = 120;
        var apart = new System.Collections.Generic.List<float>();
        for (var frame = 0; frame < Frames; frame++)
        {
            rig.Tab.State.TimeOffset = TimeSpan.FromHours(6 * frame);
            rig.Render();
            var labels = rig.Tab.OrreryLabels;
            var earth = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Earth);
            // Zoomed in on the Sun the Earth leaves the view during the month, and a body out of the view has no label (A2 of #1611).
            if (earth.X < 0 || earth.X > Width || earth.Y < 0 || earth.Y > Height)
            {
                earthOut++;
                continue;
            }
            labels.Select(l => l.Index).ShouldContain(CatalogIndex.Earth, $"frame {frame}: the Earth's label is missing");
            if (!MoonDrawnOrCrowdedOut(rig, out var moon))
            {
                continue;
            }
            apart.Add(MathF.Sqrt(((moon.X - earth.X) * (moon.X - earth.X)) + ((moon.Y - earth.Y) * (moon.Y - earth.Y))));
            apart[^1].ShouldBe(rig.Tab.LastMoonReach, 0.5f, $"frame {frame}: the Moon is not its reach from the Earth");
            if (labels.Where(l => l.Index == CatalogIndex.Moon).Select(l => l.Rect).FirstOrDefault() is { Width: > 0 } label)
            {
                moonDrawn++;
                var (lx, ly) = (label.X + (label.Width / 2), label.Y + (label.Height / 2));
                var (ux, uy) = (moon.X - earth.X, moon.Y - earth.Y);
                // The label's centre is beyond the Moon along the Earth-to-Moon direction.
                (((lx - moon.X) * ux) + ((ly - moon.Y) * uy)).ShouldBeGreaterThan(0, $"frame {frame}: the Moon's label is on the Earth's side");
            }
        }
        output.WriteLine($"{focus} x{zoom}: the Earth out of the view in {earthOut} of {Frames} frames, the Moon drawn in {apart.Count}, its label in {moonDrawn}");
        // At x0.2 the Earth leaves the view after about eleven days of the month (76 of 120 frames out): a third of it is still judged.
        (Frames - earthOut).ShouldBeGreaterThan(Frames / 4, "the Earth was out of the view nearly all month: the test no longer frames it");
        if (apart.Count == 0)
        {
            // At the default framing of 2026-10-13 Venus is days from inferior conjunction, beside the Earth: the Moon stays hidden.
            zoom.ShouldBe(1.0, "zoomed in, the Moon must be drawn");
            return;
        }
        output.WriteLine($"{focus} x{zoom}: the drawn Earth-Moon distance runs {apart.Min():F1} to {apart.Max():F1} px over the month: "
            + string.Join(" ", apart.Where((_, i) => i % 6 == 0).Select(d => d.ToString("F0"))));
        // A steady size: the drawn distance moves only as the gap to the next orbit does, a few percent at most from one six-hour
        // frame to the next, never swelling and shrinking with the month (a spiral).
        for (var i = 1; i < apart.Count; i++)
        {
            (MathF.Max(apart[i], apart[i - 1]) / MathF.Min(apart[i], apart[i - 1])).ShouldBeLessThan(1.05f, $"the Moon's reach jumps at frame {i}");
        }
        moonDrawn.ShouldBeGreaterThan(apart.Count / 2);
    }

    /// <summary>
    /// Over a lunar month in six-hour steps, at the view's tilt, the Moon goes round the Earth evenly on screen: every step turns it the
    /// same way, and the largest step is within the Moon's own range of speed (about 1.3 between perigee and apogee) of the smallest.
    /// Turned by its direction as PROJECTED, it raced through the line of sight and crawled across the sides, four times as fast at a
    /// 30 degree tilt, which the owner saw as a Moon wobbling round the Earth.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(30.0)]
    [InlineData(-45.0)]
    public async Task TheMoonCirclesTheEarthEvenly(double elevationDeg)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Moon);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.Orrery.ElevationDeg = elevationDeg;
        rig.Tab.State.Orrery.Distance *= 0.35;
        var steps = new System.Collections.Generic.List<double>();
        double? last = null;
        for (var frame = 0; frame < 120; frame++)
        {
            rig.Tab.State.TimeOffset = TimeSpan.FromHours(6 * frame);
            rig.Render();
            var drawn = rig.Tab.OrreryDrawn;
            var earth = drawn.Single(d => d.Index == CatalogIndex.Earth);
            var moon = drawn.FirstOrDefault(d => d.Index == CatalogIndex.Moon);
            moon.Index.ShouldBe(CatalogIndex.Moon, $"frame {frame}: zoomed in, the Moon must be drawn");
            var angle = Math.Atan2(moon.Y - earth.Y, moon.X - earth.X) * 180 / Math.PI;
            if (last is { } previous)
            {
                steps.Add(Math.IEEERemainder(angle - previous, 360));
            }
            last = angle;
        }
        var magnitudes = steps.Select(Math.Abs).ToArray();
        output.WriteLine($"tilt {elevationDeg}: steps of {magnitudes.Min():F2} to {magnitudes.Max():F2} degrees");
        steps.All(s => Math.Sign(s) == Math.Sign(steps[0])).ShouldBeTrue("the Moon turned back on itself");
        (magnitudes.Max() / magnitudes.Min()).ShouldBeLessThan(1.45);
    }

    /// <summary>
    /// The Moon is drawn no farther from the Earth than half the screen gap to the nearest neighbouring orbit as drawn (Venus's and
    /// Mars's), so it never sits on another planet's orbit; it comes in toward the Earth to fit, and is hidden only when even its
    /// nearest place (clear of the Earth's disc) would not fit. Measured against the drawn ORBITS, which the owner saw it sitting on
    /// while the rule kept it half the gap from Venus the planet.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Moon, 1.0)]
    [InlineData(CatalogIndex.Moon, 0.35)]
    [InlineData(CatalogIndex.Jupiter, 1.0)]
    public async Task TheMoonStaysInsideHalfTheGapToTheNeighbouringOrbits(CatalogIndex focus, double zoom)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, focus);
        rig.Tab.OpenOrrery();
        rig.Render();
        await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
        await rig.Tab.OrreryScene.WhenCometsRankedAsync();
        rig.Tab.State.Orrery.Distance *= zoom;
        var (drawnFrames, hiddenFrames) = (0, 0);
        for (var frame = 0; frame < 120; frame += 4)
        {
            rig.Tab.State.TimeOffset = TimeSpan.FromHours(6 * frame);
            rig.Render();
            var drawn = rig.Tab.OrreryDrawn;
            var earth = drawn.Single(d => d.Index == CatalogIndex.Earth);
            var gap = NeighbourOrbitGap(rig, earth.X, earth.Y);
            var moon = drawn.FirstOrDefault(d => d.Index == CatalogIndex.Moon);
            if (moon.Index == CatalogIndex.Moon)
            {
                drawnFrames++;
                MathF.Sqrt(((moon.X - earth.X) * (moon.X - earth.X)) + ((moon.Y - earth.Y) * (moon.Y - earth.Y)))
                    .ShouldBeLessThanOrEqualTo((gap / 2) + 0.5f, $"frame {frame}: the Moon is more than half the gap to the next orbit out");
            }
            else
            {
                hiddenFrames++;
                // Hidden only where even the nearest place clear of the Earth's disc (the two radii and the 4 px gap) would not fit.
                (gap / 2).ShouldBeLessThan(6f + 3.5f + 4f, $"frame {frame}: the Moon is hidden with room for it");
            }
        }
        output.WriteLine($"{focus} x{zoom}: the Moon drawn in {drawnFrames} frames, hidden in {hiddenFrames}");
    }

    // The screen distance from (x, y) to the nearer of Venus's and Mars's orbits as the view draws them: each orbit's samples scaled
    // and projected, and the distance to every segment between two that project.
    private static float NeighbourOrbitGap(Rig rig, float x, float y)
    {
        var orbits = rig.Tab.OrreryScene.Orbits.ShouldNotBeNull();
        var camera = CameraOf(rig);
        var scaling = new OrreryScaling(rig.Tab.State.Orrery.Scale, OrreryScaling.ReachFor(rig.Tab.State.Orrery.Distance));
        var best = float.PositiveInfinity;
        foreach (var planet in new[] { CatalogIndex.Venus, CatalogIndex.Mars })
        {
            var points = orbits[planet];
            (float X, float Y)? previous = null;
            for (var i = 0; i <= points.Length / 3; i++)
            {
                var j = i % (points.Length / 3);
                var (wx, wy, wz) = OrreryScene.Scaled(scaling, points[3 * j], points[(3 * j) + 1], points[(3 * j) + 2]);
                if (!camera.TryProject(wx, wy, wz, out var sx, out var sy))
                {
                    previous = null;
                    continue;
                }
                if (previous is var (px, py))
                {
                    var (dx, dy) = (sx - px, sy - py);
                    var len = (dx * dx) + (dy * dy);
                    var t = len == 0 ? 0 : Math.Clamp((((x - px) * dx) + ((y - py) * dy)) / len, 0, 1);
                    var (cx, cy) = (px + (t * dx) - x, py + (t * dy) - y);
                    best = MathF.Min(best, MathF.Sqrt((cx * cx) + (cy * cy)));
                }
                previous = (sx, sy);
            }
        }
        return best;
    }

    // Sirius (HIP 32349) and Polaris (HIP 11767), J2000.
    private static readonly (string Name, double RaHours, double DecDeg)[] BrightStars =
    [
        ("Sirius", 6.752478, -16.716116),
        ("Polaris", 2.530303, 89.264109),
    ];

    // Independent of the code under test: the obliquity of J2000 by its IAU value, a plain rotation about x, then the orrery camera's
    // own projection of a point a billion AU along the star's ecliptic direction (translation then negligible: 6e-9 radians).
    private static bool WhereTheCameraPutsAStar(in OrreryCamera camera, double raHours, double decDeg, out float x, out float y)
    {
        const double Obliquity = 23.4392911 * Math.PI / 180;
        var (ra, dec) = (raHours * Math.PI / 12, decDeg * Math.PI / 180);
        var (ex, ey, ez) = (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        var (lx, ly, lz) = (ex, (Math.Cos(Obliquity) * ey) + (Math.Sin(Obliquity) * ez), (-Math.Sin(Obliquity) * ey) + (Math.Cos(Obliquity) * ez));
        const double Far = 1e9;
        return camera.TryProject(lx * Far, ly * Far, lz * Far, out x, out y);
    }

    private static OrreryCamera CameraOf(Rig rig)
        => new(rig.Tab.State.Orrery.AzimuthDeg, rig.Tab.State.Orrery.ElevationDeg, rig.Tab.State.Orrery.Distance, rig.Content);

    /// <summary>
    /// The backdrop (the sky map's own stars, at infinity): a bright star lands where the camera's rotation and the obliquity put it,
    /// within a pixel; it stays put on screen while a scrub of thirty days moves the planets; and it moves when the camera orbits.
    /// Turned to face each star in turn, so the star is on screen.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TheBackdropsStarsTurnWithTheCameraAndNotWithTime(int star)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        var (name, raHours, decDeg) = BrightStars[star];
        // Face the star: the camera sits opposite it about the Sun, so the star's ecliptic direction is the view's axis.
        Span<double> dir = [Math.Cos(decDeg * Math.PI / 180) * Math.Cos(raHours * Math.PI / 12), Math.Cos(decDeg * Math.PI / 180) * Math.Sin(raHours * Math.PI / 12), Math.Sin(decDeg * Math.PI / 180)];
        SolarSystemOrbits.EquatorialToEcliptic(dir);
        rig.Tab.State.Orrery.AzimuthDeg = (Math.Atan2(-dir[1], -dir[0]) * 180 / Math.PI) + 3;
        rig.Tab.State.Orrery.ElevationDeg = Math.Clamp(-Math.Asin(dir[2]) * 180 / Math.PI, -80, 80);
        rig.Render();
        rig.Render();

        var backdrop = rig.Tab.LastOrreryBackdrop.ShouldNotBeNull("the orrery drew no backdrop");
        var camera = CameraOf(rig);
        var (cx, cy) = (rig.Content.X + (rig.Content.Width / 2), rig.Content.Y + (rig.Content.Height / 2));
        SkyMapProjection.ProjectGnomonicWithMatrix(raHours, decDeg, backdrop.View, backdrop.PixelsPerRadian, cx, cy, out var sx, out var sy).ShouldBeTrue();
        WhereTheCameraPutsAStar(camera, raHours, decDeg, out var ex, out var ey).ShouldBeTrue();
        output.WriteLine($"{name}: drawn at ({sx:F2}, {sy:F2}), the camera's math ({ex:F2}, {ey:F2})");
        sx.ShouldBe(ex, 1f);
        sy.ShouldBe(ey, 1f);

        var mars = DrawnAt(rig, CatalogIndex.Mars);
        rig.Tab.State.TimeOffset = TimeSpan.FromDays(30);
        rig.Render();
        DrawnAt(rig, CatalogIndex.Mars).ShouldNotBe(mars, "a month's scrub moved no planet");
        var after = rig.Tab.LastOrreryBackdrop.ShouldNotBeNull();
        SkyMapProjection.ProjectGnomonicWithMatrix(raHours, decDeg, after.View, after.PixelsPerRadian, cx, cy, out var tx, out var ty).ShouldBeTrue();
        tx.ShouldBe(sx, 1e-3f);
        ty.ShouldBe(sy, 1e-3f);

        rig.Tab.State.Orrery.AzimuthDeg += 5;
        rig.Render();
        var orbited = rig.Tab.LastOrreryBackdrop.ShouldNotBeNull();
        SkyMapProjection.ProjectGnomonicWithMatrix(raHours, decDeg, orbited.View, orbited.PixelsPerRadian, cx, cy, out var ox, out var oy).ShouldBeTrue();
        WhereTheCameraPutsAStar(CameraOf(rig), raHours, decDeg, out var mx, out var my).ShouldBeTrue();
        Math.Abs(ox - sx).ShouldBeGreaterThan(10f, "an orbit of five degrees left the star where it was");
        ox.ShouldBe(mx, 1f);
        oy.ShouldBe(my, 1f);
    }

    /// <summary>
    /// With the backdrop the orbits and labels stay readable: the backdrop draws stars (it changes some pixels of the frame), and under
    /// every label the backdrop adds at most <see cref="MaxBackdropUnderLabel"/> to any channel, compared with the same frame drawn
    /// without it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheBackdropStaysDimUnderTheLabels()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        rig.Tab.State.Orrery.ElevationDeg = 8;
        await SettleAsync(rig);
        byte[] with = [.. rig.Renderer.Surface.Pixels];
        var labels = rig.Tab.OrreryLabels.Select(l => l.Rect).ToArray();
        rig.Tab.State.Orrery.ShowBackdrop = false;
        rig.Render();
        var without = rig.Renderer.Surface.Pixels;

        var changed = 0;
        for (var i = 0; i < with.Length; i++)
        {
            if (with[i] != without[i])
            {
                changed++;
            }
        }
        changed.ShouldBeGreaterThan(0, "the backdrop drew nothing");

        var worst = 0;
        foreach (var rect in labels)
        {
            for (var y = (int)rect.Y; y < (int)(rect.Y + rect.Height); y++)
            {
                for (var x = (int)rect.X; x < (int)(rect.X + rect.Width); x++)
                {
                    var i = ((y * Width) + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        worst = Math.Max(worst, with[i + c] - without[i + c]);
                    }
                }
            }
        }
        output.WriteLine($"{changed} bytes changed by the backdrop; the most it adds under a label is {worst} of 255");
        worst.ShouldBeLessThanOrEqualTo(MaxBackdropUnderLabel);
    }

    /// <summary>The most the backdrop may add to a channel under a label: under two fifths of full scale, against labels drawn near 224.</summary>
    private const int MaxBackdropUnderLabel = 96;

    internal static async Task SettleAsync(Rig rig)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            rig.Render();
            if (rig.Tab.PlanetTextures.AtlasLoad is { } atlas)
            {
                await atlas.WaitAsync(TestContext.Current.CancellationToken);
            }
            await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
            await rig.Tab.OrreryScene.WhenCometsRankedAsync();
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
        }
        rig.Render();
    }

    /// <summary>
    /// Zoomed in on the Sun, a body BEHIND it never paints over it: the camera swept round the Sun close in, every frame, any dark
    /// pixel inside the Sun's inner disc lies on a body drawn NEARER the camera than the Sun (a transit), never on one behind it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABodyBehindTheSunNeverPaintsOverIt()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Sol);
        rig.Tab.OpenOrrery();
        rig.Tab.State.Orrery.ShowBackdrop = false;
        await SettleAsync(rig);
        var orrery = rig.Tab.State.Orrery;
        var offending = new System.Collections.Generic.List<string>();
        foreach (var scale in new[] { OrreryScale.Auto, OrreryScale.True })
        foreach (var (distance, elevation) in new[] { (0.3, 4.0), (0.6, 4.0), (0.9, 4.0), (1.5, 4.0), (0.6, 25.0), (1.5, 25.0), (0.6, 60.0) })
        for (var az = 0; az < 360; az += 6)
        {
            orrery.Scale = scale;
            orrery.AzimuthDeg = az;
            orrery.ElevationDeg = elevation;
            orrery.Distance = distance;
            rig.Render();
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
            rig.Render();

            var camera = CameraOf(rig);
            var scene = rig.Tab.OrreryScene;
            scene.TryFind(CatalogIndex.Sol, out var sunBody).ShouldBeTrue();
            var sunDepth = camera.Depth(0, 0, 0);
            var drawn = rig.Tab.OrreryDrawn;
            var sun = drawn.Single(d => d.Index == CatalogIndex.Sol);
            var pixels = rig.Renderer.Surface.Pixels;
            for (var y = (int)(sun.Y - (0.7f * sun.Radius)); y <= (int)(sun.Y + (0.7f * sun.Radius)); y++)
            {
                for (var x = (int)(sun.X - (0.7f * sun.Radius)); x <= (int)(sun.X + (0.7f * sun.Radius)); x++)
                {
                    if (((x - sun.X) * (x - sun.X)) + ((y - sun.Y) * (y - sun.Y)) > 0.49f * sun.Radius * sun.Radius)
                    {
                        continue;
                    }
                    var i = ((y * Width) + x) * 4;
                    // Dark in EVERY channel: a test in the "UI" collection may flip the app-wide theme to Night meanwhile, which
                    // tints the Sun red (its green and blue low), and a night-side disc is dark in all three either way.
                    if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) >= 60)
                    {
                        continue;
                    }
                    // A dark pixel: it must lie on a body drawn nearer than the Sun.
                    var explained = drawn.Any(d => d.Index != CatalogIndex.Sol
                        && ((x - d.X) * (x - d.X)) + ((y - d.Y) * (y - d.Y)) <= d.Radius * d.Radius
                        && scene.TryFind(d.Index, out var body)
                        && scene.Drawn(new OrreryScaling(orrery.Scale, OrreryScaling.ReachFor(orrery.Distance)), body) is var (bx, by, bz)
                        && camera.Depth(bx, by, bz) < sunDepth);
                    if (!explained)
                    {
                        offending.Add($"{scale}, distance {distance}, elevation {elevation}, az {az}: dark pixel ({x}, {y}) on the Sun, no nearer body there");
                        y = int.MaxValue - 1;
                        break;
                    }
                }
            }
        }
        output.WriteLine(offending.Count == 0 ? "no body behind the Sun painted over it" : string.Join("\n", offending.Take(10)));
        offending.ShouldBeEmpty();
    }

    /// <summary>
    /// The Moon as the last frame drew it, true; or false when it was not drawn, which is only allowed where its reach, come in to half
    /// the screen gap to the nearest neighbouring orbit, is short of its nearest place clear of the Earth's disc (the Moon is hidden
    /// rather than drawn on another planet's orbit).
    /// </summary>
    private static bool MoonDrawnOrCrowdedOut(Rig rig, out (CatalogIndex Index, float X, float Y, float Radius) moon)
    {
        var drawn = rig.Tab.OrreryDrawn;
        moon = drawn.FirstOrDefault(d => d.Index == CatalogIndex.Moon);
        if (moon.Index == CatalogIndex.Moon)
        {
            return true;
        }
        rig.Tab.LastMoonReach.ShouldBeLessThan((6f + 3.5f + 4f) * rig.Tab.DpiScale, "the Moon is missing with room for it beside the Earth");
        return false;
    }

    /// <summary>
    /// The zoom stays inside a reach of 0.25 to 50 AU: forty wheel steps either way stop at the two framings.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheZoomStaysBetweenAQuarterAndFiftyAU()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        rig.Render();
        for (var step = 0; step < 40; step++)
        {
            rig.Tab.HandleInput(new InputEvent.Scroll(-3, 450, 300));
        }
        rig.Tab.State.Orrery.Distance.ShouldBe(OrreryCamera.DistanceToFrame(50), 1e-9);
        for (var step = 0; step < 80; step++)
        {
            rig.Tab.HandleInput(new InputEvent.Scroll(3, 450, 300));
        }
        rig.Tab.State.Orrery.Distance.ShouldBe(OrreryCamera.DistanceToFrame(0.25), 1e-9);
    }

    /// <summary>
    /// Fully zoomed out the orbits shrink into the Sun and the discs must not: no disc overlaps the Sun's (a body that would is a dot of
    /// 1.5 px at most), and the Moon, when drawn, stays its reach from the Earth.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FullyZoomedOutNoDiscCoversTheSun()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(50);
        rig.Render();

        var drawn = rig.Tab.OrreryDrawn;
        var sun = drawn.Single(d => d.Index == CatalogIndex.Sol);
        foreach (var body in drawn.Where(d => d.Index != CatalogIndex.Sol))
        {
            var apart = MathF.Sqrt(((body.X - sun.X) * (body.X - sun.X)) + ((body.Y - sun.Y) * (body.Y - sun.Y)));
            output.WriteLine($"{body.Index}: {apart:F1} px from the Sun's centre, radius {body.Radius:F1}");
            if (body.Radius > 1.5f)
            {
                apart.ShouldBeGreaterThanOrEqualTo(sun.Radius + body.Radius, $"{body.Index}'s disc overlaps the Sun's");
            }
        }
        if (drawn.Any(d => d.Index == CatalogIndex.Moon))
        {
            var earth = drawn.Single(d => d.Index == CatalogIndex.Earth);
            var moon = drawn.Single(d => d.Index == CatalogIndex.Moon);
            MathF.Sqrt(((moon.X - earth.X) * (moon.X - earth.X)) + ((moon.Y - earth.Y) * (moon.Y - earth.Y))).ShouldBe(rig.Tab.LastMoonReach, 0.5f);
        }
    }

    /// <summary>
    /// A body NEARER the camera that lies over the Sun's disc (a transit, at a zoomed-out framing) is drawn as a dot, so no disc covers
    /// the Sun: the camera put on the line from the Sun through Venus, beyond it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABodyInFrontOfTheSunIsADot()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Sol);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.OrreryScene.TryFind(CatalogIndex.Venus, out var venus).ShouldBeTrue();
        var r = Math.Sqrt((venus.X * venus.X) + (venus.Y * venus.Y) + (venus.Z * venus.Z));
        rig.Tab.State.Orrery.AzimuthDeg = Math.Atan2(venus.Y, venus.X) * 180 / Math.PI;
        rig.Tab.State.Orrery.ElevationDeg = Math.Asin(venus.Z / r) * 180 / Math.PI;
        rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(20);
        rig.Render();

        var drawn = rig.Tab.OrreryDrawn;
        var sun = drawn.Single(d => d.Index == CatalogIndex.Sol);
        var dot = drawn.Single(d => d.Index == CatalogIndex.Venus);
        MathF.Sqrt(((dot.X - sun.X) * (dot.X - sun.X)) + ((dot.Y - sun.Y) * (dot.Y - sun.Y))).ShouldBeLessThan(sun.Radius, "Venus does not lie over the Sun");
        output.WriteLine($"Venus over the Sun drawn with radius {dot.Radius:F2}");
        dot.Radius.ShouldBeLessThanOrEqualTo(1.5f);
    }

    /// <summary>
    /// The backdrop is stars (and, on the GPU, the Milky Way) and nothing of the solar system: at the opening framing every pixel it
    /// changes lies on a catalogue star the backdrop's view projects there, within the star's drawn radius and its halo.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheBackdropDrawsOnlyCatalogueStars()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        await SettleAsync(rig);
        byte[] with = [.. rig.Renderer.Surface.Pixels];
        var view = rig.Tab.LastOrreryBackdrop.ShouldNotBeNull();
        rig.Tab.State.Orrery.ShowBackdrop = false;
        rig.Render();
        var without = rig.Renderer.Surface.Pixels;

        var db = rig.Planner.ObjectDb.ShouldNotBeNull();
        var stars = new System.Collections.Generic.List<(float X, float Y, float Reach)>();
        for (var hip = 1; hip <= db.HipStarCount; hip++)
        {
            if (db.TryLookupHIP(hip, out var ra, out var dec, out var vMag, out _) && !float.IsNaN(vMag)
                && SkyMapProjection.ProjectGnomonicWithMatrix(ra, dec, view.View, view.PixelsPerRadian, Width / 2f, Height / 2f, out var sx, out var sy))
            {
                stars.Add((sx, sy, SkyMapProjection.StarRadius(vMag, view.FieldOfViewDeg) + 3f));
            }
        }

        var stray = new System.Collections.Generic.List<string>();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                if (with[i] == without[i] && with[i + 1] == without[i + 1] && with[i + 2] == without[i + 2])
                {
                    continue;
                }
                if (!stars.Any(s => ((x - s.X) * (x - s.X)) + ((y - s.Y) * (y - s.Y)) <= s.Reach * s.Reach))
                {
                    stray.Add($"({x}, {y})");
                }
            }
        }
        output.WriteLine($"{stars.Count} stars projected; {stray.Count} changed pixels on no star");
        stray.ShouldBeEmpty();
    }

    /// <summary>
    /// A star drawn dimmed is a flat disc, not banded: every row of a bright star's core carries the same colour. The midpoint fill
    /// drew some rows twice, which a translucent colour shows as bright bands: the dimmed bright stars of the backdrop looked like
    /// banded planets (the pilot's X1).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADimmedStarIsAFlatDiscNotBanded()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        using var image = new RgbaImageRenderer(200, 200);
        // Looking straight at Sirius, the backdrop's brightness.
        const double SiriusRa = 6.752478, SiriusDec = -16.716116;
        Span<double> dir = [Math.Cos(SiriusDec * Math.PI / 180) * Math.Cos(SiriusRa * Math.PI / 12), Math.Cos(SiriusDec * Math.PI / 180) * Math.Sin(SiriusRa * Math.PI / 12), Math.Sin(SiriusDec * Math.PI / 180)];
        SolarSystemOrbits.EquatorialToEcliptic(dir);
        var camera = new OrreryCamera(Math.Atan2(-dir[1], -dir[0]) * 180 / Math.PI, -Math.Asin(dir[2]) * 180 / Math.PI, 6, new RectF32(0, 0, 200, 200));
        var matrix = camera.EquatorialView(out var axis);
        var view = new SkyOrreryBackdrop(matrix, camera.FocalPx, axis, 90, OrreryCamera.FieldOfViewDeg, 0f, SkyMapTab<RgbaImage>.OrreryBackdropBrightness, null);
        SkyMapRenderer.DrawBackdropStars(image.Surface, db, view, 100, 100, 2f);

        var pixels = image.Surface.Pixels;
        var core = (int)(SkyMapProjection.StarRadius(-1.46f, OrreryCamera.FieldOfViewDeg) + 0.5f) - 1;
        var rows = Enumerable.Range(100 - core, (2 * core) + 1).Select(y => pixels[((y * 200) + 100) * 4]).ToArray();
        output.WriteLine($"core radius {core}: the centre column's red down the core: {string.Join(" ", rows)}");
        rows.Distinct().Count().ShouldBe(1, "the core's rows differ: banded");
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

        // The first Escape closes the panel the view opened with (#1656), the second the view.
        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.Orrery.IsOpen.ShouldBeTrue();
        rig.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        rig.Tab.State.Orrery.IsOpen.ShouldBeFalse();
        rig.Tab.State.Search.InfoPanel.ShouldBe(selection);
        rig.Tab.State.ShowMilkyWay.ShouldBe(milkyWay);
    }

    internal static RectF32[] PaintedButtons(Rig rig, params string[] actions)
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
        var strip = PaintedButtons(rig, "OrreryClose", "OrreryScale", "OrreryPlay");
        SideBySide(strip, 0, Width);
        foreach (var rect in strip)
        {
            rect.Y.ShouldBeGreaterThanOrEqualTo(Height - (24f * dpiScale) - 0.5f);
        }

        var scaleButton = strip[1];
        rig.Router.Handle(new InputEvent.MouseDown(scaleButton.X + (scaleButton.Width / 2), scaleButton.Y + (scaleButton.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(scaleButton.X + (scaleButton.Width / 2), scaleButton.Y + (scaleButton.Height / 2)));
        rig.Tab.State.Orrery.Scale.ShouldBe(OrreryScale.True);
    }

    /// <summary>
    /// With a planet in focus the strip offers ONE toggle naming what the camera looks at: a press where it was painted centres the
    /// camera on the planet, which then stays at the view's centre as time runs, and a second press puts the Sun back there. The
    /// camera only looks at another point, so the backdrop's stars (its rotation alone) do not move.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheCentreToggleCentresTheCameraOnTheFocusedPlanet(float dpiScale)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        rig.Render();
        var (cx, cy) = (rig.Content.X + (rig.Content.Width / 2), rig.Content.Y + (rig.Content.Height / 2));
        var view = rig.Tab.LastOrreryBackdrop.ShouldNotBeNull().View;
        PaintedTexts(rig).ShouldContain("Centre: Sun");

        void PressCentre()
        {
            var button = PaintedButtons(rig, "OrreryCentre")[0];
            rig.Router.Handle(new InputEvent.MouseDown(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            rig.Router.Handle(new InputEvent.MouseUp(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            rig.Render();
        }
        (float X, float Y) Drawn(CatalogIndex body) => rig.Tab.OrreryDrawn.Single(d => d.Index == body) is var d ? (d.X, d.Y) : default;

        PressCentre();
        PaintedTexts(rig).ShouldContain("Centre: Jupiter");
        var jupiter = Drawn(CatalogIndex.Jupiter);
        output.WriteLine($"centred: Jupiter at ({jupiter.X:F1}, {jupiter.Y:F1}), the view's centre ({cx}, {cy})");
        jupiter.X.ShouldBe(cx, 1f);
        jupiter.Y.ShouldBe(cy, 1f);
        rig.Tab.LastOrreryBackdrop.ShouldNotBeNull().View.ShouldBe(view);

        rig.Tab.State.TimeOffset = TimeSpan.FromDays(60);
        rig.Render();
        jupiter = Drawn(CatalogIndex.Jupiter);
        jupiter.X.ShouldBe(cx, 1f, "the camera did not follow Jupiter");
        jupiter.Y.ShouldBe(cy, 1f, "the camera did not follow Jupiter");

        PressCentre();
        PaintedTexts(rig).ShouldContain("Centre: Sun");
        var sun = Drawn(CatalogIndex.Sol);
        sun.X.ShouldBe(cx, 1f);
        sun.Y.ShouldBe(cy, 1f);
    }

    /// <summary>
    /// The speed button always names the speed in force, whole: its long form ("1 week/s") where the strip has room, and its compact
    /// form ("1w/s") where it is short, never a trimmed "1…" (round 2 of #1613: at 1.5x on 900 px the button's floor held its
    /// padding, gap and mark, and the text got 21 to 28 px of its 91). With the strip as full as it gets (the Centre toggle, Pause,
    /// Now, a long offset), at 1x, 1.25x and 1.5x, in the platform's face and the app's.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, false)]
    [InlineData(1.25f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.25f, true)]
    [InlineData(1.5f, true)]
    public async Task TheSpeedButtonAlwaysNamesItsSpeed(float dpiScale, bool appFace)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale, appFace ? AppFace : null);
        Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.TimeOffset = TimeSpan.FromDays((93 * 7) + 1);
        rig.Tab.State.Orrery.Playing = true;

        foreach (var (days, longForm, compactForm) in new[] { (7.0, "1 week/s", "1w/s"), (0.25, "6 hours/s", "6h/s"),
                     (365.25 / 6, "2 months/s", "2mo/s"), (3652.5, "10 years/s", "10y/s") })
        {
            rig.Tab.State.Orrery.DaysPerSecond = days;
            rig.Render();
            var leaf = rig.Tab.GetCapturedLayout()
                .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && (text.Value == longForm || text.Value == compactForm));
            var shown = rig.Tab.StripShownText(leaf);
            output.WriteLine($"{longForm}: shows \"{shown}\" in {leaf.Bounds.Width:F0} px");
            if (dpiScale == 1f)
            {
                shown.ShouldBe(longForm, "at 1x the strip has room for the long form");
            }
            else
            {
                shown.ShouldBeOneOf([longForm, compactForm], "the speed is trimmed: the button must name it whole");
            }
        }
    }

    /// <summary>
    /// The comet filter's button always names its step, whole: its long form ("Comets ≤12") where the strip has room, at 1x, and its
    /// compact form ("≤12") where it is short, never trimmed. With the strip as full as it gets (the Centre toggle, Pause, Now, a long
    /// offset), at 1x, 1.25x and 1.5x, in the platform's face and the app's (the long form at 1x asked of the app's face only); the speed
    /// keeps its own rule beside it.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, false)]
    [InlineData(1.25f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.25f, true)]
    [InlineData(1.5f, true)]
    public async Task TheCometFilterAlwaysNamesItsStep(float dpiScale, bool appFace)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale, appFace ? AppFace : null);
        Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.TimeOffset = TimeSpan.FromDays((93 * 7) + 1);
        rig.Tab.State.Orrery.Playing = true;

        foreach (var step in OrreryCometFilter.Steps)
        {
            rig.Tab.State.Orrery.CometFilter = step;
            rig.Render();
            var (longForm, compactForm) = (SkyMapTab<RgbaImage>.CometFilterLabel(step), step.ShortName);
            var leaf = rig.Tab.GetCapturedLayout()
                .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && (text.Value == longForm || text.Value == compactForm));
            var shown = rig.Tab.StripShownText(leaf);
            output.WriteLine($"{step.Name}: shows \"{shown}\" in {leaf.Bounds.Width:F0} px");
            // At 1x the app's face leaves room for the long form. The platform's need not: DejaVu Sans Mono (Linux's) is a tenth wider,
            // and with the strip this full the long form left "Distances: auto" 100 px of its 102, so the strip goes compact there.
            if (dpiScale == 1f && appFace)
            {
                shown.ShouldBe(longForm, "at 1x the strip has room for the long form");
            }
            else
            {
                shown.ShouldBeOneOf([longForm, compactForm], "the filter is trimmed: the button must name its step whole");
            }
        }
    }

    /// <summary>
    /// The strip fits the view with everything in it at once: the Centre toggle (Saturn in focus), Pause, Now, and a long offset
    /// in the time. Every button lies inside the view, Sky first at its left edge (the owner's screenshot started at "ntre: Sun"),
    /// and at 1.0x, where the row has room, each label (the distance scale, the centre, the time) is laid out at least as wide as
    /// its own content, so none is trimmed: the labels are content-capped Stars that give way only under pressure (#1584).
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    public async Task TheStripFitsWithEveryButtonShowing(float dpiScale, bool appFace)
    {
        // Measured twice: in the platform's monospace default the other orrery tests draw with, which is a different face on each
        // OS (Linux's DejaVu Sans Mono is a tenth wider than Windows's Consolas, so the CI leg kept 27 px where this box kept 53),
        // and in the face the app actually draws with, the same file everywhere.
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale, appFace ? AppFace : null);
        Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.TimeOffset = TimeSpan.FromDays((93 * 7) + 1);
        rig.Tab.State.Orrery.Playing = true;
        rig.Render();

        var buttons = rig.Tab.GetCapturedLayout()
            .Where(n => n.Node.Hit is HitResult.ButtonHit)
            .Select(n => (Action: ((HitResult.ButtonHit)n.Node.Hit!).Action, n.Bounds))
            .ToArray();
        buttons.Select(b => b.Action).ShouldContain("OrreryCentre");
        buttons.Select(b => b.Action).ShouldContain(SkyMapTab<RgbaImage>.OrrerySpeedAction);
        buttons.Select(b => b.Action).ShouldContain(SkyMapTab<RgbaImage>.OrreryCometFilterAction);
        buttons.Select(b => b.Action).ShouldContain("SkyTimeNow");
        foreach (var (action, bounds) in buttons)
        {
            output.WriteLine($"{action}: {bounds.X:F0} to {bounds.X + bounds.Width:F0}");
            bounds.X.ShouldBeGreaterThanOrEqualTo(-0.5f, $"{action} starts left of the view");
            (bounds.X + bounds.Width).ShouldBeLessThanOrEqualTo(Width + 0.5f, $"{action} ends right of the view");
        }

        // The time buttons are pushed right by the hint, so the last one ends at the strip's right padding whatever the labels do.
        var lastRight = buttons.Max(b => b.Bounds.X + b.Bounds.Width);
        lastRight.ShouldBe(Width - (8f * dpiScale), 0.5f, "the last button moved off the strip's right edge");

        // The time is weighted first: at 1.5x it still shows its whole date and time (only the offset may trim).
        var timeLeaf = rig.Tab.GetCapturedLayout()
            .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value.StartsWith("2028-", StringComparison.Ordinal));
        var timeText = (Layout.Content.Text)((Layout.Node.Leaf)timeLeaf.Node).Content;
        var dateTime = timeText.Value[..16];
        var shownTime = rig.Tab.StripShownText(timeLeaf);
        // The slack is what the time gets past its date and time; at least two ems of it, so another platform's wider glyphs
        // still leave the date and time whole (at weight 2 Windows kept 29 px at 1.5x and Linux lost the last digit).
        var slack = timeLeaf.Bounds.Width - rig.Tab.StripContentWidth(Layout.Builder.Text(dateTime, timeText.FontSize, trim: TextTrim.None));
        output.WriteLine($"the time shows \"{shownTime}\" in {timeLeaf.Bounds.Width:F0} px, {slack:F0} px past its date and time (an em is {timeText.FontSize:F0})");
        shownTime.ShouldStartWith(dateTime, Case.Sensitive, "the time is trimmed into its date and time");
        slack.ShouldBeGreaterThanOrEqualTo(2 * timeText.FontSize, "the date and time fit with less than two ems to spare");

        // At 1.0x each label reads in full; at 1.5x the row is short, so each gives way (narrower than its content) and stays in view.
        // The speed is not among them: it names its speed whole at every scale, in a compact form when short
        // (TheSpeedButtonAlwaysNamesItsSpeed).
        string[] labels = ["Distances: auto", "Centre: ", "2028-"];
        foreach (var label in labels)
        {
            var (node, bounds) = rig.Tab.GetCapturedLayout()
                .Single(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value.StartsWith(label, StringComparison.Ordinal));
            var content = rig.Tab.StripContentWidth(node);
            output.WriteLine($"{label}: {bounds.Width:F0} of its {content:F0}");
            if (dpiScale == 1f)
            {
                bounds.Width.ShouldBeGreaterThanOrEqualTo(content - 0.5f, $"\"{label}\" is trimmed though the strip has room");
                var leaf = new Layout.ArrangedNode<float>(node, bounds);
                rig.Tab.StripShownText(leaf).ShouldBe(((Layout.Content.Text)((Layout.Node.Leaf)node).Content).Value, $"\"{label}\" is drawn trimmed");
            }
            else
            {
                bounds.Width.ShouldBeLessThan(content, $"\"{label}\" did not give way though the strip is short");
                bounds.X.ShouldBeGreaterThanOrEqualTo(-0.5f, $"\"{label}\" starts left of the view");
                (bounds.X + bounds.Width).ShouldBeLessThanOrEqualTo(Width + 0.5f, $"\"{label}\" ends right of the view");
            }
        }
    }

    /// <summary>
    /// Every planet's disc is lit on its side toward the Sun as drawn: seen from high above the ecliptic each is half lit, so the
    /// brightness-weighted centre of its disc lies toward the Sun's place on screen. At the instant, 93 weeks on (the owner's
    /// screenshots: the Earth, Mars and Saturn lit wrongly), and after a run of Play-sized steps once the sprites have landed.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(0)]
    [InlineData((93 * 7) + 1)]
    public async Task EveryDiscIsLitTowardTheSun(int days)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Sol);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.State.Orrery.ElevationDeg = 80;
        for (var step = 0; step <= 20; step++)
        {
            // Play steps a day or so a frame; the last frame is the instant judged.
            rig.Tab.State.TimeOffset = TimeSpan.FromDays(days * step / 20.0);
            rig.Render();
        }
        await SettleAsync(rig);
        LitAwayFromTheSun(rig.Tab.OrreryDrawn, rig.Renderer.Surface.Pixels, Width, Height, output).ShouldBeEmpty();
    }

    /// <summary>
    /// Every planet's disc in <paramref name="pixels"/> (RGBA, <paramref name="width"/> wide) whose brightness-weighted centre lies more
    /// than 45 degrees from the Sun's place on screen, as the drawn list puts both: what <see cref="EveryDiscIsLitTowardTheSun"/> and its
    /// GPU twin (<c>OrreryGpuTests</c>, in TianWen.Lib.Tests.Gpu) judge.
    /// </summary>
    internal static System.Collections.Generic.List<string> LitAwayFromTheSun(System.Collections.Generic.IReadOnlyList<(CatalogIndex Index, float X, float Y, float Radius)> drawn,
        byte[] pixels, int width, int height, ITestOutputHelper output)
    {
        var sun = drawn.Single(d => d.Index == CatalogIndex.Sol);
        var failures = new System.Collections.Generic.List<string>();
        foreach (var body in drawn.Where(d => d.Index is CatalogIndex.Mercury or CatalogIndex.Venus or CatalogIndex.Earth
            or CatalogIndex.Mars or CatalogIndex.Jupiter or CatalogIndex.Saturn && d.Radius >= 4))
        {
            if (body.X - body.Radius < 0 || body.Y - body.Radius < 0 || body.X + body.Radius > width || body.Y + body.Radius > height)
            {
                // Not wholly in the view: its lit side cannot be weighed.
                output.WriteLine($"{body.Index}: at ({body.X:F0}, {body.Y:F0}), not wholly in the view");
                continue;
            }
            double sx = 0, sy = 0, sw = 0;
            var r = body.Radius * 0.9f;
            for (var y = (int)(body.Y - r); y <= (int)(body.Y + r); y++)
            {
                for (var x = (int)(body.X - r); x <= (int)(body.X + r); x++)
                {
                    var (dx, dy) = (x + 0.5f - body.X, y + 0.5f - body.Y);
                    if (x < 0 || y < 0 || x >= width || y >= height || (dx * dx) + (dy * dy) > r * r)
                    {
                        continue;
                    }
                    var i = ((y * width) + x) * 4;
                    var level = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                    sx += dx * level;
                    sy += dy * level;
                    sw += level;
                }
            }
            if (sw <= 0)
            {
                failures.Add($"{body.Index} drew a black disc at ({body.X:F0}, {body.Y:F0})");
                continue;
            }
            var lit = Math.Atan2(sy / sw, sx / sw) * 180 / Math.PI;
            var toSun = Math.Atan2(sun.Y - body.Y, sun.X - body.X) * 180 / Math.PI;
            var off = Math.Abs(Math.IEEERemainder(lit - toSun, 360));
            output.WriteLine($"{body.Index}: r {body.Radius:F1}, lit toward {lit:F0} deg, the Sun at {toSun:F0} deg, {off:F0} off");
            if (off > 45)
            {
                failures.Add($"{body.Index} lit {off:F0} deg away from the Sun");
            }
        }
        return failures;
    }

    /// <summary>With the Sun in focus, or nothing, there is nothing else to centre on: no toggle.</summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheSunInFocusOffersNoCentreToggle(float dpiScale)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale);
        Select(rig, CatalogIndex.Sol);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.GetCapturedLayout().Any(n => n.Node.Hit is HitResult.ButtonHit { Action: "OrreryCentre" }).ShouldBeFalse();
    }

    internal static string[] PaintedTexts(Rig rig)
        => [.. rig.Tab.GetCapturedLayout()
            .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } ? text.Value : null)
            .OfType<string>()];

    /// <summary>
    /// The distance scale is ONE toggle in the strip, labelled with the mode in force; a press where it was painted switches the
    /// scale and the label, and a second press switches them back.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheDistanceScaleIsOneToggleNamingItsMode()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.GetCapturedLayout().Count(n => n.Node.Hit is HitResult.ButtonHit { Action: var a } && a.StartsWith("OrreryScale", StringComparison.Ordinal))
            .ShouldBe(1);
        PaintedTexts(rig).ShouldContain("Distances: auto");

        foreach (var (expected, label) in new[] { (OrreryScale.True, "Distances: true"), (OrreryScale.Auto, "Distances: auto") })
        {
            var button = PaintedButtons(rig, "OrreryScale")[0];
            rig.Router.Handle(new InputEvent.MouseDown(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            rig.Router.Handle(new InputEvent.MouseUp(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
            rig.Tab.State.Orrery.Scale.ShouldBe(expected);
            rig.Render();
            PaintedTexts(rig).ShouldContain(label);
        }
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
        await rig.Tab.OrreryScene.WhenCometsRankedAsync();
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
    /// The orrery's Earth is drawn from its day map (#1573), not the flat disc it was: on the CPU surface, the camera swept round the Earth
    /// until its day side faces it, the lit pixels inside the disc carry the map's colours, the blue of the sea beside the ochre and green
    /// of the land. A flat colour lit by Minnaert's law keeps one chromaticity across the disc (measured with the Earth still flat: a
    /// spread of 0.015 in the blue share, the 8-bit rounding); the map spreads it (0.54).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOrrerysEarthIsDrawnFromItsDayMap()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale: 3f);
        // The Earth is never selected on the sky; the orrery is opened on Mars and turned to it, as a click on its disc would.
        Select(rig, CatalogIndex.Mars);
        rig.Tab.OpenOrrery();
        rig.Tab.State.Orrery.Focus = CatalogIndex.Earth;
        rig.Tab.State.Orrery.CentredOnFocus = true;
        rig.Tab.State.Orrery.ShowBackdrop = false;
        // Mars's panel, open with the view (#1656), would cover the Earth at 3x.
        rig.Tab.State.Orrery.Selected = null;
        await SettleAsync(rig);

        var best = (Lit: 0, Spread: 0.0, Azimuth: 0);
        for (var az = 0; az < 360; az += 30)
        {
            rig.Tab.State.Orrery.AzimuthDeg = az;
            await SettleAsync(rig);
            var earth = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Earth);
            var pixels = rig.Renderer.Surface.Pixels;
            var (lit, low, high) = (0, double.MaxValue, double.MinValue);
            var inner = 0.6f * earth.Radius;
            for (var y = (int)(earth.Y - inner); y <= (int)(earth.Y + inner); y++)
            {
                for (var x = (int)(earth.X - inner); x <= (int)(earth.X + inner); x++)
                {
                    if (((x - earth.X) * (x - earth.X)) + ((y - earth.Y) * (y - earth.Y)) > inner * inner || x < 0 || y < 0 || x >= Width || y >= Height)
                    {
                        continue;
                    }
                    var i = ((y * Width) + x) * 4;
                    var sum = pixels[i] + pixels[i + 1] + pixels[i + 2];
                    // Lit: the map is dark (its mean 0.135, 0.173, 0.225), so the bar is low, but high enough that a flat disc's
                    // rounding cannot spread its blue share by more than a few hundredths.
                    if (sum < 40)
                    {
                        continue;
                    }
                    lit++;
                    var blueShare = pixels[i + 2] / (double)sum;
                    (low, high) = (Math.Min(low, blueShare), Math.Max(high, blueShare));
                }
            }
            if (lit > best.Lit)
            {
                best = (lit, high - low, az);
            }
        }
        output.WriteLine($"the Earth drawn {rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Earth).Radius:F1} px in radius; its day side best seen at azimuth {best.Azimuth}: {best.Lit} lit px inside 0.6 of its radius, blue share spread {best.Spread:F3}");
        best.Lit.ShouldBeGreaterThan(50, "the camera never saw the Earth's day side");
        best.Spread.ShouldBeGreaterThan(0.1, "the Earth is one colour: it is drawn flat, not from its map");
    }

    /// <summary>
    /// The orrery's Sun is drawn from its atlas map (#1608), the generated quiet photosphere, not the flat yellow it was: on the CPU
    /// surface its centre is near white (the flat colour's blue is 0x80) and its limb darkens by the linear law, read at 0.9 of its radius.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOrrerysSunIsDrawnFromItsMap()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken, dpiScale: 3f);
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        rig.Tab.State.Orrery.ShowBackdrop = false;
        // Jupiter's panel, open with the view (#1656), would cover the Sun at 3x.
        rig.Tab.State.Orrery.Selected = null;
        await SettleAsync(rig);

        var sun = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Sol);
        var pixels = rig.Renderer.Surface.Pixels;
        (int R, int G, int B) At(float x, float y)
        {
            var i = (((int)y * Width) + (int)x) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }
        var centre = At(sun.X, sun.Y);
        var reach = 0.9f * sun.Radius;
        var limb = (At(sun.X + reach, sun.Y).G + At(sun.X - reach, sun.Y).G) / 2.0;
        var law = 1 - (TianWen.Lib.Imaging.Planetary.PlanetaryRender.SolarLimbDarkening * (1 - Math.Sqrt(1 - (0.9 * 0.9))));
        output.WriteLine($"the Sun drawn {sun.Radius:F1} px in radius: centre {centre}, green at 0.9 of the radius {limb:F0}, the law's {law * centre.G:F0}");
        centre.B.ShouldBeGreaterThan(200, "the Sun is its flat yellow, not its photosphere");
        (limb / centre.G).ShouldBe(law, 0.06, "the Sun's limb is not the law's");
    }

    /// <summary>
    /// The orrery through the CPU renderer, true and compressed scale, Jupiter selected and a set of comets the size of the one the app
    /// draws (<see cref="OrreryCometTests.DemoComets"/>): the PNGs a person looks at, written to the test output, and every planet's
    /// sprite drawn once it has landed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOrreryDrawsOnTheCpuSurface()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Planner.Comets = new StubCometRepository(OrreryCometTests.DemoComets());
        Select(rig, CatalogIndex.Jupiter);
        rig.Tab.OpenOrrery();
        var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(OrreryTests));
        Directory.CreateDirectory(folder);
        foreach (var scale in new[] { OrreryScale.Auto, OrreryScale.True })
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
                await rig.Tab.OrreryScene.WhenCometsRankedAsync();
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

        // Auto at two more zooms (the inner system, and the farthest the zoom goes) and the nearest, on the Sun.
        rig.Tab.State.Orrery.Scale = OrreryScale.Auto;
        foreach (var (name, reach) in new[] { ("auto-inner", 2.0), ("auto-out", 50.0), ("sun", 0.25) })
        {
            rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(reach);
            await SettleAsync(rig);
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
            rig.Render();
            var path = Path.Combine(folder, $"orrery-{name}.png");
            await File.WriteAllBytesAsync(path, PngWriter.Encode(rig.Renderer.Surface.Pixels, Width, Height), TestContext.Current.CancellationToken);
            output.WriteLine(path);
        }
    }
}

/// <summary>
/// Night spends no blue (CLAUDE.md, "Colour Theme"): every pixel the orrery paints under Night, the sky behind it, the orbits, the
/// discs, the comet, the labels and the strip, has a blue channel of zero. In the "UI" collection, since it flips the app-wide theme.
/// </summary>
[Collection("UI")]
public sealed class OrreryNightTests
{
    [Fact(Timeout = 120_000)]
    public async Task NightSpendsNoBlueInTheOrrery()
    {
        try
        {
            GuiTheme.Apply(UiThemeState.Night, desktopIsDark: true);
            using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
            var opened = OrreryTests.SelectComet(rig);
            // The set too, so its orbits and tails are under the test as well as the opened comet's.
            rig.Planner.Comets = new StubCometRepository([opened, .. OrreryCometTests.DemoComets()]);
            rig.Tab.OpenOrrery();
            rig.Tab.State.TimeOffset = TimeSpan.FromDays(1);
            for (var pass = 0; pass < 3; pass++)
            {
                rig.Render();
                if (rig.Tab.PlanetTextures.AtlasLoad is { } atlas)
                {
                    await atlas.WaitAsync(TestContext.Current.CancellationToken);
                }
                await rig.Tab.OrreryScene.WhenOrbitsBuiltAsync();
                await rig.Tab.OrreryScene.WhenCometsRankedAsync();
                await rig.Tab.OrrerySprites.WhenBuiltAsync();
            }
            rig.Tab.State.Orrery.Distance = OrreryCamera.DistanceToFrame(Math.Sqrt(6));
            rig.Render();
            await rig.Tab.OrrerySprites.WhenBuiltAsync();
            rig.Render();

            rig.Tab.LastOrreryBackdrop.ShouldNotBeNull("the backdrop's stars were not drawn under Night");
            var pixels = rig.Renderer.Surface.Pixels;
            var blue = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] != 0)
                {
                    blue++;
                }
            }
            blue.ShouldBe(0, "pixels with any blue under Night");
            // ...with the comets' orbits and tails among them, and every orbit the palette's tint.
            rig.Tab.OrreryTails.Count.ShouldBeGreaterThan(0, "no comet tail was drawn under Night");
            rig.Tab.OrreryOrbitsDrawn.Count(o => o.Index.ToCatalog() == Catalog.Comet).ShouldBeGreaterThan(1);
            foreach (var (index, colour, _) in rig.Tab.OrreryOrbitsDrawn)
            {
                colour.Blue.ShouldBe((byte)0, $"{index}'s orbit spends blue under Night");
            }
        }
        finally
        {
            GuiTheme.Apply(UiThemeState.Dark, desktopIsDark: true);
        }
    }
}

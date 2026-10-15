using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which face of Saturn's rings the Sun lights, and which the camera sees (docs/plans/atlas-planet-detail.md, A5, "The bodies on the
/// GPU"). The Sun crossed the ring plane in May 2025: before it, the north face was lit (2020-07-20), after it the south face
/// (2026-10-13). A <see cref="PlanetView"/>'s <c>Sun.Z</c> is the sine of the sub-solar ring latitude B' and its <c>Toward.Z</c> the
/// sine of the sub-camera one B, both planetocentric; the trace lights the face the camera sees when the two have one sign. Held to
/// JPL Horizons (<c>horizons-bodies-2026-10-14.txt</c>, Saturn's rows, planetodetic latitudes turned planetocentric here) for a
/// geocentric observer, and, for the orrery's camera above and below the ecliptic, to B computed from the camera's own ray and the
/// IAU pole (Archinal et al. 2018, WGCCRE 2015), not from the code's own body axes.
/// </summary>
[Collection("Astrometry")]
public sealed class SaturnRingFaceTests(ITestOutputHelper output)
{
    private const string HorizonsFixture = "horizons-bodies-2026-10-14.txt";

    // Saturn's polar over equatorial radius (PhysicalEphemeris.Radii: 54364 / 60268 km), turning Horizons' planetodetic latitude into
    // the planetocentric one the ring plane is measured in.
    private static double Centric(double detic)
    {
        var (equatorial, polar) = PhysicalEphemeris.Radii(CatalogIndex.Saturn);
        var q = polar / equatorial;
        return Math.Atan(q * q * Math.Tan(detic * Math.PI / 180)) * 180 / Math.PI;
    }

    private static (double ObserverB, double SunB) Horizons(string utc)
    {
        var row = PhysicalEphemerisBodiesTests.Read(HorizonsFixture).Single(r => r["body"] == "699" && r["utc"] == utc);
        return (Centric(double.Parse(row["obs_sub_lat"], System.Globalization.CultureInfo.InvariantCulture)),
            Centric(double.Parse(row["sun_sub_lat"], System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc + "Z", System.Globalization.CultureInfo.InvariantCulture);

    private static double LatitudeDeg(float sine) => Math.Asin(Math.Clamp(sine, -1f, 1f)) * 180 / Math.PI;

    /// <summary>
    /// From the Earth: on 2020-07-20 Horizons has B +21.7 and B' +21.7 degrees (the north face lit and seen), on 2026-10-13 B -7.2 and
    /// B' -7.8 (the south face lit and seen). The view the orrery's trace takes, built from VSOP87's Earth and Saturn, agrees to 0.1 degree.
    /// </summary>
    [Theory]
    [InlineData("2020-07-20 00:00", 1)]
    [InlineData("2026-10-13 00:00", -1)]
    public void FromTheEarthTheLitFaceIsHorizonsOwn(string utc, int litFace)
    {
        var instant = At(utc);
        Span<double> saturn = stackalloc double[3];
        Span<double> earth = stackalloc double[3];
        SolarSystemOrbits.TryGetPosition(CatalogIndex.Saturn, instant, saturn).ShouldBeTrue();
        SolarSystemOrbits.TryGetPosition(CatalogIndex.Earth, instant, earth).ShouldBeTrue();
        Span<double> toward = [earth[0] - saturn[0], earth[1] - saturn[1], earth[2] - saturn[2]];
        Span<double> toSun = [-saturn[0], -saturn[1], -saturn[2]];
        Span<double> right = [-toward[1], toward[0], 0];
        SolarSystemOrbits.EclipticToEquatorial(toward);
        SolarSystemOrbits.EclipticToEquatorial(toSun);
        SolarSystemOrbits.EclipticToEquatorial(right);
        var view = PlanetView.FromEquatorial(CatalogIndex.Saturn, instant, right, toward, toSun);

        var (observerB, sunB) = Horizons(utc);
        var (b, bSun) = (LatitudeDeg(view.Toward.Z), LatitudeDeg(view.Sun.Z));
        output.WriteLine($"{utc}: B {b:F2} (Horizons {observerB:F2}), B' {bSun:F2} (Horizons {sunB:F2})");
        b.ShouldBe(observerB, 0.1);
        bSun.ShouldBe(sunB, 0.1);
        Math.Sign(view.Sun.Z).ShouldBe(litFace, "the face the Sun lights");
        Math.Sign(view.Toward.Z).ShouldBe(litFace, "the face the Earth sees");
    }

    /// <summary>
    /// The orrery's camera, through the tab: at each date, from above and below the ecliptic and from four sides, B is the camera's own
    /// ray (<see cref="OrreryCamera.RayFrame"/> at Saturn's drawn place) against the IAU pole, and B' Horizons'. The rings the camera
    /// sees are lit exactly when B and B' share a sign: a camera 13 degrees above the ecliptic sees the unlit north face in 2026 from
    /// most sides, since the pole leans 28 degrees from the ecliptic's.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData("2020-07-20 00:00")]
    [InlineData("2026-10-13 00:00")]
    public async Task TheOrrerysCameraSeesTheFaceItsOwnRayMeets(string utc)
    {
        var instant = At(utc);
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken);
        rig.Planner.PlanningDate = instant;
        OrreryTests.Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        var (_, horizonsSunB) = Horizons(utc);
        var pole = IauPoleInEcliptic(instant);

        var seenLit = 0;
        var seenDark = 0;
        Span<double> right = stackalloc double[3];
        Span<double> toward = stackalloc double[3];
        foreach (var elevation in new[] { 13.0, -13.0, 45.0, -45.0 })
        {
            foreach (var azimuth in new[] { -90.0, 0.0, 90.0, 180.0 })
            {
                rig.Tab.State.Orrery.ElevationDeg = elevation;
                rig.Tab.State.Orrery.AzimuthDeg = azimuth;
                rig.Render();
                var at = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Saturn);
                var view = rig.Tab.OrreryViews.Single(v => v.Body == CatalogIndex.Saturn);

                var camera = new OrreryCamera(azimuth, elevation, rig.Tab.State.Orrery.Distance, rig.Content);
                camera.RayFrame(at.X, at.Y, right, toward);
                var expectedB = Math.Asin((toward[0] * pole.X) + (toward[1] * pole.Y) + (toward[2] * pole.Z)) * 180 / Math.PI;
                var (b, bSun) = (LatitudeDeg(view.Toward.Z), LatitudeDeg(view.Sun.Z));
                var lit = Math.Sign(view.Sun.Z) == Math.Sign(view.Toward.Z);
                output.WriteLine($"{utc} el {elevation,4} az {azimuth,4}: B {b,7:F2} (from the pole {expectedB,7:F2}), B' {bSun:F2} (Horizons {horizonsSunB:F2}): {(lit ? "lit" : "unlit")} face seen");

                b.ShouldBe(expectedB, 0.1);
                bSun.ShouldBe(horizonsSunB, 0.1);
                lit.ShouldBe(Math.Sign(expectedB) == Math.Sign(horizonsSunB));
                if (lit)
                {
                    seenLit++;
                }
                else
                {
                    seenDark++;
                }
            }
        }
        // Both faces come into view somewhere in the sweep, or the test checked only one sign.
        seenLit.ShouldBeGreaterThan(0);
        seenDark.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The controller's kind of view, for the eye: the orrery on 2026-10-15 from 13 degrees above the ecliptic at azimuth 90 (Saturn and
    /// Neptune left of the Sun, Uranus below, as the controller's screenshot has them), where the camera sees the ring face the Sun does
    /// not light (B +38, B' -7.8). Writes the whole orrery frame on the CPU surface and a 480 px close-up of the very view the orrery
    /// traced Saturn from (<see cref="PlanetaryRender.RenderRgba"/>, the reference the GPU is held to), each with the date in its name.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheControllersViewOfSaturnIsWrittenOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var instant = new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);
        using var rig = await OrreryTests.RigAsync(ct);
        rig.Planner.PlanningDate = instant;
        OrreryTests.Select(rig, CatalogIndex.Saturn);
        rig.Tab.OpenOrrery();
        rig.Tab.State.Orrery.ElevationDeg = 13;
        rig.Tab.State.Orrery.AzimuthDeg = 90;
        rig.Render();
        await OrreryTests.SettleAsync(rig);
        await rig.Tab.OrrerySprites.WhenBuiltAsync();
        rig.Render();
        var view = rig.Tab.OrreryViews.Single(v => v.Body == CatalogIndex.Saturn);
        output.WriteLine($"B {LatitudeDeg(view.Toward.Z):F2}, B' {LatitudeDeg(view.Sun.Z):F2}");

        var folder = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(SaturnRingFaceTests));
        Directory.CreateDirectory(folder);
        var stem = $"saturn-{instant:yyyy-MM-dd}-el13-az90";
        var whole = Path.Combine(folder, $"{stem}-orrery.png");
        await File.WriteAllBytesAsync(whole, PngWriter.Encode(rig.Renderer.Surface.Pixels, OrreryTests.Width, OrreryTests.Height), ct);

        const int Size = 480;
        var maps = PlanetTextureAtlas.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName)));
        var placement = new DiskPlacement((Size / 2) - 0.5, (Size / 2) - 0.5, Size / 2 / SaturnRings.Main.OuterRadii * 0.95, view.PoleScreenAngleDeg);
        var linear = PlanetaryRender.RenderRgba(maps[CatalogIndex.Saturn], view.ToAspect(instant), placement, Size, Size,
            PlanetDiscSprite.MinnaertK(CatalogIndex.Saturn), PlanetDiscSprite.Supersample, SaturnRings.Main);
        var rgba = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                // Premultiplied over black: what a host's blend stores.
                rgba[(i * 4) + c] = (byte)Math.Clamp(MathF.Round(Math.Clamp(linear[(i * 4) + c], 0, 1) * 255), 0, 255);
            }
            rgba[(i * 4) + 3] = 255;
        }
        var close = Path.Combine(folder, $"{stem}-closeup.png");
        await File.WriteAllBytesAsync(close, PngWriter.Encode(rgba, Size, Size), ct);
        output.WriteLine(whole);
        output.WriteLine(close);
        Math.Sign(view.Toward.Z).ShouldBe(-Math.Sign(view.Sun.Z), "the controller's view sees the unlit face");
    }

    // Saturn's north pole, IAU WGCCRE 2015 (alpha0 = 40.589 - 0.036 T, delta0 = 83.537 - 0.004 T, T in Julian centuries from J2000), in
    // the ecliptic of J2000 the orrery's camera stands in.
    private static Vector3 IauPoleInEcliptic(DateTimeOffset utc)
    {
        var t = SolarSystemOrbits.JulianMillennia(utc) * 10;
        var (ra, dec) = ((40.589 - (0.036 * t)) * Math.PI / 180, (83.537 - (0.004 * t)) * Math.PI / 180);
        Span<double> pole = [Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec)];
        SolarSystemOrbits.EquatorialToEcliptic(pole);
        return new Vector3((float)pole[0], (float)pole[1], (float)pole[2]);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The comets the app knows in the orrery (docs/plans/atlas-planet-detail.md, "A5: the comets", #1611): the set (the brightest at
/// peak, plus the opened, focused and pinned ones), each on its own orbit, a tail away from the Sun that grows toward perihelion,
/// labels and clicks like the planets, a cache a drag or a played minute does not rebuild, and every orbit in its body's colour.
/// </summary>
[Collection("Astrometry")]
public sealed class OrreryCometTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Instant = OrreryTests.Instant;

    private static double InstantJdTt
    {
        get
        {
            Instant.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
            return tt1 + tt2;
        }
    }

    /// <summary>The periodic comet <c>{number}P</c>, its perihelion <paramref name="daysFromPerihelion"/> before the instant.</summary>
    internal static CometElements Comet(int number, double m1, double q = 1.0, double e = 0.7, double inclination = 20, double node = 100,
        double argumentOfPerihelion = 80, double daysFromPerihelion = 0, double k1 = 10)
    {
        CometDesignation.TryParse($"{number}P", out var designation).ShouldBeTrue();
        var perihelion = InstantJdTt - daysFromPerihelion;
        return new CometElements(designation, $"Test {number}", q, e, inclination, node, argumentOfPerihelion, perihelion, perihelion, m1, k1);
    }

    /// <summary>
    /// A set the size the app draws (<see cref="OrreryScene.CometSetSize"/>) for the pictures, the frame's cost and Night: comets of
    /// every kind (short and long period, inclined and retrograde, near and past perihelion), a few active in the inner system.
    /// </summary>
    internal static CometElements[] DemoComets()
        => [.. Enumerable.Range(1, OrreryScene.CometSetSize).Select(n => Comet(100 + n, m1: n,
            q: 0.3 + (0.15 * (n % 12)), e: n % 5 == 0 ? 1.0 : 0.55 + (0.04 * (n % 10)), inclination: (23 * n) % 170, node: (47 * n) % 360,
            argumentOfPerihelion: (71 * n) % 360, daysFromPerihelion: ((n % 7) - 3) * 25))];

    private static CatalogIndex Index(in CometElements comet) => comet.CatalogIndex.ShouldNotBeNull();

    private static HashSet<CatalogIndex> SetOf(OrreryScene scene) => [.. scene.CometSet.Select(c => Index(c))];

    /// <summary>
    /// The set is the <see cref="OrreryScene.CometSetSize"/> brightest at PEAK, ranked as the sky ranks them
    /// (<c>M1 + K1 log10(max(q, 0.05))</c>: a comet of M1 -5 that never comes inside 30 AU ranks between M1 9 and 10), with no comet
    /// that has no magnitude model; the comet the view was opened on and a pinned one are added however faint. It is ranked once per
    /// repository and count, however often the scene is updated or an apparition upgrade lands; with the layer off only the opened
    /// and pinned comets are left.
    /// </summary>
    [Fact]
    public void TheSetIsTheBrightestAtPeakPlusTheOpenedAndPinned()
    {
        var comets = Enumerable.Range(1, 40).Select(n => Comet(n, m1: n)).ToList();
        comets.Add(Comet(41, m1: -5, q: 30));
        comets.Add(Comet(42, m1: double.NaN));
        var repo = new StubCometRepository([.. comets]);
        var opened = Index(comets[39]);
        var pinned = Index(comets[38]);
        IReadOnlySet<CatalogIndex> pins = new HashSet<CatalogIndex> { pinned, CatalogIndex.Mars };

        var scene = new OrreryScene();
        var choice = new OrreryCometChoice(true, opened, null, pins);
        scene.Update(Instant, repo, choice);
        var expected = comets.Take(29).Append(comets[40]).Select(c => Index(c)).Append(opened).Append(pinned).ToHashSet();
        output.WriteLine($"set: {string.Join(", ", scene.CometSet.Select(c => c.DisplayName))}");
        SetOf(scene).ShouldBe(expected, ignoreOrder: true);
        OrreryScene.CometSetSize.ShouldBe(30);

        for (var minute = 1; minute <= 5; minute++)
        {
            scene.Update(Instant.AddMinutes(minute), repo, choice);
        }
        repo.Replace(0, comets[0] with { ArgumentOfPerihelionDeg = 140 });
        scene.Update(Instant, repo, choice);
        scene.CometRankings.ShouldBe(1);
        scene.CometSet.Single(c => Index(c) == Index(comets[0])).ArgumentOfPerihelionDeg.ShouldBe(140);

        scene.Update(Instant, repo, choice with { ShowSet = false });
        SetOf(scene).ShouldBe([opened, pinned], ignoreOrder: true);

        scene.Update(Instant, new StubCometRepository([.. comets, Comet(43, m1: -20)]), choice);
        scene.CometRankings.ShouldBe(2);
        SetOf(scene).ShouldContain(Index(Comet(43, m1: -20)));
    }

    /// <summary>
    /// Each comet of the set sits on its OWN drawn orbit, a closed, an open and an inclined one; and an apparition upgrade (the same
    /// index, new elements, the repository's version moved) moves the comet onto its new orbit at once, with no change of time.
    /// </summary>
    [Fact]
    public void EachCometSitsOnItsOwnDrawnOrbit()
    {
        var repo = new StubCometRepository(
            Comet(1, 5, q: 0.9, e: 0.7, daysFromPerihelion: 20),
            Comet(2, 6, q: 1.1, e: 1.004, inclination: 130, daysFromPerihelion: -40),
            Comet(3, 7, q: 2.5, e: 0.3, inclination: 60, node: 10, argumentOfPerihelion: 200, daysFromPerihelion: 400),
            Comet(4, 8, q: 0.4, e: 0.95, inclination: 5, daysFromPerihelion: 3));
        var scene = new OrreryScene();
        var choice = new OrreryCometChoice(true, null, null, null);
        scene.Update(Instant, repo, choice);
        scene.CometSet.Length.ShouldBe(4);
        AllOnTheirOrbits();

        repo.Replace(2, repo.All[2] with { ArgumentOfPerihelionDeg = 260, EpochJdTt = repo.All[2].EpochJdTt + 1 });
        scene.Update(Instant, repo, choice);
        scene.CometOrbitBuilds.ShouldBe(5);
        AllOnTheirOrbits();

        void AllOnTheirOrbits()
        {
            foreach (var comet in scene.CometSet)
            {
                scene.TryFind(Index(comet), out var body).ShouldBeTrue();
                scene.TryGetCometOrbit(Index(comet), out var orbit).ShouldBeTrue();
                Span<double> at = [body.X, body.Y, body.Z];
                var nearest = double.MaxValue;
                for (var i = 0; i + 1 < orbit.Length / 3; i++)
                {
                    nearest = Math.Min(nearest, SegmentDistance(at, orbit.AsSpan(3 * i, 3), orbit.AsSpan(3 * (i + 1), 3)));
                }
                var r = Math.Sqrt((body.X * body.X) + (body.Y * body.Y) + (body.Z * body.Z));
                output.WriteLine($"{comet.DisplayName}: r {r:F3} AU, off its orbit by {nearest:E2} AU");
                (nearest / r).ShouldBeLessThan(1e-3);
            }
        }
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

    // The Earth's heliocentric ecliptic longitude and distance at the instant, to place a comet beside it or away from it.
    private static (double LongitudeDeg, double R) Earth()
    {
        var scene = new OrreryScene();
        scene.Update(Instant);
        scene.TryFind(CatalogIndex.Earth, out var earth).ShouldBeTrue();
        return (Math.Atan2(earth.Y, earth.X) * 180 / Math.PI, Math.Sqrt((earth.X * earth.X) + (earth.Y * earth.Y)));
    }

    private static async Task<OrreryTests.Rig> RigWithAsync(CatalogIndex opened, float dpiScale, System.Threading.CancellationToken cancellationToken, params CometElements[] comets)
    {
        var rig = await OrreryTests.RigAsync(cancellationToken, dpiScale);
        rig.Planner.Comets = new StubCometRepository(comets);
        OrreryTests.Select(rig, opened);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        return rig;
    }

    private static bool IsComet(CatalogIndex index) => index.ToCatalog() == Catalog.Comet;

    /// <summary>
    /// The tail points AWAY from the Sun on screen, along the line from the Sun's drawn place through the comet's head (a radial line
    /// projects to a line through both), and it grows toward perihelion: at perihelion (0.9 AU) it is drawn, past 3 AU, two years on,
    /// there is none. The law itself: zero from 3 AU out, longer at 2 than at 2.9, longer at 1 than at 2, and capped close in.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheTailPointsAwayFromTheSunAndGrowsTowardPerihelion(float dpiScale)
    {
        SkyMapTab<RgbaImage>.OrreryTailLength(3.0).ShouldBe(0);
        SkyMapTab<RgbaImage>.OrreryTailLength(4.0).ShouldBe(0);
        SkyMapTab<RgbaImage>.OrreryTailLength(2.0).ShouldBeGreaterThan(SkyMapTab<RgbaImage>.OrreryTailLength(2.9));
        SkyMapTab<RgbaImage>.OrreryTailLength(1.0).ShouldBeGreaterThan(SkyMapTab<RgbaImage>.OrreryTailLength(2.0));
        SkyMapTab<RgbaImage>.OrreryTailLength(0.05).ShouldBe(SkyMapTab<RgbaImage>.OrreryTailLength(0.3));

        var (longitude, _) = Earth();
        var comet = Comet(7, 6, q: 0.9, e: 0.7, inclination: 25, node: 0, argumentOfPerihelion: longitude + 120);
        using var rig = await RigWithAsync(CatalogIndex.Mars, dpiScale, TestContext.Current.CancellationToken, comet);
        var index = Index(comet);

        var tail = rig.Tab.OrreryTails.Single(t => t.Index == index);
        var sun = rig.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Sol);
        var (tx, ty) = (tail.TipX - tail.HeadX, tail.TipY - tail.HeadY);
        var (ax, ay) = (tail.HeadX - sun.X, tail.HeadY - sun.Y);
        var length = MathF.Sqrt((tx * tx) + (ty * ty));
        var off = MathF.Acos(Math.Clamp(((tx * ax) + (ty * ay)) / (length * MathF.Sqrt((ax * ax) + (ay * ay))), -1, 1)) * 180 / MathF.PI;
        output.WriteLine($"at perihelion: tail {length:F1} px, {off:F2} degrees off the anti-solar line on screen");
        length.ShouldBeGreaterThan(10 * dpiScale);
        off.ShouldBeLessThan(1f);

        rig.Tab.State.TimeOffset = TimeSpan.FromDays(730);
        rig.Render();
        rig.Tab.OrreryScene.TryFind(index, out var later).ShouldBeTrue();
        var r = Math.Sqrt((later.X * later.X) + (later.Y * later.Y) + (later.Z * later.Z));
        output.WriteLine($"two years on: r {r:F2} AU, tails drawn {rig.Tab.OrreryTails.Count}");
        r.ShouldBeGreaterThan(3);
        rig.Tab.OrreryTails.ShouldNotContain(t => t.Index == index);
        rig.Tab.OrreryDrawn.ShouldContain(d => d.Index == index);
    }

    /// <summary>
    /// A press that does not move on a comet's head puts the highlight on it, as on a planet; the strip then offers the Centre toggle
    /// for it, and centred the camera follows the comet as time runs.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AClickSelectsACometAndCentreFollowsIt()
    {
        var (longitude, _) = Earth();
        var comet = Comet(9, 6, q: 1.6, e: 0.6, inclination: 40, node: 0, argumentOfPerihelion: longitude + 90);
        using var rig = await RigWithAsync(CatalogIndex.Mars, 1f, TestContext.Current.CancellationToken, comet);
        var index = Index(comet);
        var head = rig.Tab.OrreryDrawn.Single(d => d.Index == index);

        rig.Tab.HandleInput(new InputEvent.MouseDown(head.X, head.Y)).ShouldBeTrue();
        rig.Tab.HandleInput(new InputEvent.MouseUp(head.X, head.Y)).ShouldBeTrue();
        rig.Render();
        rig.Tab.State.Orrery.Focus.ShouldBe(index);

        var button = OrreryTests.PaintedButtons(rig, "OrreryCentre")[0];
        rig.Router.Handle(new InputEvent.MouseDown(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
        rig.Router.Handle(new InputEvent.MouseUp(button.X + (button.Width / 2), button.Y + (button.Height / 2)));
        rig.Render();
        rig.Tab.State.Orrery.CentredOnFocus.ShouldBeTrue();
        var (cx, cy) = (rig.Content.X + (rig.Content.Width / 2), rig.Content.Y + (rig.Content.Height / 2));
        for (var days = 0; days <= 60; days += 30)
        {
            rig.Tab.State.TimeOffset = TimeSpan.FromDays(days);
            rig.Render();
            var drawn = rig.Tab.OrreryDrawn.Single(d => d.Index == index);
            output.WriteLine($"+{days} d: the comet at ({drawn.X:F1}, {drawn.Y:F1}), the centre ({cx}, {cy})");
            drawn.X.ShouldBe(cx, 1f);
            drawn.Y.ShouldBe(cy, 1f);
        }
    }

    /// <summary>
    /// With the set drawn, a drag builds no orbit and no position and scales no orbit again, and a played frame inside the same minute
    /// of the clock builds nothing either; a played frame years on computes the positions once and builds or scales no orbit.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADragOrAPlayedMinuteBuildsNothing()
    {
        var comets = Enumerable.Range(1, 12).Select(n => Comet(n, n, q: 0.5 + (0.2 * n), argumentOfPerihelion: 30 * n, daysFromPerihelion: 10 * n)).ToArray();
        using var rig = await RigWithAsync(CatalogIndex.Saturn, 1f, TestContext.Current.CancellationToken, comets);
        var scene = rig.Tab.OrreryScene;
        scene.CometSet.Length.ShouldBe(12);
        var (orbits, cometOrbits, positions) = (scene.OrbitBuilds, scene.CometOrbitBuilds, scene.PositionBuilds);
        var scaled = scene.ScaledOrbitBuilds;
        cometOrbits.ShouldBe(12);
        orbits.ShouldBe(1);

        rig.Tab.HandleInput(new InputEvent.MouseDown(400, 300, MouseButton.Left, InputModifier.None, 1)).ShouldBeTrue();
        for (var step = 1; step <= 20; step++)
        {
            rig.Tab.HandleInput(new InputEvent.MouseMove(400 + (step * 10), 300 + step)).ShouldBeTrue();
            rig.Render();
        }
        rig.Tab.HandleInput(new InputEvent.MouseUp(600, 320, MouseButton.Left)).ShouldBeTrue();
        (scene.OrbitBuilds, scene.CometOrbitBuilds, scene.PositionBuilds).ShouldBe((orbits, cometOrbits, positions));
        scene.ScaledOrbitBuilds.ShouldBe(scaled, "a drag scaled an orbit again");

        // The rig's clock does not move between frames, so each played frame is in the same minute.
        rig.Tab.State.Orrery.Playing = true;
        for (var frame = 0; frame < 10; frame++)
        {
            rig.Render();
        }
        (scene.OrbitBuilds, scene.CometOrbitBuilds, scene.PositionBuilds).ShouldBe((orbits, cometOrbits, positions));

        rig.Tab.State.TimeOffset = TimeSpan.FromDays(5 * 365.25);
        rig.Render();
        (scene.OrbitBuilds, scene.CometOrbitBuilds, scene.PositionBuilds).ShouldBe((orbits, cometOrbits, positions + 1));
        scene.ScaledOrbitBuilds.ShouldBe(scaled, "Play scaled an orbit again");
        output.WriteLine($"orbit builds {scene.OrbitBuilds}, comet orbit builds {scene.CometOrbitBuilds}, position builds {scene.PositionBuilds}");
    }

    /// <summary>
    /// The planets' discs keep their size with comets beside them: a comet a degree and a half along the Earth's orbit from it, and
    /// one by Mars, are never a planet's nearest neighbour in the disc cap; each comet is a dot, never a disc.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ThePlanetsDiscsAreTheSameSizeWithAndWithoutComets(float dpiScale)
    {
        var (longitude, r) = Earth();
        var scene = new OrreryScene();
        scene.Update(Instant);
        scene.TryFind(CatalogIndex.Mars, out var mars).ShouldBeTrue();
        var marsLongitude = Math.Atan2(mars.Y, mars.X) * 180 / Math.PI;
        var marsR = Math.Sqrt((mars.X * mars.X) + (mars.Y * mars.Y));
        var comets = new[]
        {
            Comet(5, 6, q: r, e: 0.5, inclination: 0, node: 0, argumentOfPerihelion: longitude + 1.5),
            Comet(6, 6, q: marsR, e: 0.5, inclination: 0, node: 0, argumentOfPerihelion: marsLongitude - 1.5),
        };

        Dictionary<CatalogIndex, float> Radii(OrreryTests.Rig rig)
            => rig.Tab.OrreryDrawn.Where(d => !IsComet(d.Index)).ToDictionary(d => d.Index, d => d.Radius);

        using var without = await RigWithAsync(CatalogIndex.Mars, dpiScale, TestContext.Current.CancellationToken);
        using var with = await RigWithAsync(CatalogIndex.Mars, dpiScale, TestContext.Current.CancellationToken, comets);
        var (a, b) = (Radii(without), Radii(with));
        foreach (var (body, radius) in a)
        {
            output.WriteLine($"{body}: {radius:F2} px without comets, {b[body]:F2} with");
            b[body].ShouldBe(radius, 0.01f, $"{body}'s disc changed size beside a comet");
        }
        foreach (var comet in with.Tab.OrreryDrawn.Where(d => IsComet(d.Index)))
        {
            comet.Radius.ShouldBe(SkyMapTab<RgbaImage>.OrreryCometRadius * dpiScale, 0.01f);
        }
        with.Tab.OrreryDrawn.Count(d => IsComet(d.Index)).ShouldBe(2);
    }

    /// <summary>
    /// Each orbit is drawn in its body's colour, from the palette: Mars's red over blue, the Earth's blue over red, a comet's violet
    /// (red and blue over green), a planet's at the dim alpha and a comet's at its own, the selected one in the accent.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OrbitsCarryTheirBodysColour()
    {
        var comet = Comet(8, 6, q: 1.2, argumentOfPerihelion: 10);
        using var rig = await RigWithAsync(CatalogIndex.Jupiter, 1f, TestContext.Current.CancellationToken, comet);
        RGBAColor32 Orbit(CatalogIndex body) => rig.Tab.OrreryOrbitsDrawn.Single(o => o.Index == body).Colour;

        var (mars, earth, violet, jupiter) = (Orbit(CatalogIndex.Mars), Orbit(CatalogIndex.Earth), Orbit(Index(comet)), Orbit(CatalogIndex.Jupiter));
        output.WriteLine($"Mars {mars}, Earth {earth}, comet {violet}, Jupiter (selected) {jupiter}");
        ((int)mars.Red).ShouldBeGreaterThan(mars.Blue + 0x30);
        ((int)earth.Blue).ShouldBeGreaterThan(earth.Red + 0x30);
        ((int)violet.Red).ShouldBeGreaterThan(violet.Green);
        ((int)violet.Blue).ShouldBeGreaterThan(violet.Green);
        mars.Alpha.ShouldBe((byte)0x70);
        violet.Alpha.ShouldBe((byte)0x90);
        jupiter.ShouldBe(GuiTheme.Palette.Accent);
        Orbit(CatalogIndex.Uranus).ShouldNotBe(Orbit(CatalogIndex.Saturn));
    }

    /// <summary>
    /// With the comet layer off the orrery draws no comet of the set (no head, no tail, no orbit); the comet the view was opened on
    /// is still drawn, as a pinned comet shows on the sky with its layer off.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCometLayerOffDrawsNone()
    {
        var comets = Enumerable.Range(1, 6).Select(n => Comet(n, n, q: 0.6 + (0.2 * n), argumentOfPerihelion: 50 * n)).ToArray();
        using var rig = await RigWithAsync(CatalogIndex.Mars, 1f, TestContext.Current.CancellationToken, comets);
        rig.Tab.OrreryDrawn.Count(d => IsComet(d.Index)).ShouldBeGreaterThan(0);

        rig.Tab.State.ShowComets = false;
        rig.Render();
        rig.Tab.OrreryDrawn.ShouldNotContain(d => IsComet(d.Index));
        rig.Tab.OrreryTails.ShouldBeEmpty();
        rig.Tab.OrreryOrbitsDrawn.ShouldNotContain(o => IsComet(o.Index));
        rig.Tab.OrreryLabels.ShouldNotContain(l => IsComet(l.Index));

        var opened = OrreryTests.SelectComet(rig);
        rig.Tab.OpenOrrery();
        rig.Render();
        rig.Tab.OrreryDrawn.Where(d => IsComet(d.Index)).Select(d => d.Index).ShouldBe([Index(opened)]);
    }

    /// <summary>
    /// Labels: the planets' first, then the comet in focus, then the others; a comet's label is placed as a planet's, under or over
    /// its head where it is clear, else left out, so no label overprints another.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CometsAreLabelledAfterThePlanetsAndNoLabelOverprints()
    {
        var comets = Enumerable.Range(1, 30).Select(n => Comet(n, n, q: 0.4 + (0.1 * n), inclination: 7 * n, argumentOfPerihelion: 37 * n, daysFromPerihelion: 13 * n)).ToArray();
        using var rig = await RigWithAsync(CatalogIndex.Jupiter, 1f, TestContext.Current.CancellationToken, comets);
        var labels = rig.Tab.OrreryLabels;
        output.WriteLine($"{labels.Count} labels, {labels.Count(l => IsComet(l.Index))} of comets, {rig.Tab.OrreryDrawn.Count(d => IsComet(d.Index))} comets drawn");
        labels.Count(l => IsComet(l.Index)).ShouldBeGreaterThan(0);
        var firstComet = labels.ToList().FindIndex(l => IsComet(l.Index));
        labels.Skip(firstComet).ShouldAllBe(l => IsComet(l.Index), "a planet's label was placed after a comet's");
        for (var i = 0; i < labels.Count; i++)
        {
            for (var j = i + 1; j < labels.Count; j++)
            {
                var (p, q) = (labels[i].Rect, labels[j].Rect);
                (p.X < q.X + q.Width && q.X < p.X + p.Width && p.Y < q.Y + q.Height && q.Y < p.Y + p.Height)
                    .ShouldBeFalse($"{labels[i].Index}'s label overprints {labels[j].Index}'s");
            }
        }
    }
}

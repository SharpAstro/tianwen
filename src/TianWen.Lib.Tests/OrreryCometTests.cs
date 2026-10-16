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

    // The scenes' clock: a fake one, so a ranking's cooldown is the test's to step past, never the machine's.
    private readonly FakeTimeProviderWrapper _clock = new(Instant);

    private OrreryScene NewScene() => new(clock: _clock);

    // The scene brought to the instant with its comet ranking landed: the clock is stepped past any ranking's cooldown, the first
    // update starts the ranking off the render thread, a frame after it lands adopts it.
    private async Task UpdateRankedAsync(OrreryScene scene, DateTimeOffset utc, ICometRepository repo, OrreryCometChoice choice)
    {
        _clock.Advance(TimeSpan.FromSeconds(10));
        scene.Update(utc, repo, choice);
        await scene.WhenCometsRankedAsync();
        scene.Update(utc, repo, choice);
    }

    // Thirty comets on circular orbits at 3 AU, about magnitude 14 to 17 at any instant: enough to fill the set without the others. At 3
    // AU a revolution is 5.2 years, so ten years on their element sets are under two revolutions old and the ranking still trusts them.
    private static List<CometElements> Fillers()
        => [.. Enumerable.Range(1, 30).Select(n => Comet(n, m1: 8 + (0.1 * n), q: 3, e: 0, inclination: 10, node: 0, argumentOfPerihelion: 12 * n))];

    /// <summary>
    /// The set is the <see cref="OrreryScene.CometSetSize"/> brightest at the VIEWING INSTANT (#1611, the pilot's amendment): a
    /// periodic comet of faint peak (M1 12) at perihelion on the date is in the set, and ten years on, near aphelion, it is out; a
    /// great comet (M1 -6, perihelion 0.2 AU) fifty years past its perihelion, far out, is out, though its PEAK estimate ranks it
    /// first of all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSetIsTheBrightestAtTheViewingInstant()
    {
        var faint = Comet(40, m1: 12, q: 1.0, e: 0.6, inclination: 5, node: 0, argumentOfPerihelion: 30);
        var great = Comet(41, m1: -6, q: 0.2, e: 0.995, daysFromPerihelion: 50 * 365.25);
        SkyMapState.PeakMagnitude(great).ShouldBeLessThan(SkyMapState.PeakMagnitude(faint));
        foreach (var at in new[] { Instant, Instant.AddYears(10) })
        {
            CometEphemeris.TryGetEquatorialJ2000WithMagnitude(faint, at, out _, out _, out var faintMag);
            CometEphemeris.TryGetEquatorialJ2000WithMagnitude(great, at, out _, out _, out var greatMag);
            CometEphemeris.TryGetEquatorialJ2000WithMagnitude(Fillers()[^1], at, out _, out _, out var fillerMag);
            output.WriteLine($"{at:yyyy-MM}: the faint comet {faintMag:F1}, the great one {greatMag:F1}, the faintest filler {fillerMag:F1}");
        }
        var repo = new StubCometRepository([.. Fillers(), faint, great]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var scene = NewScene();

        await UpdateRankedAsync(scene, Instant, repo, choice);
        output.WriteLine($"at {Instant:yyyy-MM}: {scene.CometSet.Length} comets, {string.Join(", ", scene.CometSet.Select(c => c.DisplayName))}");
        SetOf(scene).ShouldContain(Index(faint), "the faint comet at its perihelion is not in the set");
        SetOf(scene).ShouldNotContain(Index(great), "the great comet far out is in the set");
        scene.CometSet.Length.ShouldBe(OrreryScene.CometSetSize);

        var later = Instant.AddYears(10);
        await UpdateRankedAsync(scene, later, repo, choice);
        output.WriteLine($"at {later:yyyy-MM}: {string.Join(", ", scene.CometSet.Select(c => c.DisplayName))}");
        SetOf(scene).ShouldNotContain(Index(faint), "the faint comet near aphelion is still in the set");
    }

    /// <summary>
    /// The ranking is per MONTH of the view's clock: two instants in one month give the same set and one ranking; crossing into the
    /// next month ranks once, and a second instant in that month not again.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMonthIsRankedOnce()
    {
        var repo = new StubCometRepository([.. Fillers(), Comet(40, m1: 12, q: 1.0, e: 0.6), Comet(41, m1: 11, q: 0.8, e: 0.7, daysFromPerihelion: 200)]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var scene = NewScene();
        var october = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        await UpdateRankedAsync(scene, october, repo, choice);
        var first = SetOf(scene);
        scene.CometRankings.ShouldBe(1);
        await UpdateRankedAsync(scene, october.AddDays(25), repo, choice);
        scene.CometRankings.ShouldBe(1);
        SetOf(scene).ShouldBe(first, ignoreOrder: true);

        await UpdateRankedAsync(scene, october.AddDays(31), repo, choice);
        scene.CometRankings.ShouldBe(2);
        await UpdateRankedAsync(scene, october.AddDays(50), repo, choice);
        scene.CometRankings.ShouldBe(2);
        OrreryScene.MonthBucket(october).ShouldNotBe(OrreryScene.MonthBucket(october.AddDays(31)));
    }

    /// <summary>
    /// Fast Play does not rank every month it crosses (R1 Q2 of PR #1631): the next ranking starts only once
    /// <see cref="OrreryScene.CooldownAfter"/> the last one's time has passed since it landed, so the share of the time spent ranking
    /// stays near a fifth on any host and the months between are skipped. Frames a sixtieth of a second apart, each a month on: the
    /// rankings are spaced by the cooldown, never one a frame.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FastPlayRanksOnlyAfterTheCooldown()
    {
        OrreryScene.CooldownAfter(TimeSpan.FromMilliseconds(50)).ShouldBe((OrreryScene.RankCooldownFactor * TimeSpan.FromMilliseconds(50)) + OrreryScene.RankCooldownFloor);
        OrreryScene.RankCooldownFactor.ShouldBeGreaterThanOrEqualTo(4);
        OrreryScene.RankCooldownFloor.ShouldBeGreaterThan(TimeSpan.Zero);
        var repo = new StubCometRepository([.. Fillers(), Comet(40, m1: 12, q: 1.0, e: 0.6)]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var scene = NewScene();
        await UpdateRankedAsync(scene, Instant, repo, choice);
        scene.CometRankings.ShouldBe(1);

        var frame = TimeSpan.FromSeconds(1.0 / 60);
        var cooldown = OrreryScene.CooldownAfter(TimeSpan.Zero);
        var started = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        for (var month = 1; month <= 120; month++)
        {
            _clock.Advance(frame);
            elapsed += frame;
            var before = scene.CometRankings;
            scene.Update(Instant.AddMonths(month), repo, choice);
            if (scene.CometRankings > before)
            {
                started.Add(elapsed);
            }
            await scene.WhenCometsRankedAsync();
        }
        output.WriteLine($"120 frames a month apart over {elapsed.TotalSeconds:F2} s: {started.Count} rankings, at {string.Join(", ", started.Select(t => $"{t.TotalMilliseconds:F0}"))} ms (cooldown {cooldown.TotalMilliseconds:F0} ms)");
        started.Count.ShouldBeGreaterThan(0);
        started.Count.ShouldBeLessThan(120 / 2);
        for (var i = 1; i < started.Count; i++)
        {
            (started[i] - started[i - 1]).ShouldBeGreaterThanOrEqualTo(cooldown - TimeSpan.FromTicks(1));
        }
    }

    /// <summary>
    /// Slow Play (a week a second) still ranks every month: a month is about four seconds of the wall clock, far past the cooldown.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SlowPlayRanksEveryMonth()
    {
        var repo = new StubCometRepository([.. Fillers(), Comet(40, m1: 12, q: 1.0, e: 0.6)]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var scene = NewScene();
        var start = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        await UpdateRankedAsync(scene, start, repo, choice);
        var frame = TimeSpan.FromSeconds(1.0 / 60);
        var months = new HashSet<long> { OrreryScene.MonthBucket(start) };
        // Six months at a week a second, a frame a sixtieth of a second.
        for (var t = TimeSpan.Zero; t < TimeSpan.FromDays(183); t += TimeSpan.FromDays(7.0 / 60))
        {
            _clock.Advance(frame);
            scene.Update(start + t, repo, choice);
            months.Add(OrreryScene.MonthBucket(start + t));
            await scene.WhenCometsRankedAsync();
        }
        output.WriteLine($"{months.Count} months crossed, {scene.CometRankings} rankings");
        scene.CometRankings.ShouldBe(months.Count);
    }

    /// <summary>
    /// The edge of the set does not flicker: a member scores <see cref="OrreryScene.MembershipHysteresisMag"/> brighter than it is, so
    /// one just past the cutoff (0.6 mag fainter than the best outsider) stays, and one past it by more than that (1.2) goes.
    /// </summary>
    [Fact]
    public void AMemberJustPastTheCutoffStays()
    {
        var indices = Enumerable.Range(1, 31).Select(n => Index(Comet(n, m1: n))).ToArray();
        (CatalogIndex, double)[] Scored(double lastBest)
            => [.. indices.Select((index, i) => (index, i < 29 ? (double)i : i == 29 ? 30.0 : lastBest))];
        IReadOnlySet<CatalogIndex> members = new HashSet<CatalogIndex>(indices.Take(29).Append(indices[30]));

        var kept = OrreryScene.SelectBrightest(Scored(30.6), members);
        kept.Length.ShouldBe(30);
        kept.ShouldContain(indices[30], "a member 0.6 mag past the cutoff was dropped");
        kept.ShouldNotContain(indices[29]);

        var dropped = OrreryScene.SelectBrightest(Scored(31.2), members);
        dropped.ShouldNotContain(indices[30], "a member 1.2 mag past the cutoff was kept");
        dropped.ShouldContain(indices[29]);

        var fresh = OrreryScene.SelectBrightest(Scored(30.6), null);
        fresh.ShouldNotContain(indices[30], "with no members the cutoff is the magnitude alone");
    }

    // A comet of the designation given (a D/ one too), its perihelion daysFromPerihelion before the instant and its elements stated
    // epochDaysBefore before it.
    private static CometElements Designated(string designation, double m1, double q = 1.0, double e = 0.6, double daysFromPerihelion = 0,
        double epochDaysBefore = 0, double k1 = 10)
    {
        CometDesignation.TryParse(designation, out var parsed).ShouldBeTrue();
        return new CometElements(parsed, "Test", q, e, 20, 100, 80, InstantJdTt - daysFromPerihelion, InstantJdTt - epochDaysBefore, m1, k1);
    }

    /// <summary>
    /// The ranking leaves out what its model cannot be trusted for, however bright the model says it is: a D/ (defunct or lost) comet,
    /// and an element set more than <see cref="OrreryScene.MaxRevolutionsSinceEpoch"/> revolutions old at the month's middle (ten
    /// here, its perihelion modelled on the date). Opened, focused or pinned, each is drawn all the same, as they bypass the ranking.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADefunctOrLongStaleCometIsNotRanked()
    {
        var defunct = Designated("5D", m1: 4);
        var stale = Designated("77P", m1: 4, daysFromPerihelion: 10 * Math.Pow(1.0 / (1 - 0.6), 1.5) * 365.25, epochDaysBefore: 10 * Math.Pow(1.0 / (1 - 0.6), 1.5) * 365.25);
        stale.RevolutionsSinceEpoch(InstantJdTt).ShouldBeGreaterThan(OrreryScene.MaxRevolutionsSinceEpoch);
        var fresh = Designated("78P", m1: 9);
        foreach (var comet in new[] { defunct, stale, fresh })
        {
            CometEphemeris.TryGetEquatorialJ2000WithMagnitude(comet, Instant, out _, out _, out var magnitude).ShouldBeTrue();
            output.WriteLine($"{comet.DisplayName}: magnitude {magnitude:F1} at the instant, {comet.RevolutionsSinceEpoch(InstantJdTt):F1} revolutions since its epoch");
        }
        var repo = new StubCometRepository(defunct, stale, fresh);
        var scene = NewScene();

        await UpdateRankedAsync(scene, Instant, repo, new OrreryCometChoice(true, null, null, null));
        SetOf(scene).ShouldBe([Index(fresh)], ignoreOrder: true);

        IReadOnlySet<CatalogIndex> pins = new HashSet<CatalogIndex> { Index(stale) };
        await UpdateRankedAsync(scene, Instant, repo, new OrreryCometChoice(true, Index(defunct), null, pins));
        SetOf(scene).ShouldBe([Index(fresh), Index(defunct), Index(stale)], ignoreOrder: true);
    }

    /// <summary>
    /// The ranking is over every comet with a magnitude model, not the sky's candidates: a comet whose PEAK estimate is past the
    /// sky's candidacy limit (as P/2026 R2 (Leonard)'s, 18.1, is) but which is bright at the instant, passing near the Earth, is in.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACometPastTheSkysCandidacyButBrightNowIsIn()
    {
        var (longitude, r) = Earth();
        var close = Comet(70, m1: 19.5, q: r, e: 0.5, inclination: 0, node: 0, argumentOfPerihelion: longitude + 8, k1: 7.5);
        SkyMapState.IsCometCandidate(close).ShouldBeFalse("the sky's candidacy would take it");
        CometEphemeris.TryGetEquatorialJ2000WithMagnitude(close, Instant, out _, out _, out var magnitude).ShouldBeTrue();
        output.WriteLine($"peak estimate {SkyMapState.PeakMagnitude(close):F1}, magnitude at the instant {magnitude:F1}");
        magnitude.ShouldBeLessThan(17);
        var repo = new StubCometRepository([.. Fillers().Take(10), close]);
        var scene = NewScene();

        await UpdateRankedAsync(scene, Instant, repo, new OrreryCometChoice(true, null, null, null));
        SetOf(scene).ShouldContain(Index(close));
    }

    /// <summary>
    /// The comet the view was opened on, the focused one and a pinned one are always in, however faint and wherever they are, beside
    /// the thirty brightest; with the layer off only they are left. An apparition upgrade re-reads the set without ranking again.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOpenedFocusedAndPinnedAreAlwaysIn()
    {
        var bright = Enumerable.Range(1, 35).Select(n => Comet(n, m1: 5 + (0.1 * n), q: 1.2, e: 0.5, argumentOfPerihelion: 10 * n)).ToList();
        var far = Enumerable.Range(50, 3).Select(n => Comet(n, m1: 16, q: 3, e: 0.9, daysFromPerihelion: 3000)).ToList();
        var repo = new StubCometRepository([.. bright, .. far, Comet(60, m1: double.NaN)]);
        IReadOnlySet<CatalogIndex> pins = new HashSet<CatalogIndex> { Index(far[2]), CatalogIndex.Mars };
        var choice = new OrreryCometChoice(true, Index(far[0]), Index(far[1]), pins);
        var scene = NewScene();

        await UpdateRankedAsync(scene, Instant, repo, choice);
        var set = SetOf(scene);
        output.WriteLine($"set: {string.Join(", ", scene.CometSet.Select(c => c.DisplayName))}");
        set.Count.ShouldBe(OrreryScene.CometSetSize + 3);
        set.ShouldContain(Index(far[0]));
        set.ShouldContain(Index(far[1]));
        set.ShouldContain(Index(far[2]));

        repo.Replace(0, bright[0] with { ArgumentOfPerihelionDeg = 140 });
        scene.Update(Instant, repo, choice);
        scene.CometRankings.ShouldBe(1);
        scene.CometSet.Single(c => Index(c) == Index(bright[0])).ArgumentOfPerihelionDeg.ShouldBe(140);

        scene.Update(Instant, repo, choice with { ShowSet = false });
        SetOf(scene).ShouldBe([Index(far[0]), Index(far[1]), Index(far[2])], ignoreOrder: true);
    }

    /// <summary>
    /// Each comet of the set sits on its OWN drawn orbit, a closed, an open and an inclined one; and an apparition upgrade (the same
    /// index, new elements, the repository's version moved) moves the comet onto its new orbit at once, with no change of time.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task EachCometSitsOnItsOwnDrawnOrbit()
    {
        var repo = new StubCometRepository(
            Comet(1, 5, q: 0.9, e: 0.7, daysFromPerihelion: 20),
            Comet(2, 6, q: 1.1, e: 1.004, inclination: 130, daysFromPerihelion: -40),
            Comet(3, 7, q: 2.5, e: 0.3, inclination: 60, node: 10, argumentOfPerihelion: 200, daysFromPerihelion: 400),
            Comet(4, 8, q: 0.4, e: 0.95, inclination: 5, daysFromPerihelion: 3));
        var scene = NewScene();
        var choice = new OrreryCometChoice(true, null, null, null);
        await UpdateRankedAsync(scene, Instant, repo, choice);
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
    /// Each orbit is drawn in its body's colour, from the palette: Mars's red over blue, the Earth's blue over red, a planet's at the
    /// dim alpha (0x70); a comet's a dim GREY (A2 of #1611), weaker than any planet's (0x40), so thirty of them do not crowd the
    /// planets out; the selected planet's in the accent.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OrbitsCarryTheirBodysColour()
    {
        var comet = Comet(8, 6, q: 1.2, argumentOfPerihelion: 10);
        using var rig = await RigWithAsync(CatalogIndex.Jupiter, 1f, TestContext.Current.CancellationToken, comet);
        RGBAColor32 Orbit(CatalogIndex body) => rig.Tab.OrreryOrbitsDrawn.Single(o => o.Index == body).Colour;

        var (mars, earth, grey, jupiter) = (Orbit(CatalogIndex.Mars), Orbit(CatalogIndex.Earth), Orbit(Index(comet)), Orbit(CatalogIndex.Jupiter));
        output.WriteLine($"Mars {mars}, Earth {earth}, comet {grey}, Jupiter (selected) {jupiter}");
        ((int)mars.Red).ShouldBeGreaterThan(mars.Blue + 0x30);
        ((int)earth.Blue).ShouldBeGreaterThan(earth.Red + 0x30);
        var (hi, lo) = (Math.Max(grey.Red, Math.Max(grey.Green, grey.Blue)), Math.Min(grey.Red, Math.Min(grey.Green, grey.Blue)));
        (hi - lo).ShouldBeLessThanOrEqualTo(0x20, "a comet's orbit is not grey");
        mars.Alpha.ShouldBe((byte)0x70);
        grey.Alpha.ShouldBe((byte)0x40);
        grey.Alpha.ShouldBeLessThan(mars.Alpha);
        jupiter.ShouldBe(GuiTheme.Palette.Accent);
        Orbit(CatalogIndex.Uranus).ShouldNotBe(Orbit(CatalogIndex.Saturn));
    }

    /// <summary>
    /// The comet the view is about (the focused one, else the one it was opened on) has its orbit drawn LAST, over every other orbit,
    /// at full strength in the accent and a pixel thicker; the other comets' stay the dim grey.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheFocusedCometsOrbitIsDrawnLastInTheAccent(float dpiScale)
    {
        using var rig = await OrreryTests.RigAsync(TestContext.Current.CancellationToken, dpiScale);
        var opened = OrreryTests.SelectComet(rig);
        rig.Planner.Comets = new StubCometRepository([opened, .. DemoComets()]);
        rig.Tab.OpenOrrery();
        await OrreryTests.SettleAsync(rig);
        var drawn = rig.Tab.OrreryOrbitsDrawn;
        drawn.Count(o => IsComet(o.Index)).ShouldBeGreaterThan(1);
        var last = drawn[^1];
        var baseThickness = (int)MathF.Max(1, MathF.Round(dpiScale));
        output.WriteLine($"{drawn.Count} orbits, the last {last.Index} in {last.Colour} at {last.Thickness} px");
        last.Index.ShouldBe(Index(opened));
        last.Colour.ShouldBe(GuiTheme.Palette.Accent);
        last.Thickness.ShouldBe(baseThickness + 1);
        drawn.Where(o => IsComet(o.Index) && o.Index != Index(opened)).ShouldAllBe(o => o.Colour.Alpha == 0x40 && o.Thickness == baseThickness);
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

    // A comet at its perihelion at the instant, at heliocentric ecliptic longitude and latitude (degrees) and distance r: the node a
    // quarter turn back from the longitude and the perihelion a quarter turn on from the node, so the perihelion's latitude is the
    // inclination (R1 Q1 of PR #1631).
    private static CometElements CometAt(int number, double longitudeDeg, double latitudeDeg, double r)
        => latitudeDeg >= 0
            ? Comet(number, m1: 0, q: r, e: 0.5, inclination: latitudeDeg, node: longitudeDeg - 90, argumentOfPerihelion: 90)
            : Comet(number, m1: 0, q: r, e: 0.5, inclination: -latitudeDeg, node: longitudeDeg + 90, argumentOfPerihelion: 270);

    /// <summary>
    /// A comet's head never takes a PLANET's label (R1 Q1 of PR #1631): with two heads placed on Mars's two label boxes, the one under
    /// it and the one over it, and the thirty comets of the demo set besides, every planet labelled without comets is labelled with
    /// them. A comet's own label still keeps clear of the planets' discs and of other heads.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APlanetsLabelIsNotHiddenByACometsHead()
    {
        using var without = await RigWithAsync(CatalogIndex.Jupiter, 1f, TestContext.Current.CancellationToken);
        var planetsLabelled = without.Tab.OrreryLabels.Select(l => l.Index).ToHashSet();
        planetsLabelled.ShouldContain(CatalogIndex.Mars);
        var mars = without.Tab.OrreryDrawn.Single(d => d.Index == CatalogIndex.Mars);
        var label = without.Tab.OrreryLabels.Single(l => l.Index == CatalogIndex.Mars).Rect;
        // The box under the disc and the one over it, as DrawOrreryLabels places them: their centres, a box's half height from its edge.
        var halfBox = label.Height / 2;
        var underY = label.Y > mars.Y ? label.Y + halfBox : (2 * mars.Y) - (label.Y + halfBox);
        var overY = (2 * mars.Y) - underY;

        var orrery = without.Tab.State.Orrery;
        var camera = new OrreryCamera(orrery.AzimuthDeg, orrery.ElevationDeg, orrery.Distance, without.Content);
        var scaling = new OrreryScaling(orrery.Scale, OrreryScaling.ReachFor(orrery.Distance));
        without.Tab.OrreryScene.TryFind(CatalogIndex.Mars, out var body).ShouldBeTrue();
        var r = Math.Sqrt((body.X * body.X) + (body.Y * body.Y) + (body.Z * body.Z));
        var longitude = Math.Atan2(body.Y, body.X) * 180 / Math.PI;
        var latitude = Math.Asin(body.Z / r) * 180 / Math.PI;
        float ScreenY(double beta)
        {
            var (b, l) = (beta * Math.PI / 180, longitude * Math.PI / 180);
            var (x, y, z) = scaling.Apply(r * Math.Cos(b) * Math.Cos(l), r * Math.Cos(b) * Math.Sin(l), r * Math.Sin(b));
            camera.TryProject(x, y, z, out _, out var sy).ShouldBeTrue();
            return sy;
        }
        // The latitude that puts a head at screen height y: the screen's y falls as the latitude rises, so bisect.
        double LatitudeFor(float y)
        {
            var (lo, hi) = (latitude - 20, latitude + 20);
            for (var step = 0; step < 60; step++)
            {
                var mid = (lo + hi) / 2;
                (lo, hi) = ScreenY(mid) > y ? (mid, hi) : (lo, mid);
            }
            return (lo + hi) / 2;
        }
        var (under, over) = (CometAt(91, longitude, LatitudeFor(underY), r), CometAt(92, longitude, LatitudeFor(overY), r));

        using var with = await RigWithAsync(CatalogIndex.Jupiter, 1f, TestContext.Current.CancellationToken, [.. DemoComets(), under, over]);
        foreach (var comet in new[] { under, over })
        {
            var head = with.Tab.OrreryDrawn.Single(d => d.Index == Index(comet));
            output.WriteLine($"{comet.DisplayName}'s head at ({head.X:F1}, {head.Y:F1}); Mars at ({mars.X:F1}, {mars.Y:F1}), its label box {label.X:F0},{label.Y:F0} {label.Width:F0} x {label.Height:F0}");
            MathF.Abs(head.X - mars.X).ShouldBeLessThan(2f);
        }
        var labelledWith = with.Tab.OrreryLabels.Select(l => l.Index).ToHashSet();
        planetsLabelled.Where(p => !labelledWith.Contains(p)).ShouldBeEmpty("planets labelled without comets that lost their label to a comet");
    }
}

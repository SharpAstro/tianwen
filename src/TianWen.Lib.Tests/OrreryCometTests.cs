using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging;
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
    /// A view held still inside a ranking's cooldown (#1631 R2) draws no frame of its own, so the month it moved to would never be
    /// ranked: the scene asks for ONE frame when the cooldown ends, on its own clock, and the frame that follows ranks that month.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AViewHeldInTheCooldownIsWokenWhenItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.ExternalTimePump = true;
        using var frames = new SemaphoreSlim(0);
        var scene = new OrreryScene(() => frames.Release(), _clock);
        var repo = new StubCometRepository([.. Fillers(), Comet(40, m1: 12, q: 1.0, e: 0.6)]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var october = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        // October ranked: its landing asks for the frame that adopts it (the cooldown runs from that frame), and the planets' orbits'
        // first build asks for one of its own; both are taken here, so any frame after is the wake's.
        scene.Update(october, repo, choice);
        await scene.WhenCometsRankedAsync();
        await scene.WhenOrbitsBuiltAsync();
        await frames.WaitAsync(ct);
        await frames.WaitAsync(ct);
        scene.Update(october, repo, choice);
        scene.CometRankings.ShouldBe(1);

        // The view moves to November and is held there, twice, inside the cooldown: nothing is ranked and no frame is asked for yet.
        var november = october.AddMonths(1);
        scene.Update(november, repo, choice);
        scene.Update(november, repo, choice);
        scene.CometRankings.ShouldBe(1);
        frames.CurrentCount.ShouldBe(0);

        // The cooldown ends on the scene's clock: one frame is asked for, and that frame ranks November.
        _clock.Advance(OrreryScene.CooldownAfter(TimeSpan.Zero));
        await frames.WaitAsync(ct);
        scene.Update(november, repo, choice);
        scene.CometRankings.ShouldBe(2, "the frame the wake asked for did not rank the month the view was held in");
        await scene.WhenCometsRankedAsync();
        await frames.WaitAsync(ct);
        scene.Update(november, repo, choice);
        scene.Brightest.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A ranking that throws (#1631 R2) is logged with its exception, which observes it, and is not started again for the same month,
    /// repository and catalogue: with a frame a second, well past every cooldown, it is tried ONCE a month, never every 100 ms; the
    /// next month is tried afresh.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARankingThatThrowsIsLoggedAndNotRetriedEveryFrame()
    {
        var log = new RecordingLogger();
        var attempts = 0;
        var scene = new OrreryScene(clock: _clock, logger: log)
        {
            Rank = (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("the ranking failed");
            },
        };
        var repo = new StubCometRepository([.. Fillers()]);
        var choice = new OrreryCometChoice(true, null, null, null);
        var october = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        for (var frame = 0; frame < 10; frame++)
        {
            await UpdateRankedAsync(scene, october.AddHours(frame), repo, choice);
        }
        output.WriteLine($"ten frames in October: {attempts} attempts, {log.Entries.Count} entries logged");
        attempts.ShouldBe(1, "a ranking that threw was started again in the same month");
        var entry = log.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldBeOfType<InvalidOperationException>();
        scene.Brightest.ShouldBeEmpty();

        for (var frame = 0; frame < 10; frame++)
        {
            await UpdateRankedAsync(scene, october.AddMonths(1).AddHours(frame), repo, choice);
        }
        output.WriteLine($"and ten in November: {attempts} attempts, {log.Entries.Count} entries logged");
        attempts.ShouldBe(2, "the next month was not tried afresh, or was tried more than once");
        log.Entries.Count.ShouldBe(2);
    }

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new ConcurrentQueue<(LogLevel Level, Exception? Exception)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }

    // Comets at perihelion at the instant, M1 from 1 to 11 in steps of 2, beside the thirty fillers (magnitude 14 to 17): the month's
    // best magnitudes run from about 1 to 17, so each step of the filter keeps a different set.
    private static List<CometElements> SpreadOfMagnitudes()
        => [.. Fillers(), .. Enumerable.Range(0, 6).Select(n => Comet(60 + n, m1: 1 + (2 * n), q: 1.1, e: 0.6, inclination: 10 + (20 * n), node: 30 * n))];

    /// <summary>
    /// The comet filter (amendment A2 of #1611): each step keeps the ranked comets whose MONTH-BEST magnitude is at or brighter than its
    /// limit, Naked eye 6, Binoculars 9, Small telescope 12 (the default), All the whole ranked set, its ceiling
    /// <see cref="OrreryScene.CometSetSize"/>; each step's set holds the one before it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task EachFilterStepKeepsTheCometsBrightEnoughForIt()
    {
        OrreryCometFilter.Steps.Select(s => s.MaxMagnitude).ShouldBe([6.0, 9.0, 12.0, null]);
        OrreryCometFilter.Default.ShouldBe(OrreryCometFilter.Steps[2]);
        new OrreryState().CometFilter.ShouldBe(OrreryCometFilter.Default);
        var repo = new StubCometRepository([.. SpreadOfMagnitudes()]);
        var scene = NewScene();
        await UpdateRankedAsync(scene, Instant, repo, new OrreryCometChoice(true, null, null, null));
        var ranked = scene.Brightest;
        ranked.Length.ShouldBe(OrreryScene.CometSetSize);
        output.WriteLine($"ranked, month-best: {string.Join(", ", ranked.Select(r => $"{r.Best:F1}"))}");

        var previous = new HashSet<CatalogIndex>();
        foreach (var step in OrreryCometFilter.Steps)
        {
            scene.Update(Instant, repo, new OrreryCometChoice(true, null, null, null, step.MaxMagnitude));
            var set = SetOf(scene);
            var expected = ranked.Where(r => step.MaxMagnitude is not { } max || r.Best <= max).Select(r => r.Index).ToHashSet();
            output.WriteLine($"{step.Name}: {set.Count} comets");
            set.ShouldBe(expected, ignoreOrder: true);
            set.IsSupersetOf(previous).ShouldBeTrue($"{step.Name} dropped a comet the step before kept");
            set.Count.ShouldBeGreaterThan(previous.Count, $"{step.Name} keeps no more than the step before: the fixture does not separate the steps");
            previous = set;
        }
        previous.Count.ShouldBe(OrreryScene.CometSetSize);
        scene.CometRankings.ShouldBe(1, "a change of filter ranked again");
    }

    /// <summary>
    /// The comet the view was opened on, the focused one and the pinned ones bypass the filter, as on the sky: all faint, all drawn at
    /// Naked eye.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheOpenedFocusedAndPinnedBypassTheFilter()
    {
        var faint = Enumerable.Range(80, 3).Select(n => Comet(n, m1: 16, q: 3, e: 0.9, daysFromPerihelion: 3000)).ToList();
        var repo = new StubCometRepository([.. SpreadOfMagnitudes(), .. faint]);
        IReadOnlySet<CatalogIndex> pins = new HashSet<CatalogIndex> { Index(faint[2]) };
        var choice = new OrreryCometChoice(true, Index(faint[0]), Index(faint[1]), pins, OrreryCometFilter.Steps[0].MaxMagnitude);
        var scene = NewScene();
        await UpdateRankedAsync(scene, Instant, repo, choice);
        var set = SetOf(scene);
        output.WriteLine($"Naked eye with three faint ones opened, focused and pinned: {string.Join(", ", scene.CometSet.Select(c => c.DisplayName))}");
        set.ShouldContain(Index(faint[0]));
        set.ShouldContain(Index(faint[1]));
        set.ShouldContain(Index(faint[2]));
        set.Count.ShouldBe(3 + scene.Brightest.Count(r => r.Best <= 6));
    }

    /// <summary>
    /// The filter reads the MONTH's best, so Play inside a month never flickers a comet in and out at the limit: frames every six hours
    /// across a month at Binoculars draw one set, though a comet's magnitude at the instant moves through the limit meanwhile.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APlayedMonthDoesNotFlickerAtTheLimit()
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        // One that fades through the limit inside the month: at a 0.3 AU perihelion on its first day, magnitude 6.7, and by the
        // month's end 12.3.
        var fading = Comet(70, m1: 12, q: 0.3, e: 0.9, inclination: 5, node: 0, daysFromPerihelion: (Instant - start).TotalDays);
        var fadingMags = Enumerable.Range(0, 31).Select(d => CometEphemeris.TryGetEquatorialJ2000WithMagnitude(fading, start.AddDays(d), out _, out _, out var m) ? m : double.NaN).ToList();
        output.WriteLine($"the fading comet over the month: {fadingMags.Min():F1} to {fadingMags.Max():F1}");
        var repo = new StubCometRepository([.. SpreadOfMagnitudes(), fading]);
        var choice = new OrreryCometChoice(true, null, null, null, OrreryCometFilter.Steps[1].MaxMagnitude);
        var scene = NewScene();
        await UpdateRankedAsync(scene, start, repo, choice);
        var first = SetOf(scene);
        first.Count.ShouldBeGreaterThan(0);
        var crossed = new HashSet<CatalogIndex>();
        for (var hours = 6; hours < 31 * 24; hours += 6)
        {
            var at = start.AddHours(hours);
            _clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
            scene.Update(at, repo, choice);
            SetOf(scene).ShouldBe(first, ignoreOrder: true, $"the set changed at +{hours} h");
            foreach (var comet in repo.All)
            {
                CometEphemeris.TryGetEquatorialJ2000WithMagnitude(comet, at, out _, out _, out var magnitude);
                if (first.Contains(Index(comet)) && magnitude > 9)
                {
                    crossed.Add(Index(comet));
                }
            }
        }
        output.WriteLine($"{first.Count} comets drawn all month, {crossed.Count} of them fainter than 9 at some instant of it");
        first.Count.ShouldBeLessThan(scene.Brightest.Length, "Binoculars kept every comet ranked: the filter is not applied");
        crossed.Count.ShouldBeGreaterThan(0, "no comet drawn crossed the limit: the test proves nothing");
        scene.CometRankings.ShouldBe(1);
    }

    /// <summary>
    /// The strip's comet filter: a press opens the four steps as a drop-up above it, a pick sets the step (remembered on the view's
    /// state for the session), and the view then draws only the comets that step keeps: their orbits, heads, tails, labels and the
    /// heads a click can take.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheStripsFilterPicksAStepAndTheViewDrawsOnlyIt(float dpiScale)
    {
        using var rig = await RigWithAsync(CatalogIndex.Mars, dpiScale, TestContext.Current.CancellationToken, [.. SpreadOfMagnitudes()]);
        var orrery = rig.Tab.State.Orrery;
        orrery.CometFilter.ShouldBe(OrreryCometFilter.Default);
        var ranked = rig.Tab.OrreryScene.Brightest;
        ranked.Length.ShouldBe(OrreryScene.CometSetSize);
        for (var i = 0; i < OrreryCometFilter.Steps.Length; i++)
        {
            var step = OrreryCometFilter.Steps[i];
            var button = OrreryTests.PaintedButtons(rig, SkyMapTab<RgbaImage>.OrreryCometFilterAction)[0];
            OrreryTests.PressAt(rig, button);
            rig.Tab.CometFilterMenu.IsOpen.ShouldBeTrue();
            rig.Tab.CometFilterMenu.Items.Length.ShouldBe(OrreryCometFilter.Steps.Length);
            rig.Render();
            var row = OrreryTests.DropUpRow(rig, i);
            (row.Y + row.Height).ShouldBeLessThanOrEqualTo(button.Y + 0.5f, "a drop-up opens above the strip");
            OrreryTests.PressAt(rig, row);
            rig.Tab.CometFilterMenu.IsOpen.ShouldBeFalse();
            orrery.CometFilter.ShouldBe(step);
            rig.Render();

            var kept = ranked.Where(r => step.MaxMagnitude is not { } max || r.Best <= max).Select(r => r.Index).ToHashSet();
            output.WriteLine($"{step.Name}: {kept.Count} kept, {rig.Tab.OrreryDrawn.Count(d => IsComet(d.Index))} heads drawn, "
                + $"{rig.Tab.OrreryTails.Count} tails, {rig.Tab.OrreryLabels.Count(l => IsComet(l.Index))} comet labels");
            SetOf(rig.Tab.OrreryScene).ShouldBe(kept, ignoreOrder: true);
            rig.Tab.OrreryDrawn.Where(d => IsComet(d.Index)).ShouldAllBe(d => kept.Contains(d.Index));
            rig.Tab.OrreryOrbitsDrawn.Where(o => IsComet(o.Index)).ShouldAllBe(o => kept.Contains(o.Index));
            rig.Tab.OrreryTails.ShouldAllBe(t => kept.Contains(t.Index));
            rig.Tab.OrreryLabels.Where(l => IsComet(l.Index)).ShouldAllBe(l => kept.Contains(l.Index));
            rig.Tab.OrreryOrbitsDrawn.Count(o => IsComet(o.Index)).ShouldBe(kept.Count);
            OrreryTests.PaintedTexts(rig).ShouldContain(t => t == SkyMapTab<RgbaImage>.CometFilterLabel(step) || t == step.ShortName);
        }
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

        var kept = OrreryScene.SelectBrightest(Scored(30.6), members).Select(k => k.Index).ToArray();
        kept.Length.ShouldBe(30);
        kept.ShouldContain(indices[30], "a member 0.6 mag past the cutoff was dropped");
        kept.ShouldNotContain(indices[29]);

        var dropped = OrreryScene.SelectBrightest(Scored(31.2), members).Select(k => k.Index).ToArray();
        dropped.ShouldNotContain(indices[30], "a member 1.2 mag past the cutoff was kept");
        dropped.ShouldContain(indices[29]);

        var fresh = OrreryScene.SelectBrightest(Scored(30.6), null).Select(k => k.Index).ToArray();
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
    /// there is none. The law itself: 48 px times the square root of the activity's share (A2 of #1611), held at 14 px out to 2.5 AU
    /// and tapering linearly to nothing at 3 AU, so a tail never pops in or out; capped close in.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheTailPointsAwayFromTheSunAndGrowsTowardPerihelion(float dpiScale)
    {
        static float Length(double r) => SkyMapTab<RgbaImage>.OrreryTailLength(r);
        Length(3.0).ShouldBe(0);
        Length(4.0).ShouldBe(0);
        Length(2.0).ShouldBeGreaterThan(Length(2.9));
        Length(1.0).ShouldBeGreaterThan(Length(2.0));
        Length(0.05).ShouldBe(Length(0.3));
        Length(0.3).ShouldBe(48f, 0.01f);
        // At 1 AU: 48 times the square root of (1 - 1/9) / (1/0.49 - 1/9).
        Length(1.0).ShouldBe((float)(48 * Math.Sqrt((1 - (1.0 / 9)) / ((1 / 0.49) - (1.0 / 9)))), 0.01f);
        Length(2.0).ShouldBe(14f, 0.01f);
        Length(2.49).ShouldBe(14f, 0.01f);
        Length(2.75).ShouldBe(7f, 0.01f);
        // No pop at either end of the taper.
        Math.Abs(Length(2.5005) - Length(2.4995)).ShouldBeLessThan(0.05f);
        Length(2.999).ShouldBeLessThan(0.5f);
        Length(2.99999).ShouldBeLessThan(0.05f);
        output.WriteLine($"tail lengths, design px: 0.7 AU {Length(0.7):F1}, 1 AU {Length(1.0):F1}, 1.5 AU {Length(1.5):F1}, 2 AU {Length(2.0):F1}, 2.75 AU {Length(2.75):F1}");

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
    /// A tail seen nearly end on still shows (A2 of #1611): its foreshortening is floored at 0.4, so a comet a few degrees off the
    /// camera's line through the Sun, its tail pointing almost at the camera, draws at least 0.4 of its length.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ATailSeenEndOnIsFlooredNotLost()
    {
        // Five degrees of longitude off the camera's own direction from the Sun (a new view's), at its elevation: the radial line
        // runs near the ray.
        var view = new OrreryState();
        var comet = CometAt(77, view.AzimuthDeg + 5, view.ElevationDeg, 1.0);
        using var rig = await RigWithAsync(CatalogIndex.Sol, 1f, TestContext.Current.CancellationToken, comet);
        var index = Index(comet);
        var tail = rig.Tab.OrreryTails.SingleOrDefault(t => t.Index == index);
        tail.Index.ShouldBe(index, "the end-on tail was not drawn at all");
        var length = MathF.Sqrt(((tail.TipX - tail.HeadX) * (tail.TipX - tail.HeadX)) + ((tail.TipY - tail.HeadY) * (tail.TipY - tail.HeadY)));
        var full = SkyMapTab<RgbaImage>.OrreryTailLength(1.0);
        output.WriteLine($"end on: {length:F1} px of {full:F1}");
        length.ShouldBeGreaterThanOrEqualTo((0.4f * full) - 0.05f);
        length.ShouldBeLessThan(0.5f * full, "the comet is not seen near end on: the test places it wrongly");
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
        // Every one of the twelve drawn, whatever its magnitude: the filter is not under test here.
        rig.Tab.State.Orrery.CometFilter = OrreryCometFilter.All;
        await OrreryTests.SettleAsync(rig);
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

    /// <summary>
    /// Every label lies inside the view (A2 of #1611: the real catalogue's captures cut names at the edges), a planet's as a comet's,
    /// the Moon's, the focus body's and the Earth's forced ones too: across framings that put bodies at every edge (zoomed in on the
    /// Earth and the Moon, centred on Venus, Jupiter or Saturn, looking down from high and from low), with the thirty comets of the demo set.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Moon, 0.35, 30.0, 0.0, false)]
    [InlineData(CatalogIndex.Moon, 0.2, 30.0, 40.0, false)]
    [InlineData(CatalogIndex.Venus, 0.5, 70.0, 120.0, true)]
    [InlineData(CatalogIndex.Jupiter, 0.6, 10.0, 200.0, true)]
    [InlineData(CatalogIndex.Mars, 1.0, -40.0, 300.0, false)]
    [InlineData(CatalogIndex.Saturn, 0.3, 50.0, 80.0, true)]
    public async Task EveryLabelLiesInsideTheView(CatalogIndex focus, double zoom, double elevationDeg, double azimuthDeg, bool centred)
    {
        foreach (var dpiScale in new[] { 1f, 1.5f })
        {
            using var rig = await RigWithAsync(focus, dpiScale, TestContext.Current.CancellationToken, DemoComets());
            var orrery = rig.Tab.State.Orrery;
            orrery.Distance *= zoom;
            orrery.ElevationDeg = elevationDeg;
            orrery.AzimuthDeg = azimuthDeg;
            orrery.CentredOnFocus = centred;
            rig.Render();
            var content = rig.Content;
            var outside = rig.Tab.OrreryLabels.Where(l => l.Rect.X < content.X - 0.01f || l.Rect.Y < content.Y - 0.01f
                || l.Rect.X + l.Rect.Width > content.X + content.Width + 0.01f || l.Rect.Y + l.Rect.Height > content.Y + content.Height + 0.01f).ToList();
            output.WriteLine($"{dpiScale}x: {rig.Tab.OrreryLabels.Count} labels, {outside.Count} outside the view: "
                + string.Join(", ", outside.Select(l => $"{l.Index} at {l.Rect.X:F0},{l.Rect.Y:F0} {l.Rect.Width:F0}x{l.Rect.Height:F0}")));
            outside.ShouldBeEmpty();
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

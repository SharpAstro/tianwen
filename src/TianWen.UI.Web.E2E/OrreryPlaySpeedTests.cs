using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The orrery in the browser (docs/plans/atlas-planet-detail.md, A5, #1573): Alt+S opens it from the atlas, the strip's speed button
/// opens the drop-up of the fifteen speeds, a press on "1 second = 2 years" sets the rate, and Play then runs the atlas's clock at it.
/// The strip and the drop-up are canvas paintings, so every press goes where the app says it PAINTED them (the <c>?e2e=1</c> hooks
/// <c>getButtonRect</c> and <c>getDropdownRowRect</c>), and every wait is on something the app reports, never a sleep.
/// </summary>
/// <remarks>
/// The canvas is captured to <c>%TEMP%/tianwen-orrery-shots/orrery-two-years.png</c> (or <c>TIANWEN_WEB_SHOTS</c>) once Play has run,
/// so the frame can be looked at.
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class OrreryPlaySpeedTests(TianWenWebFixture fixture, ITestOutputHelper output)
{
    private const float BootTimeout = 180_000;
    private const float FrameTimeout = 60_000;

    // Two years a second, the thirteenth of the fifteen (index 12); 730.5 days, as OrreryState.PlaySpeeds has it.
    private const int TwoYearsRow = 12;
    private const double TwoYearsDays = 2 * 365.25;

    // A pinned instant, so the frame does not depend on the day the test runs.
    private const string AtlasLink = "?e2e=1&view=sky&t=2026-10-13T22:00:00Z";

    private sealed record Rect(double x, double y, double w, double h);

    /// <summary>
    /// A <c>page.WaitForFunctionAsync</c> predicate that calls an asynchronous <c>window.__tianwenTest</c> hook one request at a time
    /// and resolves with its last answer once that answer is not "null" (the synchronous shape <see cref="RenderStatsWait"/> explains).
    /// </summary>
    private static string HookAnswered(string call, string slot)
        => "() => { const t = window.__tianwenTest; if (!t) return false; "
            + $"if (!window.__{slot}InFlight) {{ window.__{slot}InFlight = true; "
            + $"t.{call}.then(j => {{ window.__{slot} = j; }}).finally(() => {{ window.__{slot}InFlight = false; }}); }} "
            + $"const r = window.__{slot}; return r && r !== 'null' ? r : false; }}";

    private static async Task<Rect> PaintedAsync(IPage page, string call, string slot)
    {
        var handle = await page.WaitForFunctionAsync(HookAnswered(call, slot), null, new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonSerializer.Deserialize<Rect>(await handle.JsonValueAsync<string>())
            ?? throw new InvalidOperationException($"{call} answered no rect");
    }

    private static async Task PressAsync(ILocator canvas, Rect rect)
        => await canvas.ClickAsync(new LocatorClickOptions { Position = new Position { X = (float)(rect.x + (rect.w / 2)), Y = (float)(rect.y + (rect.h / 2)) } });

    private static async Task<JsonElement> StatsWhenAsync(IPage page, string condition)
    {
        var handle = await page.WaitForFunctionAsync(RenderStatsWait.Script(condition, "JSON.stringify(s)"), null,
            new PageWaitForFunctionOptions { Timeout = FrameTimeout });
        return JsonDocument.Parse(await handle.JsonValueAsync<string>()).RootElement;
    }

    [Fact]
    public async Task APickedSpeedIsTheRatePlayRunsAt()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync(fixture.BaseUrl + AtlasLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        var canvas = page.Locator("#planner");

        await canvas.PressAsync("Alt+s");
        var stats = await StatsWhenAsync(page, "s.orreryOpen && s.orreryFrames > 0");
        output.WriteLine($"the orrery opened at {stats.GetProperty("orreryDaysPerSecond").GetDouble()} days a second");

        await PressAsync(canvas, await PaintedAsync(page, "getButtonRect('OrrerySpeed')", "speedButton"));
        var row = await PaintedAsync(page, $"getDropdownRowRect({TwoYearsRow.ToString(CultureInfo.InvariantCulture)})", "speedRow");
        output.WriteLine($"the drop-up's \"1 second = 2 years\" row painted at ({row.x:F0}, {row.y:F0}) {row.w:F0} x {row.h:F0}");
        await PressAsync(canvas, row);
        stats = await StatsWhenAsync(page, $"s.orreryDaysPerSecond === {TwoYearsDays.ToString(CultureInfo.InvariantCulture)}");

        var playButton = await PaintedAsync(page, "getButtonRect('OrreryPlay')", "playButton");
        var before = stats.GetProperty("timeOffsetDays").GetDouble();
        await PressAsync(canvas, playButton);
        // At least a played second: two years on the atlas's clock, however slowly the interpreted build draws.
        var after = await StatsWhenAsync(page,
            $"s.orreryPlaying && s.timeOffsetDays >= {(before + TwoYearsDays).ToString(CultureInfo.InvariantCulture)}");
        var frames = after.GetProperty("orreryFrames").GetInt32();
        output.WriteLine($"Play ran the clock {after.GetProperty("timeOffsetDays").GetDouble() - before:F1} days by orrery frame {frames}");

        // Pause on the frame captured: the clock then holds while the page is shot.
        await PressAsync(canvas, playButton);
        await StatsWhenAsync(page, $"!s.orreryPlaying && s.orreryFrames > {frames.ToString(CultureInfo.InvariantCulture)}");
        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS") ?? Path.Combine(Path.GetTempPath(), "tianwen-orrery-shots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "orrery-two-years.png");
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"the canvas: {path}");
        await page.Context.CloseAsync();
    }

    // Eight periodic comets near perihelion at the pinned instant, in the bulk SBDB query's own shape (the fields and a row of strings
    // each), which the page fetches as comets-sbdb.json: a dev server has no baked file (pages.yml bakes it), so the test serves it.
    private static string CometQueryResponse()
    {
        var instantJd = (new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).TotalDays + 2440587.5;
        var rows = Enumerable.Range(0, 8).Select(n => new[]
        {
            null, $"{201 + n}P", $"E2E {n}", (5 + n).ToString(CultureInfo.InvariantCulture), "10", "0.6",
            (0.6 + (0.1 * n)).ToString(CultureInfo.InvariantCulture), (10 + (17 * n)).ToString(CultureInfo.InvariantCulture),
            (40 * n).ToString(CultureInfo.InvariantCulture), (73 * n % 360).ToString(CultureInfo.InvariantCulture),
            (instantJd - (8 * (n - 3))).ToString("R", CultureInfo.InvariantCulture), (instantJd - (8 * (n - 3))).ToString("R", CultureInfo.InvariantCulture),
        });
        return JsonSerializer.Serialize(new
        {
            fields = new[] { "prefix", "pdes", "name", "M1", "K1", "e", "q", "i", "om", "w", "tp", "epoch" },
            data = rows,
            count = 8,
        });
    }

    // The strip's comet filter (A2 of #1611) and the row of its drop-up that keeps every comet ranked.
    private const string CometFilterAction = "OrreryCometFilter";
    private const int AllCometsRow = 3;

    /// <summary>
    /// The comets the app knows are drawn in the browser's orrery (#1611): served the bulk comet set, Alt+S opens the orrery with the
    /// comets the default filter (Small telescope, magnitude 12) keeps; All, picked from the strip's filter drop-up, draws every comet
    /// of it, each with its orbit, tails among them. The canvas is written for a person to look at (the orbits are a dim grey, A2 of
    /// #1611, which no pixel count tells from the backdrop's dim stars, so the frame's own count of the orbits it drew is read).
    /// </summary>
    [Fact]
    public async Task TheCometsAreDrawnWithTheirOrbitsAndTails()
    {
        var page = await fixture.NewPageAsync();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Console += (_, m) =>
        {
            if (m.Text.Contains("[tianwen-web] comets loaded", StringComparison.Ordinal))
            {
                loaded.TrySetResult();
            }
        };
        var body = CometQueryResponse();
        await page.RouteAsync("**/comets-sbdb.json", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json", Body = body,
        }));
        await page.GotoAsync(fixture.BaseUrl + AtlasLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        await loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(BootTimeout), TestContext.Current.CancellationToken);
        var canvas = page.Locator("#planner");

        await canvas.PressAsync("Alt+s");
        // Small telescope by default (magnitude 12, A2 of #1611): some of the eight, never more.
        var stats = await StatsWhenAsync(page, "s.orreryOpen && s.orreryComets > 0 && s.orreryCometFilter === 12");
        var atDefault = stats.GetProperty("orreryComets").GetInt32();
        output.WriteLine($"Small telescope: {atDefault} comets");
        Assert.InRange(atDefault, 1, 8);

        // All, picked from the strip's filter drop-up as a person would: every comet of the eight, each with its orbit.
        await PressAsync(canvas, await PaintedAsync(page, $"getButtonRect('{CometFilterAction}')", "filterButton"));
        await PressAsync(canvas, await PaintedAsync(page, $"getDropdownRowRect({AllCometsRow.ToString(CultureInfo.InvariantCulture)})", "filterRow"));
        stats = await StatsWhenAsync(page, "s.orreryCometFilter === -1 && s.orreryComets >= 8 && s.orreryCometOrbits >= 8 && s.orreryTails > 0");
        output.WriteLine($"All: the orrery drew {stats.GetProperty("orreryComets").GetInt32()} comets, {stats.GetProperty("orreryCometOrbits").GetInt32()} orbits, "
            + $"{stats.GetProperty("orreryTails").GetInt32()} tails, in {stats.GetProperty("orreryMs").GetDouble():F1} ms");

        var dir = Environment.GetEnvironmentVariable("TIANWEN_WEB_SHOTS") ?? Path.Combine(Path.GetTempPath(), "tianwen-orrery-shots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "orrery-comets.png");
        var box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("the atlas canvas has no box");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path,
            Clip = new Clip { X = box.X, Y = box.Y, Width = box.Width, Height = box.Height },
        });
        output.WriteLine($"the canvas: {path}");
        await page.Context.CloseAsync();
    }

    // The ten years a second of the fifteen speeds (index 14).
    private const int TenYearsRow = 14;

    // A catalogue the size of SBDB's (4,080 comets on 2026-10-16, 1,995 with a magnitude model), generated from a fixed seed: every
    // comet with a model is ranked each month, so this is the ranking's real workload. Element sets a year or two old, so the
    // staleness filter scores nearly all of them.
    private static string RealisticCometQueryResponse()
    {
        var instantJd = (new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).TotalDays + 2440587.5;
        var random = new Random(1631);
        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var rows = Enumerable.Range(0, 4080).Select(n =>
        {
            var q = 0.3 + (5.7 * random.NextDouble() * random.NextDouble());
            var e = n % 7 == 0 ? 1.0 : 0.1 + (0.89 * random.NextDouble());
            var tp = instantJd + ((random.NextDouble() - 0.5) * 40 * 365.25);
            return new string?[]
            {
                null, $"{n + 1}P", $"E2E {n}",
                n < 1995 ? Number(4 + (14 * random.NextDouble())) : null,
                n < 1995 ? Number(5 + (10 * random.NextDouble())) : null,
                Number(e), Number(q), Number(180 * random.NextDouble()), Number(360 * random.NextDouble()), Number(360 * random.NextDouble()),
                Number(tp), Number(instantJd - (random.NextDouble() * 2 * 365.25)),
            };
        });
        return JsonSerializer.Serialize(new
        {
            fields = new[] { "prefix", "pdes", "name", "M1", "K1", "e", "q", "i", "om", "w", "tp", "epoch" },
            data = rows,
            count = 4080,
        });
    }

    /// <summary>
    /// What Play at ten years a second costs the browser with a catalogue of SBDB's size (R1 Q2 of PR #1631): a frame crosses months
    /// nearly every time, and each crossing may start a ranking of the comet set, which on the browser's one thread runs between
    /// frames. Plays half a century, five seconds of the wall clock at least, and reports the rankings started, the last ranking's time, the frames drawn and the
    /// last frame's time; the rankings must stay bounded by the cooldown (OrreryScene.CooldownAfter).
    /// </summary>
    [Fact]
    public async Task PlayAtTenYearsASecondRanksTheCometsBoundedly()
    {
        var page = await fixture.NewPageAsync();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Console += (_, m) =>
        {
            if (m.Text.Contains("[tianwen-web] comets loaded", StringComparison.Ordinal))
            {
                loaded.TrySetResult();
            }
        };
        var body = RealisticCometQueryResponse();
        await page.RouteAsync("**/comets-sbdb.json", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "application/json", Body = body,
        }));
        await page.GotoAsync(fixture.BaseUrl + AtlasLink, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        await loaded.Task.WaitAsync(TimeSpan.FromMilliseconds(BootTimeout), TestContext.Current.CancellationToken);
        var canvas = page.Locator("#planner");

        await canvas.PressAsync("Alt+s");
        await StatsWhenAsync(page, "s.orreryOpen && s.orreryFrames > 0");
        // Every comet ranked (the filter's All), so the set is the thirty and the ranking's whole workload is drawn.
        await PressAsync(canvas, await PaintedAsync(page, $"getButtonRect('{CometFilterAction}')", "filterButton"));
        await PressAsync(canvas, await PaintedAsync(page, $"getDropdownRowRect({AllCometsRow.ToString(CultureInfo.InvariantCulture)})", "filterRow"));
        var stats = await StatsWhenAsync(page, "s.orreryOpen && s.orreryCometFilter === -1 && s.orreryComets >= 30");
        output.WriteLine($"opened: {stats.GetProperty("orreryComets").GetInt32()} comets, the first ranking {stats.GetProperty("lastRankMs").GetDouble():F1} ms, "
            + $"a frame {stats.GetProperty("orreryMs").GetDouble():F1} ms");

        await PressAsync(canvas, await PaintedAsync(page, "getButtonRect('OrrerySpeed')", "speedButton"));
        await PressAsync(canvas, await PaintedAsync(page, $"getDropdownRowRect({TenYearsRow.ToString(CultureInfo.InvariantCulture)})", "speedRow"));
        await StatsWhenAsync(page, $"s.orreryDaysPerSecond === {(10 * 365.25).ToString(CultureInfo.InvariantCulture)}");
        var playButton = await PaintedAsync(page, "getButtonRect('OrreryPlay')", "playButton");

        stats = await StatsWhenAsync(page, "s.orreryOpen");
        var (rankings, frames) = (stats.GetProperty("orreryRankings").GetInt32(), stats.GetProperty("orreryFrames").GetInt32());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await PressAsync(canvas, playButton);
        // Five seconds of the wall clock at least, read off the page's own frames: a played frame steps at most 100 ms, so half a
        // century takes those five seconds however fast the page draws.
        var after = await StatsWhenAsync(page, $"s.orreryPlaying && s.timeOffsetDays >= {(50 * 365.25).ToString(CultureInfo.InvariantCulture)}");
        var seconds = clock.Elapsed.TotalSeconds;
        await PressAsync(canvas, playButton);
        var (rankingsDuring, framesDuring) = (after.GetProperty("orreryRankings").GetInt32() - rankings, after.GetProperty("orreryFrames").GetInt32() - frames);
        var lastRankMs = after.GetProperty("lastRankMs").GetDouble();
        output.WriteLine($"Play at 10 years/s for {seconds:F1} s of the wall clock: {framesDuring} frames ({framesDuring / seconds:F1} a second), "
            + $"{rankingsDuring} rankings started, the last ranking {lastRankMs:F1} ms, the last frame {after.GetProperty("orreryMs").GetDouble():F1} ms");
        Assert.True(framesDuring > 0, "Play drew no frame");
        // Bounded by the cooldown (OrreryScene.CooldownAfter): a ranking waits four of its own durations and at least 100 ms after the
        // last one lands, so ranking takes at most about a fifth of the time and never more than ten start a second. Before the
        // cooldown, 51 rankings started in 57 frames (14.9 s), each 55 to 86 ms on the page's one thread.
        var rankingShare = rankingsDuring * lastRankMs / (seconds * 1000);
        output.WriteLine($"the share of the time spent ranking: {rankingShare:P0}");
        Assert.True(rankingsDuring <= (seconds * 10) + 1, $"{rankingsDuring} rankings in {seconds:F1} s: the cooldown's floor allows ten a second");
        Assert.True(rankingShare <= 0.25, $"ranking took {rankingShare:P0} of the time, past the cooldown's fifth");
        Assert.True(after.GetProperty("orreryMs").GetDouble() < 250, "a played frame took a quarter of a second or more");
        await page.Context.CloseAsync();
    }
}

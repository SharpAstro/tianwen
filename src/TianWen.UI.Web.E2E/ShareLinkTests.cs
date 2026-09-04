using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// Browser E2E for the share link (P20): a URL the desktop viewer's right-click menu produced must
/// open the atlas looking at the frame it came from.
/// </summary>
/// <remarks>
/// <para>This is the only test that exercises the two ENDS together. <c>SkyAtlasLinkTests</c> pins what
/// the desktop writes and the parsers are pinned by nothing else at all, so a unit suite on either side
/// stays green while the two disagree about units -- which is exactly the failure this vocabulary is
/// most likely to have: RA is in hours on one side and degrees on the wire, and both readings of any
/// number are a legal position.</para>
/// <para>The link below is a LITERAL, which is a second copy of the format, and deliberately so: this
/// project is reference-free on purpose (it needs a browser, not the desktop UI stack), so it cannot
/// call the writer. What keeps the copy honest is
/// <c>TianWen.Lib.Tests.SkyAtlasLinkTests.TheWholeLinkIsThisExactly</c>, which pins the identical
/// string against the writer -- so a format change fails there and names this file.</para>
/// </remarks>
[Collection(TianWenWebCollection.Name)]
public sealed class ShareLinkTests(TianWenWebFixture fixture) : IAsyncDisposable
{
    private const float BootTimeout = 120_000;

    private readonly List<IBrowserContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.CloseAsync();
        }
    }

    // M42. Named rather than arbitrary so a wrong answer is recognisable: 15x off in RA lands in
    // Ophiuchus, and a degrees/hours slip on Dec is not possible to miss at -5.
    private const double OrionRaHours = 5.588139;
    private const double OrionDecDeg = -5.391111;
    private const double FovDeg = 2.5;

    // The query the desktop viewer produces for that pointing. Kept in step by
    // SkyAtlasLinkTests.TheWholeLinkIsThisExactly; see the note on this class.
    private const string OrionQuery = "?view=sky&ra=83.822085&dec=-5.391111&fov=2.5000&t=2026-01-18T23:26:51Z";

    private sealed record SkyView(double fovDeg, double centerRA, double centerDec);

    /// <summary>
    /// Opens a share link on a FRESH page: the pointing is applied once, at init, so an in-app
    /// navigation would not exercise the path at all.
    /// </summary>
    private async Task<IPage> OpenLinkAsync(string query)
    {
        var page = await fixture.NewPageAsync();
        _contexts.Add(page.Context);
        // ?e2e=1 registers the view-state hook; a share link carries no such thing, so it is appended
        // to the link the desktop would actually produce rather than replacing part of it.
        await page.GotoAsync(fixture.BaseUrl + query + "&e2e=1", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await Expect(page.Locator("[data-view=sky]")).ToBeVisibleAsync(new() { Timeout = BootTimeout });
        await Expect(page.Locator(".catalog-loading")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });
        return page;
    }

    private static async Task<SkyView> ReadSkyViewAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        json.ShouldNotBe("null", customMessage: "the sky tab has to exist for a pointing to have been applied");
        return JsonSerializer.Deserialize<SkyView>(json)!;
    }

    [Fact]
    public async Task AShareLinkPointsTheAtlasAtTheFrame()
    {
        var page = await OpenLinkAsync(OrionQuery);
        var view = await ReadSkyViewAsync(page);

        // CenterRA is in HOURS on this side and the link carried degrees. Tolerant to a tenth of a
        // degree, not to a factor of fifteen.
        Assert.Equal(OrionRaHours, view.centerRA, 3);
        Assert.Equal(OrionDecDeg, view.centerDec, 3);
        Assert.Equal(FovDeg, view.fovDeg, 3);
    }

    [Fact]
    public async Task AShareLinkLandsOnTheAtlasWhateverTheViewSays()
    {
        // A pointing is meaningless on the planner list, so it overrides the view. Written the way a
        // hand-edited or truncated link plausibly arrives.
        var page = await OpenLinkAsync("?view=planner&ra=83.822085&dec=-5.391111");

        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"),
            new() { Timeout = BootTimeout });
    }

    [Fact]
    public async Task ALinkWithNoPointingLeavesTheViewWhereItWas()
    {
        // Absent is not zero: RA 0 / Dec 0 is a real place in the sky, so a link that says nothing
        // about where to look must not silently point at it.
        var page = await OpenLinkAsync("?view=sky");
        var view = await ReadSkyViewAsync(page);

        Assert.False(view.centerRA == 0 && view.centerDec == 0 && view.fovDeg == 0,
            "an empty pointing must leave the atlas at its own default, not at the origin");
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace TianWen.UI.Web.E2E;

/// <summary>
/// The atlas keeps assembling itself after the catalog chip clears -- the ~30 MB star catalog is
/// fetched, decompressed and flattened, and each of those is a synchronous stall on the one WASM
/// thread. These pin the two things that makes survivable.
///
/// <para><b>The overlay must always come down.</b> Its phases end in a <c>finally</c> for a reason:
/// a dev server with no baked catalog answers 404, and an overlay left up by a failure is an app
/// that cannot be navigated at all -- strictly worse than the freeze it exists to explain. The
/// 404 path is the DEFAULT here (the dev server has no <c>tyc2.bin.lz</c>), so this suite exercises
/// the failure case by construction rather than by simulating it.</para>
/// </summary>
[Collection(TianWenWebCollection.Name)]
public sealed class AtlasLoadingOverlayTests(TianWenWebFixture fixture)
{
    private const float BootTimeout = 120_000;
    private static readonly Regex ActiveClass = new(@"\bactive\b");

    private async Task<IPage> SkyAtlasAsync()
    {
        var page = await fixture.WarmPageAsync();
        await page.Locator("[data-view=sky]").ClickAsync();
        await Expect(page.Locator("[data-view=sky]")).ToHaveClassAsync(ActiveClass, new() { Timeout = BootTimeout });
        return page;
    }

    /// <summary>
    /// However the catalog load ends -- landed, or 404 on a server that never baked it -- the overlay
    /// comes down and the canvas takes input again. Asserted by DRIVING a zoom and reading the field
    /// of view back: "the element is gone" would still pass if the input gate had been left latched,
    /// and a latched gate is indistinguishable from a hung app.
    /// </summary>
    [Fact]
    public async Task TheOverlayClearsAndInputResumesHoweverTheCatalogLoadEnds()
    {
        var page = await SkyAtlasAsync();

        await Expect(page.Locator("[data-atlas-loading]")).ToHaveCountAsync(0, new() { Timeout = BootTimeout });

        var canvas = page.Locator("#planner");
        var before = await FovAsync(page);
        await CanvasGestures.WheelZoomAsync(page, canvas, events: 4, deltaPerEvent: +120.0, burst: false);
        var after = await FovAsync(page);

        Assert.True(after > before,
            $"the canvas ignored a zoom after loading finished (fov {before:F1} -> {after:F1}); "
            + "the input gate is latched");
    }

    /// <summary>
    /// The overlay never eats a click. It is drawn over the canvas, so a stray
    /// <c>pointer-events</c> would break selection on the region it covers for the whole session --
    /// and only while it is UP, which is the hardest kind of bug to reproduce later.
    /// </summary>
    [Fact]
    public async Task TheOverlayIsNotHitTestable()
    {
        var page = await SkyAtlasAsync();

        var pointerEvents = await page.EvaluateAsync<string>("""
            () => {
              // Force it visible for the check regardless of load timing: the property is what
              // matters and it is static CSS, not state.
              const el = document.createElement('div');
              el.className = 'atlas-loading';
              document.querySelector('.canvas-host').appendChild(el);
              const v = getComputedStyle(el).pointerEvents;
              el.remove();
              return v;
            }
            """);

        Assert.Equal("none", pointerEvents);
    }

    private static async Task<double> FovAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>("async () => await window.__tianwenTest.getSkyView()");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("fovDeg").GetDouble();
    }
}

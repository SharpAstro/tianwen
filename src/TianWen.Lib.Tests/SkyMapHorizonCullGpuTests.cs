using DIR.Lib;
using SdlVulkan.Renderer;
using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The star shader's horizon cull, on a real device, through the whole pipeline: geometry built from
/// the real catalogue, uploaded, drawn offscreen and read back as pixels.
/// </summary>
/// <remarks>
/// <para><b>What this adds over <see cref="ShaderContractTests"/>.</b> That one asserts the source
/// still says the safe thing. This one asserts the cull still DOES the right thing, which no amount
/// of reading the text can tell you: a cull condition inverted, a uniform written to the wrong
/// offset, or a star buffer uploaded in the wrong layout all leave the text correct and the picture
/// wrong. It is also the only test in the repo that drives <see cref="VkSkyMapPipeline"/> at all.</para>
/// <para><b>Why the nadir.</b> Pointing at the sky's lowest point puts EVERY star in the field below
/// the horizon, so the whole frame is one assertion with no horizon line to straddle and no
/// arithmetic about which half of the image should be which. The zenith frame is the control that
/// keeps it honest: without it, a build that drew no stars at all for some unrelated reason would
/// pass the nadir assertion perfectly.</para>
/// <para><b>Why brightness and not coverage.</b> The ground fill paints the below-horizon sky its own
/// flat tint, a dark brown, and it is drawn BEFORE the stars, so a star that escapes the cull lands
/// on top of it. Counting near-white pixels separates the two populations without depending on the
/// fill's colour beyond its being dark.</para>
/// <para><b>This does not prove the 2026-09-22 wedge fixed.</b> A conforming driver drops a
/// <c>w = 0</c> primitive just as it drops the out-of-volume one that replaced it, so on a desktop
/// GPU this test is green either way, which is exactly what the desktop measured with the validation
/// layer live. It is worth having on a TILER, where the two are not equivalent, and it is worth
/// having everywhere as the cull's own regression test.</para>
/// </remarks>
[Collection("UI")]
public sealed class SkyMapHorizonCullGpuTests(VkSkyMapGpuFixture gpu) : IClassFixture<VkSkyMapGpuFixture>
{
    private const int Width = VkSkyMapGpuFixture.Width;
    private const int Height = VkSkyMapGpuFixture.Height;

    // Cape Town, and an evening that puts plenty of the southern sky up. The same site the hover
    // tests use, so a failure here and there can be compared without a second set of numbers.
    private const double SiteLatitude = -33.9;
    private const double SiteLongitude = 18.4;
    private static readonly DateTimeOffset When = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A pixel is a star when every channel is at least this. The ground tint is (46, 28, 15).</summary>
    private const byte StarLevel = 128;

    [Fact]
    public async Task NoStarIsPaintedBelowTheHorizonAndTheSkyAboveItIsFullOfThem()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var site = SiteContext.Create(SiteLatitude, SiteLongitude, When);
        site.IsValid.ShouldBeTrue("the horizon clip is only applied for a valid site");

        var pipeline = await gpu.GetStarPipelineAsync(db, When, ct);

        // The zenith is the point the site looks straight up at: right ascension is the local
        // sidereal time, declination is the latitude. The nadir is its antipode.
        var zenith = RenderAt(pipeline, site.LST, SiteLatitude, site);
        var nadir = RenderAt(pipeline, (site.LST + 12.0) % 24.0, -SiteLatitude, site);

        var above = CountStarPixels(zenith);
        var below = CountStarPixels(nadir);

        above.ShouldBeGreaterThan(200,
            "the sky overhead is full of stars, and a control that draws none would make the "
            + "assertion below meaningless");
        below.ShouldBe(0,
            $"every star in a field centred on the nadir is below the horizon and must be culled; "
            + $"{below} star pixels were painted there against {above} at the zenith");
    }

    /// <summary>
    /// <b>The same field, with the clip switched off, is full of stars.</b> Without this the zero
    /// above would be worth nothing: an empty field, a magnitude limit that admitted no star, a star
    /// buffer that never uploaded and a working cull all produce it, and only this tells them apart.
    /// The clip is the ONLY thing that differs between the two renders.
    /// </summary>
    [Fact]
    public async Task WithTheHorizonClipOffTheFieldBelowTheHorizonIsFullOfStars()
    {
        if (!gpu.VulkanAvailable)
        {
            Assert.Skip($"Vulkan runtime not available on this host ({gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var site = SiteContext.Create(SiteLatitude, SiteLongitude, When);

        var pipeline = await gpu.GetStarPipelineAsync(db, When, ct);

        var nadirRa = (site.LST + 12.0) % 24.0;
        var clipped = CountStarPixels(RenderAt(pipeline, nadirRa, -SiteLatitude, site, horizonClip: true));
        var unclipped = CountStarPixels(RenderAt(pipeline, nadirRa, -SiteLatitude, site, horizonClip: false));

        unclipped.ShouldBeGreaterThan(200,
            "the nadir field holds plenty of stars; if it did not, the cull test above would pass "
            + "over an empty sky");
        clipped.ShouldBe(0, "and the clip is the only difference between these two frames");
    }

    /// <summary>One offscreen frame with the view centred on a point of the sky, as RGBA.</summary>
    private byte[] RenderAt(VkSkyMapPipeline pipeline, double centerRaHours, double centerDecDeg, SiteContext site, bool horizonClip = true)
    {
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Horizon,
            CenterRA = centerRaHours,
            CenterDec = centerDecDeg,
            FieldOfViewDeg = 60.0,

            // ShowHorizon is what turns the cull on (SkyMapUbo writes horizonClip from it), and it
            // also draws the ground fill, which is why the count below is of near-white pixels.
            ShowHorizon = horizonClip,
            ShowMilkyWay = false,
            ShowAltAzGrid = false,

            // DrawOwnGrid is derived from this, and the grid would paint bright lines the star
            // count cannot tell from a star.
            ShowGrid = false,
        };

        return gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx!;
            var renderer = gpu.Renderer!;

            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            pipeline.UpdateUbo(state, Width, Height, 0f, 0f, site, ctx.CurrentFrame);
            pipeline.Draw(
                renderer.CurrentCommandBuffer, state, Width, Height, 0f, 0f, 0f,
                default, default, default);
            renderer.EndOffscreenFrame();
            return ctx.ReadbackOffscreenRgba();
        });
    }

    /// <summary>Pixels bright in every channel: a star's core, never the ground tint behind it.</summary>
    private static int CountStarPixels(byte[] rgba)
    {
        var count = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] >= StarLevel && rgba[i + 1] >= StarLevel && rgba[i + 2] >= StarLevel)
            {
                count++;
            }
        }

        return count;
    }
}

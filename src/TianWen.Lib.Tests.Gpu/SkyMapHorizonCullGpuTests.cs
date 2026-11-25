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
[Collection(GpuCollection.Name)]
public sealed class SkyMapHorizonCullGpuTests(VkSkyMapGpuFixture gpu) : IClassFixture<VkSkyMapGpuFixture>
{
    private const int Width = VkSkyMapGpuFixture.Width;
    private const int Height = VkSkyMapGpuFixture.Height;

    // Cape Town, and an evening that puts plenty of the southern sky up. The same site the hover
    // tests use, so a failure here and there can be compared without a second set of numbers.
    private const double SiteLatitude = -33.9;
    private const double SiteLongitude = 18.4;
    private static readonly DateTimeOffset When = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A pixel is a star when every channel is at least this. The ground is at most (46, 28, 15).</summary>
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

    /// <summary>
    /// <b>You cannot see through the ground.</b> Cleared to white, a field centred on the nadir must
    /// come back no brighter anywhere than the ground's own colour: a translucent fill (the ground was
    /// 45% to 88% opaque before) lets at least 12% of the white through, which lifts every channel by
    /// 30 or more and the blue far past the tint's 15. The control is the same frame without the
    /// ground, which must still be white, so a frame that drew nothing cannot pass.
    /// </summary>
    [Fact]
    public async Task TheGroundIsOpaqueSoNothingBehindItShowsThrough()
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
        var white = new RGBAColor32(255, 255, 255, 255);

        // Faint constellation lines are allowed under the ground; this is about the fill alone.
        static void FillOnly(SkyMapState s)
        {
            s.ShowConstellationBoundaries = false;
            s.ShowConstellationFigures = false;
        }

        var withoutGround = RenderAt(pipeline, nadirRa, -SiteLatitude, site, horizonClip: false, clear: white, configure: FillOnly);
        var withGround = RenderAt(pipeline, nadirRa, -SiteLatitude, site, horizonClip: true, clear: white, configure: FillOnly);

        var whiteWithout = CountWhitePixels(withoutGround);
        whiteWithout.ShouldBeGreaterThan(Width * Height / 2,
            "without the ground the white clear colour fills the field; if it did not, the frame below proves nothing");

        var brightest = BrightestChannels(withGround);
        brightest.R.ShouldBeLessThanOrEqualTo(GroundAtTheHorizon.R,
            $"no pixel of the ground may be brighter than its own colour; the brightest was {brightest}");
        brightest.G.ShouldBeLessThanOrEqualTo(GroundAtTheHorizon.G, $"the brightest was {brightest}");
        brightest.B.ShouldBeLessThanOrEqualTo(GroundAtTheHorizon.B, $"the brightest was {brightest}");
    }

    /// <summary>
    /// <b>Under the ground the grid draws nothing and the constellation lines draw faintly.</b> The RA/Dec
    /// grid crosses every part of a 60° field, so turning it on over the nadir changes no byte of the
    /// frame when the ground is drawn, and plenty without it (the control). Constellation boundaries are
    /// the one exception the ground makes: they show, but at well under half the strength they have off
    /// the ground (<see cref="SkyMapGpuGeometry.BelowHorizon.Faint"/>).
    /// </summary>
    [Fact]
    public async Task UnderTheGroundTheGridIsHiddenAndTheConstellationBoundariesAreFaint()
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

        static Action<SkyMapState> Lines(bool grid, bool boundaries) => s =>
        {
            s.ShowGrid = grid;
            s.ShowConstellationBoundaries = boundaries;
            s.ShowConstellationFigures = false;
        };

        byte[] Render(bool ground, bool grid, bool boundaries)
            => RenderAt(pipeline, nadirRa, -SiteLatitude, site, horizonClip: ground, configure: Lines(grid, boundaries));

        // The grid: the control first, then the frame that must not change.
        var bare = Render(ground: false, grid: false, boundaries: false);
        LargestChannelChange(Render(ground: false, grid: true, boundaries: false), bare).ShouldBeGreaterThan(20,
            "the grid crosses the whole field; if it painted nothing here the assertion below would prove nothing");

        var onTheGround = Render(ground: true, grid: false, boundaries: false);
        var again = Render(ground: true, grid: false, boundaries: false);
        LargestChannelChange(again, onTheGround).ShouldBe(0,
            $"the same frame twice is byte-identical, which is what lets the grid be held to no change at all ({Describe(again, onTheGround)})");

        // This caught a stray line, one level dark, from a grid segment with one end culled near the view's antipode: the
        // clipper keeps such a segment, and its culled end read as sky (skymap_line.vert's vProjected).
        var withGrid = Render(ground: true, grid: true, boundaries: false);
        LargestChannelChange(withGrid, onTheGround).ShouldBe(0,
            $"the grid is hidden under the ground: not a pixel of it may be drawn there ({Describe(withGrid, onTheGround)})");

        // The boundaries: drawn on both frames, faint on the ground.
        var full = LargestChannelChange(Render(ground: false, grid: false, boundaries: true), bare);
        var faint = LargestChannelChange(Render(ground: true, grid: false, boundaries: true), onTheGround);
        full.ShouldBeGreaterThan(20, "the nadir field is crossed by constellation boundaries");
        faint.ShouldBeGreaterThan(0, "a faint border of the constellations still shows on the ground");
        faint.ShouldBeLessThan(full / 2,
            $"and it is faint: the boundaries changed a channel by {faint} on the ground against {full} off it");
    }

    /// <summary>The ground's colour where it meets the horizon, its brightest (skymap_hzfill.frag's groundTint, (46, 28, 15) in UNORM), plus one level for rounding.</summary>
    private static readonly (byte R, byte G, byte B) GroundAtTheHorizon = (47, 29, 16);

    /// <summary>One offscreen frame with the view centred on a point of the sky, as RGBA.</summary>
    private byte[] RenderAt(
        VkSkyMapPipeline pipeline, double centerRaHours, double centerDecDeg, SiteContext site, bool horizonClip = true,
        RGBAColor32? clear = null, Action<SkyMapState>? configure = null)
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
        configure?.Invoke(state);

        return gpu.Invoke(() =>
        {
            var ctx = gpu.Ctx!;
            var renderer = gpu.Renderer!;

            renderer.BeginOffscreenFrame(clear ?? new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            pipeline.UpdateUbo(state, Width, Height, 0f, 0f, site, ctx.CurrentFrame);
            pipeline.Draw(
                renderer.CurrentCommandBuffer, state, Width, Height, 0f, 0f, 0f,
                default, default, default);
            renderer.EndOffscreenFrame();
            return ctx.ReadbackOffscreenRgba();
        });
    }

    /// <summary>Pixels at the white clear colour in every channel.</summary>
    private static int CountWhitePixels(byte[] rgba)
    {
        var count = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] == 255 && rgba[i + 1] == 255 && rgba[i + 2] == 255)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The largest value each colour channel reaches anywhere in the frame.</summary>
    private static (byte R, byte G, byte B) BrightestChannels(byte[] rgba)
    {
        byte r = 0, g = 0, b = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            r = Math.Max(r, rgba[i]);
            g = Math.Max(g, rgba[i + 1]);
            b = Math.Max(b, rgba[i + 2]);
        }

        return (r, g, b);
    }

    /// <summary>How many pixels differ between two frames, and the first of them, for a failure message.</summary>
    private static string Describe(byte[] a, byte[] b)
    {
        var changed = 0;
        var first = "";
        for (var i = 0; i + 3 < a.Length; i += 4)
        {
            if ((a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) && changed++ == 0)
            {
                var p = i / 4;
                first = $", the first at ({p % Width}, {p / Width}): {a[i]},{a[i + 1]},{a[i + 2]} against {b[i]},{b[i + 1]},{b[i + 2]}";
            }
        }

        return $"{changed} pixels changed{first}";
    }

    /// <summary>The largest change of any colour channel of any pixel between two frames of the same size.</summary>
    private static int LargestChannelChange(byte[] a, byte[] b)
    {
        a.Length.ShouldBe(b.Length);
        var largest = 0;
        for (var i = 0; i + 3 < a.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                largest = Math.Max(largest, Math.Abs(a[i + c] - b[i + c]));
            }
        }

        return largest;
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

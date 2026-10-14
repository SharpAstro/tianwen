using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Png;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas's textured planet discs through the REAL tab render on the CPU surface (docs/plans/atlas-planet-detail.md, "A4: the
/// textured disc"): no sprite work at a wide field, sprite builds COUNTED over a pan, a zoom and a time scrub, and the sprite's pixels
/// on the surface where the planet is.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapPlanetDiscTests
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
    private const int Size = 600;

    // Publishes the view matrix each frame the way the GPU pipelines do, so the planets are projected with a matrix that agrees
    // with the centre.
    private sealed class DiscTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site, phase);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }
    }

    private sealed record Rig(DiscTab Tab, RgbaImageRenderer Renderer, PlannerState Planner, FakeTimeProviderWrapper Time) : IDisposable
    {
        public RectF32 Content => new(0, 0, Size, Size);

        public void Render() => Tab.Render(Planner, Content, Time);

        // Renders, lets whatever the frame started (the atlas read, a sprite build) land, and renders again.
        public async Task RenderSettledAsync()
        {
            // Twice: the first settle may only bring the atlas in, after which the next frame starts the build.
            for (var pass = 0; pass < 2; pass++)
            {
                Render();
                if (Tab.PlanetDiscsTouched)
                {
                    if (Tab.PlanetTextures.AtlasLoad is { } atlas)
                    {
                        await atlas.WaitAsync(TestContext.Current.CancellationToken);
                    }
                    await Tab.PlanetSprites.WhenBuiltAsync();
                }
            }
            Render();
        }

        public void Look(CatalogIndex body, double diameterPx)
        {
            var utc = Instant + Tab.State.TimeOffset;
            Tab.State.TryGetPlanetCached(body, utc, out var marker).ShouldBeTrue();
            var arcsec = marker.Aspect.ShouldNotBeNull().AngularDiameterArcsec;
            var ppr = diameterPx / (arcsec / 3600 * Math.PI / 180);
            Tab.State.CenterRA = marker.RA;
            Tab.State.CenterDec = marker.Dec;
            Tab.State.FieldOfViewDeg = 4 * Math.Atan(Size / (4 * ppr)) * 180 / Math.PI;
        }

        public void Dispose() => Renderer.Dispose();
    }

    private static async Task<Rig> RigAsync(System.Threading.CancellationToken cancellationToken)
    {
        var db = await SharedCatalogDB.InitAsync(cancellationToken);
        var renderer = new RgbaImageRenderer(Size, Size);
        var tab = new DiscTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = Instant,
        };
        var rig = new Rig(tab, renderer, planner, new FakeTimeProviderWrapper(Instant));
        rig.Render();
        tab.State.ShowHorizon = false;
        return rig;
    }

    /// <summary>
    /// At a 60 degree field Jupiter is a fraction of a pixel: the dot is drawn, and nothing about a disc is touched, no texture asked
    /// for and no sprite built, however many frames.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AWideFieldDoesNoSpriteWork()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Look(CatalogIndex.Jupiter, 1);
        rig.Tab.State.FieldOfViewDeg = 60;

        for (var frame = 0; frame < 5; frame++)
        {
            rig.Render();
        }

        rig.Tab.PlanetDiscsTouched.ShouldBeFalse();
        rig.Tab.PlanetSpritesDrawn.ShouldBe(0);
    }

    /// <summary>
    /// Builds are COUNTED, never timed: a small pan rebuilds nothing; a zoom rebuilds once per size bucket it crosses and never per
    /// frame within one; a time scrub of Jupiter (its meridian turns 0.6 degrees a minute) rebuilds as the bucketed meridian moves, so
    /// some of ten one-minute steps and not all; an hour's jump once. Each bound is paired with one above zero.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SpriteBuildsAreCountedOverAPanAZoomAndATimeScrub()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        // Equatorial: a fixed roll, where the horizon view eases its roll toward the vertical over real time between frames.
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Look(CatalogIndex.Jupiter, 60);
        await rig.RenderSettledAsync();
        var sprites = rig.Tab.PlanetSprites;
        sprites.Builds.ShouldBe(1);
        rig.Tab.PlanetSpritesDrawn.ShouldBeGreaterThan(0);

        // Pan: a twentieth of the field, five times; the disc stays on screen.
        var fov = rig.Tab.State.FieldOfViewDeg;
        for (var i = 0; i < 5; i++)
        {
            rig.Tab.State.CenterRA += fov / 20 / 15;
            await rig.RenderSettledAsync();
        }
        sprites.Builds.ShouldBe(1, "a pan rebuilds nothing");

        // Zoom: half again bigger, in ten frames.
        var buckets = new HashSet<int>();
        for (var i = 1; i <= 10; i++)
        {
            var diameter = 60 * (1 + (0.05 * i));
            buckets.Add(PlanetSpriteCache.SizeBucket(CatalogIndex.Jupiter, diameter).Bucket);
            rig.Tab.State.FieldOfViewDeg = fov / (1 + (0.05 * i));
            await rig.RenderSettledAsync();
        }
        var afterZoom = sprites.Builds - 1;
        afterZoom.ShouldBeGreaterThan(0);
        afterZoom.ShouldBeLessThanOrEqualTo(buckets.Count, "once per size bucket crossed, never per frame");

        // Time scrub: ten one-minute steps, then an hour.
        var beforeScrub = sprites.Builds;
        for (var i = 0; i < 10; i++)
        {
            rig.Tab.State.TimeOffset += TimeSpan.FromMinutes(1);
            await rig.RenderSettledAsync();
        }
        var scrub = sprites.Builds - beforeScrub;
        scrub.ShouldBeGreaterThan(0);
        scrub.ShouldBeLessThan(10, "a minute turns Jupiter less than a bucket at this size");
        var beforeJump = sprites.Builds;
        rig.Tab.State.TimeOffset += TimeSpan.FromHours(1);
        await rig.RenderSettledAsync();
        (sprites.Builds - beforeJump).ShouldBe(1);
    }

    /// <summary>
    /// The disc is the planet's hit area: a click 80 % of the way to the Moon's limb, far past the 20 px a dot is hit within, selects
    /// the Moon, whatever star lies behind it, through the one resolver the click and the hover share.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AClickAnywhereOnTheDiscSelectsThePlanet()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Look(CatalogIndex.Moon, 400);
        await rig.RenderSettledAsync();
        var state = rig.Tab.State;
        var ppr = SkyMapProjection.PixelsPerRadian(Size, state.FieldOfViewDeg);
        var search = new SkyMapSearchState();

        SkyMapSearchActions.SelectObjectByClick(search, state, rig.Planner.ObjectDb.ShouldNotBeNull(), 48.0, 11.0, Instant,
            SiteContext.Create(48.0, 11.0, Instant), (Size / 2f) + 160, Size / 2f, state.ComputeViewMatrix(), ppr, Size / 2f, Size / 2f)
            .ShouldBeTrue();

        search.InfoPanel.ShouldNotBeNull().Index.ShouldBe(CatalogIndex.Moon);
    }

    /// <summary>
    /// The hover wash is the object's own footprint (CLAUDE.md, "The atlas's hover highlight"): over a Moon drawn 400 px across it is
    /// the disc's 200 px radius, from the same resolver the click uses; over Jupiter at 4 px, a dot, it stays the dot's tolerance.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Moon, 400.0, 200f)]
    [InlineData(CatalogIndex.Jupiter, 4.0, 20f)]
    public async Task TheHoverWashIsTheDiscsRadius(CatalogIndex body, double diameterPx, float expectedRadius)
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Look(body, diameterPx);
        await rig.RenderSettledAsync();

        var target = SkyMapSearchActions.ResolveHoverAtScreenPoint(rig.Tab.State, rig.Planner.ObjectDb.ShouldNotBeNull(), Instant,
            (Size / 2f) + 1, Size / 2f, pinnedCatalogIndices: null).ShouldNotBeNull();

        target.Index.ShouldBe(body);
        target.HitRadiusPx.ShouldBe(expectedRadius, expectedRadius * 0.02f);
    }

    /// <summary>
    /// The CPU surface shows the sprite: at about 200 px the disc's pixels are the texture's (many colours, none of them the dot's) and
    /// cover the disc, for Jupiter, Saturn, Venus and the Moon. The four are written to the test output folder as one PNG, the
    /// screenshot-equivalent of the feature. The build cost of a 64 and a 256 px sprite is measured beside it.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheCpuSurfaceShowsTheSpritesPixels()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        CatalogIndex[] bodies = [CatalogIndex.Jupiter, CatalogIndex.Saturn, CatalogIndex.Venus, CatalogIndex.Moon];
        var sheet = new byte[Size * 2 * Size * 2 * 4];
        var results = new List<(CatalogIndex Body, int Inside, int Textured, int Colours)>();
        for (var b = 0; b < bodies.Length; b++)
        {
            var body = bodies[b];
            // Saturn's sprite reaches its rings, so its globe is smaller for the same sprite.
            rig.Look(body, body == CatalogIndex.Saturn ? 100 : 200);
            await rig.RenderSettledAsync();

            var image = rig.Renderer.Surface;
            var dot = SkyMapRenderer.GetPlanetColor(body);
            var colours = new HashSet<int>();
            int inside = 0, textured = 0;
            // The whole disc but its outermost pixel: a crescent's light is all at the limb.
            var radius = body == CatalogIndex.Saturn ? 49 : 98;
            for (var y = (Size / 2) - radius; y < (Size / 2) + radius; y++)
            {
                for (var x = (Size / 2) - radius; x < (Size / 2) + radius; x++)
                {
                    if (((x - (Size / 2)) * (x - (Size / 2))) + ((y - (Size / 2)) * (y - (Size / 2))) > radius * radius)
                    {
                        continue;
                    }
                    var i = ((y * Size) + x) * 4;
                    var (r, g, bl) = (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
                    inside++;
                    if ((r, g, bl) != (dot.Red, dot.Green, dot.Blue))
                    {
                        textured++;
                        colours.Add((r << 16) | (g << 8) | bl);
                    }
                }
            }
            results.Add((body, inside, textured, colours.Count));

            for (var y = 0; y < Size; y++)
            {
                Array.Copy(image.Pixels, y * Size * 4, sheet, ((((b / 2) * Size) + y) * Size * 2 * 4) + ((b % 2) * Size * 4), Size * 4);
            }
        }
        rig.Tab.PlanetSpritesDrawn.ShouldBeGreaterThanOrEqualTo(bodies.Length);

        var dir = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(TheCpuSurfaceShowsTheSpritesPixels));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "planet-discs.png"), PngWriter.Encode(sheet, Size * 2, Size * 2), TestContext.Current.CancellationToken);
        foreach (var (body, inside, textured, count) in results)
        {
            textured.ShouldBe(inside, $"{body}: no pixel of the disc is the dot");
            count.ShouldBeGreaterThan(50, $"{body}: the texture and the shading, not a flat fill");
        }

        // Cost: one build each at 64 and 256 px, Jupiter from the atlas, after a warm-up.
        rig.Tab.PlanetTextures.TryGet(CatalogIndex.Jupiter, out var map, out _).ShouldBeTrue();
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Instant);
        PlanetDiscSprite.Build(CatalogIndex.Jupiter, map, default, aspect, 32, -90, false, null, out _);
        var report = "";
        foreach (var px in new[] { 64, 256 })
        {
            var watch = Stopwatch.StartNew();
            PlanetDiscSprite.Build(CatalogIndex.Jupiter, map, default, aspect, (px - 2) / 2.0, -90, false, null, out var side);
            report += $"{side} px: {watch.Elapsed.TotalMilliseconds:F1} ms; ";
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "build-cost.txt"), $"{report}{Environment.ProcessorCount} cores", TestContext.Current.CancellationToken);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                    await Tab.PanelSprites.WhenBuiltAsync();
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
    /// A planet whose disc is NOT drawn (its texture has not landed: here no atlas at all) is still the dot, and is hit as the dot: a
    /// click 160 px from a Moon that would be 400 px across does not select it, one at its centre does.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APlanetWhoseDiscIsNotDrawnIsHitOnlyAsItsDot()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.PlanetTextures.AtlasPath = Path.Combine(AppContext.BaseDirectory, "no-such-atlas.png");
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Look(CatalogIndex.Moon, 400);
        await rig.RenderSettledAsync();
        rig.Tab.PlanetSpritesDrawn.ShouldBe(0, "no texture, no disc");
        var state = rig.Tab.State;
        var ppr = SkyMapProjection.PixelsPerRadian(Size, state.FieldOfViewDeg);
        var db = rig.Planner.ObjectDb.ShouldNotBeNull();

        CatalogIndex? Select(float dx)
        {
            var search = new SkyMapSearchState();
            SkyMapSearchActions.SelectObjectByClick(search, state, db, 48.0, 11.0, Instant, SiteContext.Create(48.0, 11.0, Instant),
                (Size / 2f) + dx, Size / 2f, state.ComputeViewMatrix(), ppr, Size / 2f, Size / 2f);
            return search.InfoPanel?.Index;
        }

        Select(160).ShouldNotBe(CatalogIndex.Moon, "far outside the dot");
        Select(5).ShouldBe(CatalogIndex.Moon, "on the dot");
    }

    // Selects `body` as a click would, at the rig's instant, and renders until its panel's disc has landed.
    private static async Task<SkyMapInfoPanelData> SelectAsync(Rig rig, CatalogIndex body)
    {
        var utc = Instant + rig.Tab.State.TimeOffset;
        rig.Tab.State.TryGetPlanetCached(body, utc, out var marker).ShouldBeTrue();
        var info = SkyMapSearchActions.PlanetInfoPanel(rig.Planner.ObjectDb.ShouldNotBeNull(), marker, 48.0, 11.0, utc,
            SiteContext.Create(48.0, 11.0, utc));
        rig.Tab.State.Search.InfoPanel = info;
        await rig.RenderSettledAsync();
        return info;
    }

    // The pixels of the panel's disc slot that are not its dark frame: (count, centroid x, centroid y, leftmost, rightmost),
    // relative to the slot's centre.
    private static (int Count, double X, double Y, int Left, int Right) LitInSlot(Rig rig, RectF32 slot)
    {
        var image = rig.Renderer.Surface;
        var (count, sx, sy, left, right) = (0, 0.0, 0.0, int.MaxValue, int.MinValue);
        var (cx, cy) = (slot.X + (slot.Width / 2), slot.Y + (slot.Height / 2));
        for (var y = (int)slot.Y; y < (int)(slot.Y + slot.Height); y++)
        {
            for (var x = (int)slot.X; x < (int)(slot.X + slot.Width); x++)
            {
                var i = ((y * image.Width) + x) * 4;
                var sum = image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2];
                if (sum > 60)
                {
                    count++;
                    sx += sum * (x - cx);
                    sy += sum * (y - cy);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }
        return (count, sx, sy, left, right);
    }

    /// <summary>
    /// Selecting Saturn puts its disc in the panel's disc slot: the sprite's pixels fill the slot's middle, and its rings reach well
    /// past its globe (north up, the rings near level, so across the slot), from the same sprite path as the map.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SelectingSaturnPutsItsRingedDiscInThePanel()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Look(CatalogIndex.Saturn, 1);
        await SelectAsync(rig, CatalogIndex.Saturn);

        var slot = rig.Tab.LastPanelDiscRect.ShouldNotBeNull("the panel declares a disc slot for a planet");
        var (count, _, _, left, right) = LitInSlot(rig, slot);
        count.ShouldBeGreaterThan(300, "the sprite's pixels are in the slot");
        var globe = PlanetDiscSprite.PanelRadiusPx(CatalogIndex.Saturn, slot.Width, slot.Height);
        ((double)(right - left)).ShouldBeGreaterThan(3.5 * globe, "the rings reach past the globe's diameter");
    }

    /// <summary>
    /// Selecting the Moon (a 6 % crescent on 2026-10-13) puts it in the panel north up and east left, as the sky shows it: the light's
    /// centroid lies toward the bright limb's position angle.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SelectingTheMoonPutsItsPhasedDiscInThePanel()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Look(CatalogIndex.Moon, 1);
        var info = await SelectAsync(rig, CatalogIndex.Moon);

        var slot = rig.Tab.LastPanelDiscRect.ShouldNotBeNull();
        var (count, sx, sy, _, _) = LitInSlot(rig, slot);
        count.ShouldBeGreaterThan(30);
        var chi = info.Aspect.ShouldNotBeNull().BrightLimbPositionAngle * Math.PI / 180;
        var cos = ((sx * -Math.Sin(chi)) + (sy * -Math.Cos(chi))) / Math.Sqrt((sx * sx) + (sy * sy));
        Math.Acos(Math.Clamp(cos, -1, 1)).ShouldBeLessThan(15 * Math.PI / 180, "the lit limb faces the Sun");
    }

    /// <summary>An object that is not a solar-system body gets no disc slot: its panel is as it was.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ADeepSkyObjectGetsNoDiscSlot()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.State.Search.InfoPanel = SkyMapInfoPanelData.FromPosition("Test Object", 5.0, 20.0, 48.0, 11.0, Instant, default);
        await rig.RenderSettledAsync();

        rig.Tab.LastPanelDiscRect.ShouldBeNull();
    }

    /// <summary>
    /// The panel's sprite is keyed at the PANEL's size, so frames that change nothing build nothing: ten renders of a selected
    /// Jupiter cost its one build.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ThePanelsDiscDoesNotRebuildEveryFrame()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Look(CatalogIndex.Jupiter, 1);
        await SelectAsync(rig, CatalogIndex.Jupiter);
        var builds = rig.Tab.PanelSprites.Builds;
        builds.ShouldBeGreaterThan(0);

        for (var frame = 0; frame < 10; frame++)
        {
            await rig.RenderSettledAsync();
        }

        rig.Tab.PanelSprites.Builds.ShouldBe(builds);
    }

    /// <summary>
    /// The panel as a person sees it, written as PNGs (the review's screenshot-equivalent): Saturn on 2026-10-13 and the Moon five
    /// days on (a waxing crescent near quarter), each selected, at a DPI scale of 1.5 (the GUI's on this laptop), the panel's
    /// bottom-left region cropped out of a 1200 x 900 CPU surface. Asserts only that the disc was drawn; the pictures are for eyes.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(CatalogIndex.Saturn, 0)]
    [InlineData(CatalogIndex.Moon, 5)]
    public async Task ThePanelWithItsDiscIsWrittenAsAPicture(CatalogIndex body, int daysOn)
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        const int W = 1200, H = 900;
        using var renderer = new RgbaImageRenderer(W, H);
        var tab = new DiscTab(renderer) { FontPath = FontResolver.ResolveSystemFont(), DpiScale = 1.5f };
        var planner = new PlannerState
        {
            ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1), PlanningDate = Instant,
        };
        var time = new FakeTimeProviderWrapper(Instant);
        var content = new RectF32(0, 0, W, H);
        tab.Render(planner, content, time);
        tab.State.TimeOffset = TimeSpan.FromDays(daysOn);
        var utc = Instant + tab.State.TimeOffset;
        tab.State.TryGetPlanetCached(body, utc, out var marker).ShouldBeTrue();
        tab.State.CenterRA = marker.RA;
        tab.State.CenterDec = marker.Dec;
        tab.State.FieldOfViewDeg = 20;
        tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(db, marker, 48.0, 11.0, utc, SiteContext.Create(48.0, 11.0, utc));
        for (var pass = 0; pass < 3; pass++)
        {
            tab.Render(planner, content, time);
            if (tab.PlanetTextures.AtlasLoad is { } atlas)
            {
                await atlas.WaitAsync(ct);
            }
            await tab.PanelSprites.WhenBuiltAsync();
        }
        tab.Render(planner, content, time);

        var slot = tab.LastPanelDiscRect.ShouldNotBeNull();
        var (x0, y0) = (0, Math.Max(0, (int)slot.Y - 260));
        var (cw, ch) = (Math.Min(W, 560), H - y0);
        var crop = new byte[cw * ch * 4];
        for (var y = 0; y < ch; y++)
        {
            Array.Copy(renderer.Surface.Pixels, (((y0 + y) * W) + x0) * 4, crop, y * cw * 4, cw * 4);
        }
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.Lib.Tests", DateTime.Now.ToString("yyyyMMdd"), nameof(ThePanelWithItsDiscIsWrittenAsAPicture));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"panel-{body}.png".ToLowerInvariant()), PngWriter.Encode(crop, cw, ch), ct);
        tab.PanelSprites.Builds.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A disc is culled on its REACH, not its centre: a Moon 400 px across whose centre sits 50 px above the view's top edge still
    /// covers the top 150 px of it, so it is drawn there (bright: thirteen days on, near full) and stays clickable as a disc.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADiscWhoseCentreIsOffScreenIsStillDrawnWhereItReaches()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Tab.State.TimeOffset = TimeSpan.FromDays(13);
        rig.Look(CatalogIndex.Moon, 400);
        // North is up: moving the centre south by half the view and 50 px puts the Moon's centre 50 px above the top edge.
        var ppr = SkyMapProjection.PixelsPerRadian(Size, rig.Tab.State.FieldOfViewDeg);
        rig.Tab.State.CenterDec -= ((Size / 2.0) + 50) / ppr * 180 / Math.PI;
        await rig.RenderSettledAsync();

        rig.Tab.State.PlanetDiscsDrawn.ShouldContain(CatalogIndex.Moon);
        var image = rig.Renderer.Surface;
        var lit = 0;
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = ((y * Size) + x) * 4;
                lit += image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2] > 240 ? 1 : 0;
            }
        }
        lit.ShouldBeGreaterThan(1000, "the Moon's lower limb covers the top rows");
    }

    /// <summary>
    /// The label starts clear of everything the sprite draws: Saturn's sprite reaches its ring ansae, about 2.27 globe radii, so its
    /// label's clickable begins past them, not 4 px past the globe and over the rings.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SaturnsLabelClearsItsRings()
    {
        using var rig = await RigAsync(TestContext.Current.CancellationToken);
        rig.Tab.State.Mode = SkyMapMode.Equatorial;
        rig.Look(CatalogIndex.Saturn, 100);
        await rig.RenderSettledAsync();
        rig.Tab.State.PlanetDiscsDrawn.ShouldContain(CatalogIndex.Saturn);

        var label = rig.Tab.GetRegisteredRegions()
            .Single(r => r.Result is HitResult.ButtonHit { Action: var action } && action.StartsWith("SkyMapPlanetLabel:", StringComparison.Ordinal)
                && action.EndsWith("Saturn", StringComparison.Ordinal));
        var ringEdge = (Size / 2f) + (float)(PlanetDiscSprite.ExtentRadii(CatalogIndex.Saturn) * 50);
        label.X.ShouldBeGreaterThanOrEqualTo(ringEdge, "the label begins past the rings' ansa");
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The picture section of the shared object panel: its size, its credit, and where the atlas asks a host to
/// draw it (<c>docs/plans/object-imagery.md</c> P1).
/// </summary>
[Collection("Astrometry")]
public sealed class ObjectPicturePanelTests
{
    private static ObjectArticleImage Picture(int width = 4000, int height = 3000, string artist = "ESO/S. Guisard", string credit = "ESO")
        => new ObjectArticleImage("Lagoon Nebula (ESO).jpg", "2c", "CC BY 4.0", artist, credit,
            AttributionRequired: true, Width: width, Height: height);

    [Theory]
    [InlineData(4000, 2000, ObjectInfoPanel.DesignPictureWidth * 0.5f)]  // its own aspect ratio
    [InlineData(8000, 1000, ObjectInfoPanel.DesignPictureMinHeight)]     // a panorama is not a sliver
    [InlineData(1000, 4000, ObjectInfoPanel.DesignPictureMaxHeight)]     // a tall frame does not push the buttons off
    public void ThePictureTakesItsAspectRatioWithinBounds(int width, int height, float expected)
        => ObjectInfoPanel.DesignPictureHeight(Picture(width, height)).ShouldBe(expected, 0.01f);

    [Fact]
    public void APictureGrowsThePanelByItsSection()
    {
        var picture = Picture();
        var actions = new ObjectInfoPanel.PanelActions(Goto: () => { });
        var without = new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true);
        var with = without with { Picture = picture };

        (ObjectInfoPanel.DesignHeight(in with, in actions) - ObjectInfoPanel.DesignHeight(in without, in actions))
            .ShouldBe(ObjectInfoPanel.DesignPictureSectionHeight(in picture), 0.01f);
    }

    [Theory]
    [InlineData("ESO/S. Guisard", "ESO", "ESO/S. Guisard, CC BY 4.0")]
    [InlineData("", "NASA, ESA", "NASA, ESA, CC BY 4.0")]
    [InlineData("", "", "CC BY 4.0")]
    public void TheCreditNamesWhoMadeItAndTheLicence(string artist, string credit, string expected)
        => ObjectInfoPanel.CreditLine(Picture(artist: artist, credit: credit)).ShouldBe(expected);

    [Fact]
    public void ALongArtistIsCutRatherThanRunningOffThePanel()
        => ObjectInfoPanel.CreditLine(Picture(artist: new string('a', 80))).ShouldBe(new string('a', 48) + "..., CC BY 4.0");

    /// <summary>Records where the atlas asks for the picture, and still draws everything else.</summary>
    private sealed class PictureCapturingTab(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        public List<(ObjectArticleImage Image, RectF32 Rect)> Pictures { get; } = [];

        protected override void DrawObjectPicture(in ObjectArticleImage image, RectF32 rect) => Pictures.Add((image, rect));
    }

    private static (PictureCapturingTab Tab, PlannerState Planner, ITimeProvider Clock, RectF32 Content) Atlas(
        ICelestialObjectDB db, RgbaImageRenderer renderer, SkyMapInfoPanelData info, DateTimeOffset now)
    {
        var tab = new PictureCapturingTab(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        tab.State.Search.InfoPanel = info;
        var planner = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = 45.0,
            SiteLongitude = -75.0,
            SiteTimeZone = TimeSpan.Zero,
            PlanningDate = now,
        };
        return (tab, planner, new FakeTimeProviderWrapper(now), new RectF32(0, 0, renderer.Width, renderer.Height));
    }

    [Fact]
    public async Task TheAtlasPanelAsksForTheVerifiedPictureUnderItsRowsWithItsCreditLinked()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var andromeda).ShouldBeTrue();
        db.TryGetArticle(andromeda.Index, out var article).ShouldBeTrue();
        var picture = article.Image.ShouldNotBeNull("the test needs an object whose article kept a picture");

        var now = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(45.0, -75.0, now);
        var info = SkyMapInfoPanelData.FromCatalogObject(andromeda, 45.0, -75.0, now, site, null);

        using var renderer = new RgbaImageRenderer(900, 900);
        var (tab, planner, clock, content) = Atlas(db, renderer, info, now);
        tab.Render(planner, content, clock);

        var (image, rect) = tab.Pictures.ShouldHaveSingleItem();
        image.ShouldBe(picture);
        rect.Width.ShouldBe(ObjectInfoPanel.DesignPictureWidth, 1f);
        rect.Height.ShouldBe(ObjectInfoPanel.DesignPictureHeight(in picture), 1f);

        // Under the rows and above the buttons: the Goto button's region sits below the picture's rect.
        var goto_ = tab.GetRegisteredRegions().First(r => r.Result is HitResult.ButtonHit { Action: "ObjectInfoGoto" });
        (rect.Y + rect.Height).ShouldBeLessThanOrEqualTo(goto_.Y);
        rect.Y.ShouldBeGreaterThan(goto_.Y - ObjectInfoPanel.DesignHeight(
            new ObjectInfoPanel.PanelDisplayOptions(ShowAltAz: true, ShowRiseSet: true, Picture: picture),
            new ObjectInfoPanel.PanelActions(Goto: () => { })));

        // The credit is a link to the Commons file page, and it sits under the picture.
        var credit = tab.GetRegisteredRegions()
            .Where(r => r.Result is HitResult.LinkHit { Url: var url } && url == picture.FilePageUrl)
            .ShouldHaveSingleItem();
        credit.Y.ShouldBeGreaterThanOrEqualTo(rect.Y + rect.Height - 1f);
    }

    [Fact]
    public async Task TheAtlasPanelLinksToTheVerifiedArticle()
    {
        // Raised 2026-09-18: the panel had the article's picture and credit but no way to the article itself.
        // A first cut put the link on the title row, where a dim word read as a label; it is a link row now.
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var andromeda).ShouldBeTrue();
        db.TryGetArticle(andromeda.Index, out var article).ShouldBeTrue();

        var now = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(45.0, -75.0, now);
        var info = SkyMapInfoPanelData.FromCatalogObject(andromeda, 45.0, -75.0, now, site, null);

        using var renderer = new RgbaImageRenderer(900, 900);
        var (tab, planner, clock, content) = Atlas(db, renderer, info, now);
        tab.Render(planner, content, clock);

        // One link, to the VERIFIED article and never a designation guess, with the hand pointer, under the
        // picture's credit and above the buttons.
        var link = tab.GetRegisteredRegions()
            .Where(r => r.Result is HitResult.LinkHit { Url: var url } && url == article.Url)
            .ShouldHaveSingleItem();
        article.Url.ShouldStartWith("https://en.wikipedia.org/wiki/");
        link.Cursor.ShouldBe(CursorKind.Pointer);

        var goto_ = tab.GetRegisteredRegions().First(r => r.Result is HitResult.ButtonHit { Action: "ObjectInfoGoto" });
        (link.Y + link.Height).ShouldBeLessThanOrEqualTo(goto_.Y);
        if (article.Image is { } picture)
        {
            var credit = tab.GetRegisteredRegions()
                .First(r => r.Result is HitResult.LinkHit { Url: var url } && url == picture.FilePageUrl);
            link.Y.ShouldBeGreaterThanOrEqualTo(credit.Y + credit.Height);
        }
    }

    /// <summary>The atlas with M31 selected and its picture opened large by a press on the thumbnail.</summary>
    private sealed record ExpandedAtlas(
        PictureCapturingTab Tab, PlannerState Planner, ITimeProvider Clock, RectF32 Content,
        ObjectArticleImage Picture, RectF32 Thumbnail, InputRouter Router);

    private static async Task<ExpandedAtlas> ExpandedAtlasAsync(RgbaImageRenderer renderer, float dpiScale)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M31", out var andromeda).ShouldBeTrue();
        db.TryGetArticle(andromeda.Index, out var article).ShouldBeTrue();
        var picture = article.Image.ShouldNotBeNull();

        var now = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(45.0, -75.0, now);
        var info = SkyMapInfoPanelData.FromCatalogObject(andromeda, 45.0, -75.0, now, site, null);

        var (tab, planner, clock, content) = Atlas(db, renderer, info, now);
        tab.DpiScale = dpiScale;
        tab.Render(planner, content, clock);

        // Press the thumbnail, which is where the panel asked for the picture to be drawn.
        var thumbnail = tab.Pictures.ShouldHaveSingleItem().Rect;
        var cx = thumbnail.X + (thumbnail.Width / 2f);
        var cy = thumbnail.Y + (thumbnail.Height / 2f);
        // Through a router, as a real press arrives: the router is what consumes a press on a painted
        // region, and a press the tab handles itself starts a pan instead.
        var router = new InputRouter(tab.Ui, new BackgroundTaskTracker(), () => { })
        {
            Widgets = () => [tab],
            // What a host wires: the routing that is the tab's own, which is where its key handling lives.
            Unhandled = tab.HandleInput,
        };
        router.Handle(new InputEvent.MouseDown(cx, cy)).ShouldBeTrue();
        tab.State.PictureExpanded.ShouldBeTrue();

        tab.Pictures.Clear();
        tab.Render(planner, content, clock);
        return new ExpandedAtlas(tab, planner, clock, content, picture, thumbnail, router);
    }

    [Fact]
    public async Task ClickingThePictureOpensItLargeAndEscapeClosesIt()
    {
        using var renderer = new RgbaImageRenderer(900, 900);
        var atlas = await ExpandedAtlasAsync(renderer, dpiScale: 1f);
        var (tab, content, thumbnail) = (atlas.Tab, atlas.Content, atlas.Thumbnail);

        // Two draws now: the panel's thumbnail and the large view, which is much bigger and therefore asks
        // Wikimedia for a wider standard width.
        tab.Pictures.Count.ShouldBe(2);
        var large = tab.Pictures[^1].Rect;
        large.Width.ShouldBeGreaterThan(thumbnail.Width * 2f);
        ObjectArticleImage.StandardWidthFor((int)large.Width)
            .ShouldBeGreaterThan(ObjectArticleImage.StandardWidthFor((int)thumbnail.Width));
        large.X.ShouldBeGreaterThanOrEqualTo(content.X);
        (large.X + large.Width).ShouldBeLessThanOrEqualTo(content.X + content.Width);

        atlas.Router.Handle(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue("Escape retires the large picture first");
        tab.State.PictureExpanded.ShouldBeFalse();
        tab.State.Search.InfoPanel.ShouldNotBeNull("closing the picture must not close the panel it came from");
    }

    [Fact]
    public async Task TheLargeViewIsTheRectItWasPlacedInOnAHighDpiScreen()
    {
        // 1.5 is the desktop this shipped on, where the picture row was stated in PIXELS to a tree
        // arranged at design scale: the slot came out 1.5x too tall, so the picture sat below the centre
        // of the screen under a black band, and the credit row landed off the bottom of the window.
        using var renderer = new RgbaImageRenderer(1350, 1350);
        var atlas = await ExpandedAtlasAsync(renderer, dpiScale: 1.5f);
        var expected = ObjectInfoPanel.LargePictureRect(atlas.Content, atlas.Picture, 1.5f);

        var large = atlas.Tab.Pictures[^1].Rect;
        large.Y.ShouldBe(expected.Y, 1f);
        large.Height.ShouldBe(expected.Height, 1f, "the slot must be the rect the picture was placed in, at every DPI");

        // And the credit reads directly under it, on screen.
        var creditY = large.Y + large.Height + (ObjectInfoPanel.DesignRowHeight * 1.5f / 2f);
        creditY.ShouldBeLessThan(atlas.Content.Y + atlas.Content.Height);
        atlas.Tab.HitTest(large.X + (large.Width / 2f), creditY)
            .ShouldBeOfType<HitResult.LinkHit>().Url.ShouldBe(atlas.Picture.FilePageUrl);
    }

    [Fact]
    public async Task TheLargeViewCoversTheLayerPalette()
    {
        // The large view is drawn LAST in the frame. It used to be drawn inside the info panel, which the
        // selection marker, a comet's path and the layer palette all follow, so each painted over the
        // scrim -- and the palette's rows, registered after the scrim's, took the press meant to close it.
        using var renderer = new RgbaImageRenderer(900, 900);
        var atlas = await ExpandedAtlasAsync(renderer, dpiScale: 1f);
        var palette = atlas.Tab.State.LayerPalette.PanelRect;
        palette.Width.ShouldBeGreaterThan(0f, "the palette is on screen in this test, or it proves nothing");

        atlas.Tab.HitTest(palette.X + (palette.Width / 2f), palette.Y + (palette.Height / 2f))
            .ShouldBeOfType<HitResult.ButtonHit>().Action
            .ShouldBe("ObjectInfoPictureClose", "a press on a palette row under the scrim closes the picture, it does not toggle a layer");
    }

    [Fact]
    public async Task OpeningTheSearchRetiresTheLargeView()
    {
        // The search window owns the screen while it is open; a picture drawn last would cover it.
        using var renderer = new RgbaImageRenderer(900, 900);
        var atlas = await ExpandedAtlasAsync(renderer, dpiScale: 1f);

        atlas.Tab.State.Search.IsOpen = true;
        atlas.Tab.Pictures.Clear();
        atlas.Tab.Render(atlas.Planner, atlas.Content, atlas.Clock);

        atlas.Tab.State.PictureExpanded.ShouldBeFalse();
        // The panel itself stays painted under the modal, as it always has; only the large view goes.
        atlas.Tab.Pictures.ShouldHaveSingleItem().Rect.ShouldBe(atlas.Thumbnail);
    }

    [Fact]
    public async Task APositionWithNoCatalogueIndexHasNoPictureSection()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero);
        var info = SkyMapInfoPanelData.FromPosition("Somewhere", 5.0, 20.0, 45.0, -75.0, now, default);

        using var renderer = new RgbaImageRenderer(900, 900);
        var (tab, planner, clock, content) = Atlas(db, renderer, info, now);
        tab.Render(planner, content, clock);

        tab.Pictures.ShouldBeEmpty();
        tab.GetRegisteredRegions().ShouldNotContain(r => r.Result is HitResult.LinkHit);
    }
}

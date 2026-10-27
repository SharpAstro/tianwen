using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A left tap inside a DETECTED star's footprint selects that star (#708), through a nearest-centroid
/// resolver measured in image pixels off the star's own HFD: the same image pixel answers the same star
/// at every zoom, and nothing about the answer depends on the overlay's magnitude limit (#709).
/// </summary>
[Collection("Astrometry")]
public class ViewerStarSelectionTests
{
    private const uint WindowW = 900;
    private const uint WindowH = 700;
    private const int ImageW = 600;
    private const int ImageH = 400;
    private const double ScaleDeg = 2.0 / 3600.0;

    private static readonly ImagedStar StarA = StarAt(320f, 180f, hfd: 6f);
    private static readonly ImagedStar StarB = StarAt(100f, 300f, hfd: 4f);

    private static ImagedStar StarAt(float x, float y, float hfd)
        => new(HFD: hfd, StarFWHM: hfd, SNR: 100f, Flux: 1000f, XCentroid: x, YCentroid: y, Ellipticity: 0f);

    private sealed class StarViewer : ImageRendererBase<RgbaImage>
    {
        public StarViewer(RgbaImageRenderer renderer) : base(renderer)
        {
            Width = renderer.Width;
            Height = renderer.Height;
            DpiScale = 1f;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels)
        { }

        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
            ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness)
            => Ellipses.Add((cx, cy, semiMajor));

        /// <summary>Every ellipse the last frames drew: centre and semi-major axis.</summary>
        public List<(float Cx, float Cy, float R)> Ellipses { get; } = [];

        protected override void FillEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color)
        { }

        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
            RGBAColor32 color, float thickness) { }

        protected override void OnResize(uint width, uint height) { }

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel, int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) { }

        protected override HistogramDisplay? GetHistogramDisplay() => null;

        /// <summary>Where an image coordinate (centroid frame) is on screen right now.</summary>
        public (float X, float Y) ScreenOf(float imageX, float imageY)
            => (Placement.OffsetX + ((imageX + 0.5f) * Placement.Scale),
                Placement.OffsetY + ((imageY + 0.5f) * Placement.Scale));
    }

    private static WCS CentredOn(double raHours, double decDeg) => new(raHours, decDeg)
    {
        CRPix1 = ImageW / 2.0,
        CRPix2 = ImageH / 2.0,
        CD1_1 = -ScaleDeg,
        CD1_2 = 0.0,
        CD2_1 = 0.0,
        CD2_2 = -ScaleDeg,
    };

    private static Image SyntheticFrame()
    {
        var plane = new float[ImageH, ImageW];
        return new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero,
                FrameType.Light, "", 0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN,
                SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN));
    }

    private static async Task<(StarViewer Viewer, ViewerState State, AstroImageDocument Document)> NewViewerAsync(
        RgbaImageRenderer renderer, bool withCatalogue, bool starOverlay, params ImagedStar[] stars)
    {
        var ct = TestContext.Current.CancellationToken;
        var viewer = new StarViewer(renderer);
        double ra = 13.5, dec = 47.0;
        if (withCatalogue)
        {
            var db = await SharedCatalogDB.InitAsync(ct);
            db.TryLookupByIndex(CatalogIndex.NGC5194, out var obj).ShouldBeTrue();
            viewer.CelestialObjectDB = new DotNext.Threading.AsyncLazy<ICelestialObjectDB>(db);
            ra = obj.RA;
            dec = obj.Dec;
        }

        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

        var document = await AstroImageDocument.AdoptImageAsync(
            SyntheticFrame(), DebayerAlgorithm.None, CentredOn(ra, dec),
            filePath: "synthetic.fits", cancellationToken: ct);
        document.RecordStars(new StarList(new ConcurrentBag<ImagedStar>(stars)));

        var state = new ViewerState
        {
            ShowFileList = false,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            ZoomToFit = false,
            Zoom = 1f,
            ShowStarOverlay = starOverlay,
            ShowOverlays = withCatalogue,
        };

        return (viewer, state, document);
    }

    private static SkyMapInfoPanelData Selected()
        => SkyMapInfoPanelData.FromPosition(
            "Lagoon Nebula", 18.06, -24.38, double.NaN, double.NaN, DateTimeOffset.UnixEpoch, default);

    private static void TapAt(StarViewer viewer, float x, float y)
        => UiRouting.Route(viewer, new InputEvent.MouseDown(x, y), new InputEvent.MouseUp(x, y));

    [Fact]
    public void TheResolverAnswersTheNearestCentroidWithinItsOwnFootprint()
    {
        var list = new StarList(new ConcurrentBag<ImagedStar>([StarA, StarB]));

        list.NearestAt(320.0, 180.0).ShouldBe(StarA);
        list.NearestAt(322.0, 181.0).ShouldBe(StarA, "inside the radius HFD/2 = 3");
        list.NearestAt(324.0, 180.0).ShouldBeNull("outside the footprint");
        list.NearestAt(101.5, 300.0).ShouldBe(StarB, "a tight star keeps the small image-pixel floor");
        list.NearestAt(0.0, 0.0).ShouldBeNull();
        StarList.Empty.NearestAt(0.0, 0.0).ShouldBeNull();
    }

    [Fact]
    public void TheNearestOfTwoOverlappingFootprintsWins()
    {
        var wide = StarAt(50f, 50f, hfd: 20f);
        var small = StarAt(54f, 50f, hfd: 4f);
        var list = new StarList(new ConcurrentBag<ImagedStar>([wide, small]));

        list.NearestAt(53.0, 50.0).ShouldBe(small);
        list.NearestAt(48.0, 50.0).ShouldBe(wide);
    }

    [Fact]
    public async Task ATapInsideAStarsFootprintSelectsIt()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, true, StarA, StarB);

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(StarA.XCentroid, StarA.YCentroid);
        TapAt(viewer, x, y);

        state.SelectedStar.ShouldBe(StarA);
        state.SelectedObject.ShouldBeNull();
        state.StatusMessage.ShouldBe("Selected star at 320.0, 180.0");
    }

    [Fact]
    public async Task TheSelectedStarIsRingedAtItsCentroidWhateverTheOverlay()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, true, StarA);

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(StarA.XCentroid, StarA.YCentroid);
        TapAt(viewer, x, y);

        state.ShowStarOverlay = false;
        viewer.Ellipses.Clear();
        viewer.Render(document, state);

        // Toolbar icons draw ellipses of their own; the selection ring is the 9 and 12 px pair.
        var rings = viewer.Ellipses.FindAll(e => e.R is 9f or 12f);
        rings.Count.ShouldBe(2, "the selection ring's own pair, and no overlay marker");
        foreach (var ring in rings)
        {
            ring.Cx.ShouldBe(x, 0.01f);
            ring.Cy.ShouldBe(y, 0.01f);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(3f)]
    public async Task TheSameImagePixelAnswersTheSameStarAtAnyZoom(float zoom)
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, true, StarA, StarB);
        state.Zoom = zoom;

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(322f, 181f);
        TapAt(viewer, x, y);

        state.SelectedStar.ShouldBe(StarA, $"image point (322, 181) at zoom {zoom}");

        var (farX, farY) = viewer.ScreenOf(326f, 180f);
        TapAt(viewer, farX, farY);
        state.SelectedStar.ShouldBeNull("four pixels from the centroid is outside a 3 px footprint at any zoom");
    }

    [Fact]
    public async Task WithTheStarOverlayOffNoStarIsSelected()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, false, StarA);

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(StarA.XCentroid, StarA.YCentroid);
        TapAt(viewer, x, y);

        state.SelectedStar.ShouldBeNull("what is not drawn is not clickable");
    }

    [Fact]
    public async Task ADrawnMarkerStillWinsOverAStar()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        // A star at the frame's centre, which is where M51's marker is drawn.
        var onTheGalaxy = StarAt(ImageW / 2f, ImageH / 2f, hfd: 6f);
        var (viewer, state, document) = await NewViewerAsync(renderer, true, true, onTheGalaxy);

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(onTheGalaxy.XCentroid, onTheGalaxy.YCentroid);
        TapAt(viewer, x, y);

        state.SelectedObject.ShouldNotBeNull("the marker you can see is the one that is clicked")
            .Index.ShouldBe(CatalogIndex.NGC5194);
        state.SelectedStar.ShouldBeNull();
    }

    /// <summary>
    /// The hover wash and the click are one precedence (#708): over a detected star that sits on a catalogue
    /// object with no marker drawn there, a click takes the star, so the wash names no catalogue object. With the
    /// star overlay off the very same point washes the object, which is what makes the first half mean something.
    /// </summary>
    [Fact]
    public async Task OverADetectedStarOnACatalogueObjectTheWashAndTheClickAgree()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var onTheGalaxy = StarAt(ImageW / 2f, ImageH / 2f, hfd: 6f);
        var (viewer, state, document) = await NewViewerAsync(renderer, true, true, onTheGalaxy);

        // Rendered with the overlay off so no marker or label is drawn at the galaxy; the catalogue search alone
        // answers for it once the overlay is on again.
        state.ShowOverlays = false;
        viewer.Render(document, state);
        state.ShowOverlays = true;
        var (x, y) = viewer.ScreenOf(onTheGalaxy.XCentroid, onTheGalaxy.YCentroid);
        var (farX, farY) = viewer.ScreenOf(500f, 50f);

        state.ShowStarOverlay = false;
        viewer.HandleInput(new InputEvent.MouseMove(x, y));
        state.HoverObject.ShouldNotBeNull("without the star overlay the catalogue object is what is under the pointer")
            .Index.ShouldBe(CatalogIndex.NGC5194);

        viewer.HandleInput(new InputEvent.MouseMove(farX, farY));
        state.HoverObject.ShouldBeNull();

        state.ShowStarOverlay = true;
        viewer.HandleInput(new InputEvent.MouseMove(x, y));
        state.HoverObject.ShouldBeNull("a click here takes the star, so no catalogue object is washed");

        TapAt(viewer, x, y);
        state.SelectedStar.ShouldBe(onTheGalaxy);
        state.SelectedObject.ShouldBeNull();
    }

    [Fact]
    public async Task ASelectedStarAndASelectedObjectExcludeEachOther()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, true, StarA);

        state.SelectedObject = Selected();
        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(StarA.XCentroid, StarA.YCentroid);
        TapAt(viewer, x, y);

        state.SelectedStar.ShouldBe(StarA);
        state.SelectedObject.ShouldBeNull("selecting a star clears the object");

        state.SelectedObject = Selected();
        state.SelectedStar.ShouldBeNull("selecting an object clears the star");
    }

    [Fact]
    public async Task EscapeAndAnEmptySkyTapClearTheStar()
    {
        using var renderer = new RgbaImageRenderer(WindowW, WindowH);
        var (viewer, state, document) = await NewViewerAsync(renderer, false, true, StarA);

        viewer.Render(document, state);
        var (x, y) = viewer.ScreenOf(StarA.XCentroid, StarA.YCentroid);

        TapAt(viewer, x, y);
        state.SelectedStar.ShouldNotBeNull();
        var (emptyX, emptyY) = viewer.ScreenOf(500f, 50f);
        TapAt(viewer, emptyX, emptyY);
        state.SelectedStar.ShouldBeNull("a tap on empty sky clears it");
        state.StatusMessage.ShouldBeNull();

        TapAt(viewer, x, y);
        state.SelectedStar.ShouldNotBeNull();
        UiRouting.RouteKey(viewer, InputKey.Escape);
        state.SelectedStar.ShouldBeNull("Escape dismisses it before it means quit");
    }
}

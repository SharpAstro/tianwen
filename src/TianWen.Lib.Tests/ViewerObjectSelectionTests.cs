using System;
using System.Collections.Generic;
using System.Threading;
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

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A left click on the picture selects the catalogued object it landed on, and the selection
    /// persists.
    /// </summary>
    /// <remarks>
    /// <para><b>The resolver existed for weeks with nowhere to put its answer.</b>
    /// <c>ImageRendererBase.FindObjectAt</c> could already say which catalogued object is under a
    /// pixel, and it was wired to right-click alone: the viewer had no notion of a SELECTED object, so
    /// a left click had nothing to write to and nothing to draw. What shipped here is that missing
    /// half, and the user chose click over hover for it.</para>
    /// <para><b>Driven against the REAL catalogue, and the frame is built around whatever it holds.</b>
    /// The WCS is centred on the object's own catalogued coordinates, read out of the database in the
    /// test rather than written down here -- so the case exercises the plumbing (press, tap, resolve,
    /// store) instead of asserting that this file remembers M42's position, and it cannot rot when a
    /// catalogue refresh moves a coordinate by an arcsecond.</para>
    /// </remarks>
    [Collection("Astrometry")]
    public class ViewerObjectSelectionTests
    {
        private const uint WindowW = 900;
        private const uint WindowH = 700;
        private const int ImageW = 600;
        private const int ImageH = 400;

        /// <summary>Two arcseconds per pixel, so the frame spans about 0.33 by 0.22 degrees.</summary>
        private const double ScaleDeg = 2.0 / 3600.0;

        private sealed class SelectionViewer : ImageRendererBase<RgbaImage>
        {
            public SelectionViewer(RgbaImageRenderer renderer) : base(renderer)
            {
                Width = renderer.Width;
                Height = renderer.Height;
                DpiScale = 1f;
                FontPath = FontResolver.ResolveSystemFont();
            }

            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels) { }

            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float rotationRad, RGBAColor32 color, float thickness)
            {
                Ellipses++;
                DrawnEllipses.Add((semiMajor, semiMinor, rotationRad));
            }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) { }

            protected override void OnResize(uint width, uint height) { }

            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int width, int height) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;

            public RectF32 ImageArea => ImageAreaRect;

            /// <summary>
            /// How many ellipses this frame drew. The selection highlight is a PAIR of them, which is
            /// what makes "is it ringed" answerable without a pixel readback.
            /// </summary>
            public int Ellipses { get; set; }

            /// <summary>
            /// Every ellipse this frame drew, in draw order. The COUNT answers "is it ringed"; the
            /// geometry is what answers "is it ringed with the object's own shape", which a count
            /// cannot tell from a circle.
            /// </summary>
            public List<(float SemiMajor, float SemiMinor, float AngleRad)> DrawnEllipses { get; } = [];
        }

        /// <summary>A frame whose reference pixel is at its own centre, pointed at the given sky position.</summary>
        private static WCS CentredOn(double raHours, double decDeg) => new WCS(raHours, decDeg)
        {
            CRPix1 = ImageW / 2.0,
            CRPix2 = ImageH / 2.0,
            CD1_1 = -ScaleDeg,
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = -ScaleDeg,
        };

        /// <summary>
        /// A blank frame, defaulting to the UnixEpoch/no-site header the plumbing cases use (which
        /// <see cref="SkyAtlasLink.IsKnownCaptureTime"/> and <see cref="FrameSite"/> both read as
        /// UNKNOWN). Site/time are overridable so the site-dependent selection cases can build a
        /// header that answers both.
        /// </summary>
        private static Image SyntheticFrame(
            DateTimeOffset? exposureStart = null, float latitude = float.NaN, float longitude = float.NaN)
        {
            var plane = new float[ImageH, ImageW];
            for (var y = 0; y < ImageH; y++)
            {
                for (var x = 0; x < ImageW; x++)
                {
                    plane[y, x] = 1000f + (y * ImageW) + x;
                }
            }

            return new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
                new ImageMeta("synth", exposureStart ?? DateTimeOffset.UnixEpoch, TimeSpan.Zero,
                    FrameType.Light, "", 0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN,
                    SensorType.Monochrome, 0, 0, RowOrder.TopDown, latitude, longitude));
        }

        /// <summary>
        /// A viewer over a frame centred on <paramref name="index"/>'s own catalogued position, with the
        /// catalogue already loaded.
        /// </summary>
        /// <remarks>
        /// The <c>AsyncLazy</c> is built from an ALREADY-COMPUTED value, so its peek answers on the
        /// first frame. Built from the lambda form it would answer null until something awaited it,
        /// which is correct in production (the build stays off the render thread) and would make every
        /// case here silently resolve nothing.
        /// </remarks>
        private static async Task<(SelectionViewer Viewer, ViewerState State, AstroImageDocument Document,
            CelestialObject Object)> NewViewerOnAsync(
            RgbaImageRenderer renderer, CatalogIndex index, CancellationToken ct,
            double pointOffsetDeg = 0.0, DateTimeOffset? exposureStart = null,
            float latitude = float.NaN, float longitude = float.NaN)
        {
            var db = await SharedCatalogDB.InitAsync(ct);
            db.TryLookupByIndex(index, out var obj).ShouldBeTrue($"the catalogue has to hold {index}");

            var viewer = new SelectionViewer(renderer)
            {
                CelestialObjectDB = new DotNext.Threading.AsyncLazy<ICelestialObjectDB>(db),
            };
            viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, ImageW, ImageH);

            // pointOffsetDeg aims the FRAME away from the object, in RA, so the object lands OUTSIDE
            // the sensor while staying near it -- which is the only way to put a clickable marker
            // beside the picture rather than in it.
            var raOffsetHours = pointOffsetDeg / 15.0 / Math.Cos(obj.Dec * Math.PI / 180.0);

            var document = await AstroImageDocument.AdoptImageAsync(
                SyntheticFrame(exposureStart, latitude, longitude), DebayerAlgorithm.None,
                CentredOn(obj.RA + raOffsetHours, obj.Dec),
                filePath: "synthetic.fits", cancellationToken: ct);

            var state = new ViewerState
            {
                ShowFileList = false,
                ShowInfoPanel = false,
                ShowHistogram = false,
                StretchMode = StretchMode.None,
                ZoomToFit = false,
                Zoom = 1f,
            };

            return (viewer, state, document, obj);
        }

        /// <summary>Presses and releases at the same point: a tap, not a drag.</summary>
        private static void TapAt(SelectionViewer viewer, float x, float y)
        {
            viewer.HandleInput(new InputEvent.MouseDown(x, y));
            viewer.HandleInput(new InputEvent.MouseUp(x, y));
        }

        /// <summary>Where the frame's reference pixel is on screen -- i.e. where the object is.</summary>
        private static (float X, float Y) ObjectOnScreen(SelectionViewer viewer, ViewerState state)
        {
            var area = viewer.ImageArea;
            return (area.X + (area.Width / 2f), area.Y + (area.Height / 2f));
        }

        /// <summary>
        /// <b>The feature, in one case.</b> A tap on the object selects it, by name.
        /// </summary>
        /// <remarks>
        /// <b>M51 rather than M42, and the reason is measured.</b> The resolver answers NEAREST
        /// CENTRE, and at the middle of the Orion Nebula three catalogued objects sit within 0.2
        /// arcseconds of each other (NGC 1976 itself, then HIP 26221 = HD 37022 = theta1 Ori C), while
        /// a click quantises to a whole image pixel -- 2 arcseconds at this plate scale. Which of the
        /// three wins there is decided by the rounding, so no assertion about it could mean anything.
        /// M51's nearest other drawn object is its own companion NGC 5195 at 265 arcseconds, an order
        /// of magnitude outside the click tolerance, so the answer is the object and not the density.
        /// </remarks>
        [Fact]
        public async Task ATapOnAnObjectSelectsIt()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, obj) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            var selection = state.SelectedObject.ShouldNotBeNull();
            selection.Canonical.ShouldBe("NGC 5194");
            selection.Canonical.ShouldBe(obj.Index.ToCanonical());
            selection.RA.ShouldBe(obj.RA, 1e-9);
            selection.Dec.ShouldBe(obj.Dec, 1e-9);
            selection.Name.ShouldNotBeNullOrEmpty("the panel needs something to print");
        }

        /// <summary>
        /// A DRAG does not select. This is the case that would be broken by resolving on the press:
        /// a press on the picture is the start of a pan, so every drag would leave whatever was under
        /// the finger selected.
        /// </summary>
        [Fact]
        public async Task ADragDoesNotSelect()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);

            viewer.HandleInput(new InputEvent.MouseDown(x, y));
            viewer.HandleInput(new InputEvent.MouseMove(x + 60f, y + 40f));
            viewer.HandleInput(new InputEvent.MouseUp(x + 60f, y + 40f));

            state.SelectedObject.ShouldBeNull("a drag is a pan, not a question about what is under it");
        }

        /// <summary>
        /// A tap on empty sky clears the selection -- the dismissal that needs no key, and the reason
        /// the resolver's "nothing there" answer is not simply ignored.
        /// </summary>
        [Fact]
        public async Task ATapOnEmptySkyClearsTheSelection()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);
            state.SelectedObject.ShouldNotBeNull();

            // The frame's own corner: a tenth of a degree from the centre at this plate scale, which is
            // several times the click tolerance (2 percent of the field).
            var area = viewer.ImageArea;
            TapAt(viewer, area.X + (area.Width / 2f) - 250f, area.Y + (area.Height / 2f) - 150f);

            state.SelectedObject.ShouldBeNull();
        }

        /// <summary>
        /// The selection survives repainting, which is what separates it from a hover: it is stored
        /// state, not something re-derived from where the pointer happens to be.
        /// </summary>
        [Fact]
        public async Task TheSelectionSurvivesARepaint()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);
            var selected = state.SelectedObject.ShouldNotBeNull();

            // Three frames, and a pointer excursion in between: nothing here should touch it.
            viewer.HandleInput(new InputEvent.MouseMove(x + 200f, y + 100f));
            viewer.Render(document, state);
            viewer.Render(document, state);

            state.SelectedObject.ShouldBe(selected);
        }

        /// <summary>
        /// <b>An object BESIDE the picture can be selected.</b> Reported live: "i also couldn't click
        /// on any object outside the frame".
        /// </summary>
        /// <remarks>
        /// <para>The overlay draws objects the gather found beside the frame as well as in it -- that
        /// is what the in-frame label tier is about -- and a marker you can see is one you expect to be
        /// able to click. But the selection resolved its position through
        /// <c>UpdateCursorFromScreenPosition</c>, the pixel READOUT's resolver, which nulls both cursor
        /// fields off-raster because there is no pixel there to report. So the click got nothing and
        /// CLEARED, which is indistinguishable from clicking empty sky.</para>
        /// <para>A sky position needs only the WCS. The frame here is aimed a fifth of a degree away
        /// from the object: at 2 arcseconds per pixel that is 360 image pixels, which clears the sensor half-width (300) while staying inside the pane half-width (450) at 1:1 -- a marker outside the picture AND on screen, which is the only place this bug lives.
        /// the raster with room to spare -- asserted below rather than asserted-by-arithmetic, since
        /// the whole point is that the pixel is off the sensor.</para>
        /// </remarks>
        [Fact]
        public async Task AnObjectBesideThePictureCanBeSelected()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, obj) = await NewViewerOnAsync(
                renderer, CatalogIndex.NGC5194, ct, pointOffsetDeg: 0.20);

            viewer.Render(document, state);

            // Where the object projects, in the frame's own 1-based pixels -- outside the raster.
            var wcs = document.Wcs.ShouldNotBeNull();
            var px = wcs.SkyToPixel(obj.RA, obj.Dec).ShouldNotBeNull();
            (px.X < 1 || px.X > ImageW).ShouldBeTrue(
                $"the object has to be off the sensor for this to test anything (x={px.X:F1})");

            // The same screen mapping the renderer's placement uses: origin, then scale, 0-based.
            var area = viewer.ImageArea;
            var originX = area.X + ((area.Width - (ImageW * state.Zoom)) / 2f) + state.PanOffset.X;
            var originY = area.Y + ((area.Height - (ImageH * state.Zoom)) / 2f) + state.PanOffset.Y;
            var sx = originX + ((float)px.X - 1f) * state.Zoom;
            var sy = originY + ((float)px.Y - 1f) * state.Zoom;

            (sx >= area.X && sx < area.X + area.Width).ShouldBeTrue("the marker must be on screen");

            TapAt(viewer, sx, sy);

            var selection = state.SelectedObject.ShouldNotBeNull();
            selection.Canonical.ShouldBe("NGC 5194");
        }

        /// <summary>
        /// A click FAR outside the frame names nothing, and that bound is not about the projection's
        /// maths.
        /// </summary>
        /// <remarks>
        /// <para>A gnomonic deprojection is exact and single-valued anywhere short of 90 degrees, so
        /// nothing breaks arithmetically out here. What does not hold is the SOLUTION: a plate solve is
        /// fitted to the stars ON the sensor, and asking it about a point degrees beyond one answers
        /// with a confidence it never earned. Zoomed to a hundredth, one screen pixel is 0.055 degrees,
        /// so a press a few hundred pixels off the picture is well past the five-degree bound.</para>
        /// <para><b>Asserted through the context MENU, not through the selection</b>, and the first
        /// draft of this case is why. Asserting "nothing gets selected out there" passes with the bound
        /// raised to 89 degrees -- because there is no catalogued object at that position either way,
        /// so it was testing the emptiness of that patch of sky rather than the bound. The menu answers
        /// the question directly: it offers a sky position when it has one and refuses to open at all
        /// when it does not, so the two clicks below differ in outcome for exactly one reason.</para>
        /// </remarks>
        [Fact]
        public async Task AClickFarOutsideTheFrameClaimsNoSkyPosition()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            state.Zoom = 0.01f;
            viewer.Render(document, state);

            var area = viewer.ImageArea;
            var cx = area.X + (area.Width / 2f);
            var cy = area.Y + (area.Height / 2f);

            // Two screen pixels out is 0.11 degrees at this zoom: off the raster (the whole frame is
            // six pixels wide here) and well inside the bound, so the menu has a position to offer.
            viewer.TryOpenImageContextMenu(state, cx + 2f, cy).ShouldBeTrue(
                "just beside the picture there is still a sky position");
            state.ToolbarDropdown.Close();

            // Three hundred out is 16.7 degrees, past the bound.
            viewer.TryOpenImageContextMenu(state, cx + 300f, cy).ShouldBeFalse(
                "past the bound the frame's solution may not be asked, so there is nothing to offer");

            // And the selection agrees: a tap out there names nothing. Weaker than the two lines above
            // on its own -- see the remarks -- but it is the gesture the user actually makes.
            TapAt(viewer, cx, cy);
            state.SelectedObject.ShouldNotBeNull("the frame centre still resolves at any zoom");
            TapAt(viewer, cx + 300f, cy);
            state.SelectedObject.ShouldBeNull();
        }

        /// <summary>
        /// A click can only ever name something the overlay would have DRAWN -- which the resolver's
        /// own documentation had always claimed and, until this went looking, was not true.
        /// </summary>
        /// <remarks>
        /// <para><b>Found by writing the case above.</b> A tap on the middle of M42 answered "HH 1146",
        /// a Herbig-Haro object: <see cref="ObjectType.HerbigHaroObj"/> is in neither
        /// <see cref="OverlayEngine.IsExtendedObjectType"/> nor <see cref="OverlayEngine.IsStarType"/>,
        /// so the overlay filters it out deliberately -- the same way it filters a star-forming region
        /// -- while the resolver, which gathers from the same grid but applied no type gate, was happy
        /// to name it. Clicking a nebula and being told the name of something drawn nowhere is the
        /// defect; the fix is the overlay's own two predicates, asked from the same class.</para>
        /// <para>M42 is the right fixture for this and the wrong one for the case above, for one
        /// reason: its core holds five HH objects inside 12 arcseconds. It is the densest field of
        /// undrawn types in the catalogue, so it is where a missing gate shows.</para>
        /// </remarks>
        [Fact]
        public async Task ATapNeverNamesATypeTheOverlayFiltersOut()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC1976, ct);
            var db = await SharedCatalogDB.InitAsync(ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            var selection = state.SelectedObject.ShouldNotBeNull();
            selection.Canonical.StartsWith("HH ", StringComparison.Ordinal).ShouldBeFalse(
                "a Herbig-Haro object is drawn nowhere, so a click may not name one");

            // Said as the rule rather than as a list of designations, so it holds for any object the
            // rounding happens to pick out of that core.
            db.TryLookupByIndex(
                CatalogIndex.NGC1976, out _).ShouldBeTrue();
            (selection.Canonical.StartsWith("NGC", StringComparison.Ordinal)
                || selection.Canonical.StartsWith("HIP", StringComparison.Ordinal)
                || selection.Canonical.StartsWith("HD", StringComparison.Ordinal))
                .ShouldBeTrue($"expected a drawn type, got {selection.Canonical}");
        }

        /// <summary>
        /// The highlight is drawn, and drawn with the OVERLAY OFF -- the resolver needs only a WCS and
        /// the catalogue, so an object can be selected at every rung of the context ladder, and a
        /// selection that draws nothing cannot be told from a click that missed.
        /// </summary>
        /// <remarks>
        /// Counted rather than read back from pixels because the ring's PRESENCE is the question; the
        /// pair is two ellipses, so the count rises by at least two over a frame with no selection.
        /// </remarks>
        [Fact]
        public async Task TheHighlightDrawsWithTheOverlayOff()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            state.ShowOverlays = false;
            state.ShowStarOverlay = false;

            viewer.Render(document, state);
            viewer.Ellipses = 0;
            viewer.Render(document, state);
            var withoutSelection = viewer.Ellipses;

            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            viewer.Ellipses = 0;
            viewer.Render(document, state);

            viewer.Ellipses.ShouldBe(withoutSelection + 2,
                "the selection ring is a pair of ellipses, and nothing else changed between the frames");
        }

        /// <summary>
        /// How many ellipses a frame draws with NOTHING selected. The picture draws some of its own
        /// (a detected-star marker, say), so the selection's pair has to be counted against that
        /// rather than assumed to be all of them.
        /// </summary>
        private static int EllipsesWithoutSelection(
            SelectionViewer viewer, AstroImageDocument document, ViewerState state,
            SkyMapInfoPanelData? restore = null)
        {
            var held = restore ?? state.SelectedObject;
            state.SelectedObject = null;
            viewer.DrawnEllipses.Clear();
            viewer.Render(document, state);
            var count = viewer.DrawnEllipses.Count;
            state.SelectedObject = held;
            return count;
        }

        /// <summary>
        /// The selection's own pair out of the frame's ellipses: exactly two more than the baseline,
        /// and the LAST two, since the highlight draws after the picture's own markers.
        /// </summary>
        private static ((float SemiMajor, float SemiMinor, float AngleRad) Inner,
            (float SemiMajor, float SemiMinor, float AngleRad) Outer)
            SelectionRings(SelectionViewer viewer, int baseline)
        {
            var drawn = viewer.DrawnEllipses;
            drawn.Count.ShouldBe(baseline + 2,
                "the selection ring is a pair, and nothing else changed between the two frames");
            return (drawn[^2], drawn[^1]);
        }

        /// <summary>
        /// An EXTENDED object is ringed by its own outline, not by a circle: both rings carry the
        /// catalogue's axis ratio, which is the whole difference between "something is selected here"
        /// and "this galaxy is selected".
        /// </summary>
        /// <remarks>
        /// <b>Checked against the catalogue's own axes rather than a literal.</b> A fixed expected
        /// ratio would rot the moment an OpenNGC refresh re-measures M51, and would not tell a marker
        /// that traces the shape from one that merely happens to be elliptical -- this reads the
        /// major/minor the database holds and asks the ring to match it.
        /// </remarks>
        [Fact]
        public async Task TheRingTracesAnExtendedObjectsOwnEllipse()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);
            var db = await SharedCatalogDB.InitAsync(ct);

            db.TryGetShape(CatalogIndex.NGC5194, out var shape)
                .ShouldBeTrue("the fixture depends on M51 carrying a catalogued shape");
            var catalogueRatio = (double)shape.MinorAxis / (double)shape.MajorAxis;
            catalogueRatio.ShouldBeLessThan(0.95,
                "an object whose axes are nearly equal could not tell an ellipse from a circle");

            viewer.Render(document, state);
            var baseline = EllipsesWithoutSelection(viewer, document, state);

            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            viewer.DrawnEllipses.Clear();
            viewer.Render(document, state);

            var (inner, outer) = SelectionRings(viewer, baseline);

            foreach (var (semiMajor, semiMinor, _) in new[] { inner, outer })
            {
                (semiMinor / semiMajor).ShouldBe((float)catalogueRatio, 0.01f,
                    "each ring carries the object's OWN axis ratio, not a circle's");
            }

            // The outer ring is a UNIFORM scale of the inner, which is what stops an edge-on galaxy
            // rounding off: a constant pixel offset would leave the two ratios apart.
            (outer.SemiMinor / outer.SemiMajor).ShouldBe(inner.SemiMinor / inner.SemiMajor, 0.001f,
                "the outer ring scales uniformly rather than gaining a constant number of pixels");
            outer.SemiMajor.ShouldBeGreaterThan(inner.SemiMajor, "and it sits outside the inner one");
            outer.AngleRad.ShouldBe(inner.AngleRad, 1e-6f, "both rings share the object's position angle");
            MathF.Abs(inner.AngleRad).ShouldBeGreaterThan(0.01f,
                "M51's catalogued position angle is not zero, so the ring is not axis-aligned");
        }

        /// <summary>
        /// A STAR keeps the circular ring even when the catalogue hands it a shape. Antares sits
        /// inside the rho Ophiuchi dark-cloud complex, so a cross-linked shape on a star is a real
        /// case, and it must not acquire a nebula's ellipse.
        /// </summary>
        /// <remarks>
        /// The selection is built directly rather than clicked for: the point is the CLASSIFIER, and
        /// pointing a synthetic frame at a star that happens to carry a stray shape would test the
        /// catalogue's current cross-links instead of the rule.
        /// </remarks>
        [Fact]
        public async Task AStarKeepsTheCircularRingEvenCarryingAShape()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, obj) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            // A star at the frame's centre, carrying an emphatically elongated shape.
            state.SelectedObject = SkyMapInfoPanelData.FromPosition(
                    "Antares-like", obj.RA, obj.Dec, double.NaN, double.NaN, DateTimeOffset.UnixEpoch, default)
                with
            {
                ObjType = ObjectType.Star,
                Shape = new CelestialObjectShape((Half)40f, (Half)4f, (Half)30f),
            };

            var baseline = EllipsesWithoutSelection(viewer, document, state, restore: state.SelectedObject);

            viewer.DrawnEllipses.Clear();
            viewer.Render(document, state);

            var (inner, outer) = SelectionRings(viewer, baseline);
            foreach (var (semiMajor, semiMinor, angle) in new[] { inner, outer })
            {
                semiMinor.ShouldBe(semiMajor, 1e-3f,
                    "a star's ring is a circle however elongated the shape hung off it is");
                angle.ShouldBe(0f, 1e-6f, "and a circle has no position angle to carry");
            }
        }

        // --- the floating panel's Alt/Az and rise/transit/set are baked in at the CAPTURE instant ---

        /// <summary>
        /// A frame with no site and no usable capture time carries no Alt/Az on its selection -- the
        /// "omit rather than lie" rule the shared panel is built around (<see cref="ObjectInfoPanel"/>),
        /// pinned here at the SOURCE of the value rather than only in the panel's own string-formatting
        /// cases.
        /// </summary>
        [Fact]
        public async Task WithNoSiteOrCaptureTimeTheSelectionCarriesNoAltAz()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            var selection = state.SelectedObject.ShouldNotBeNull();
            double.IsNaN(selection.AltDeg).ShouldBeTrue(
                "the header carries neither a site nor a usable capture time, so there is no honest Alt/Az to state");
        }

        /// <summary>
        /// With BOTH a site and a real capture time, the selection's Alt/Az is resolved AT THAT
        /// INSTANT -- never the reader's wall clock, which is the whole reason it is baked in once at
        /// the click rather than re-solved every frame the panel is open.
        /// </summary>
        /// <remarks>
        /// Checked against an independently-written altitude formula rather than a literal: a fixed
        /// expected number would drift the moment M51's catalogued position is refreshed, and would
        /// not tell a genuinely wrong Alt/Az from a stale one -- this instead re-derives what the
        /// answer OUGHT to be from the same site/time the fixture states, using maths this production
        /// code did not write.
        /// </remarks>
        [Fact]
        public async Task WithASiteAndACaptureTimeTheSelectionCarriesAltAzAtThatInstant()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);

            // A real night: 2026-06-16 22:00 UTC from a mid-northern site.
            var captured = new DateTimeOffset(2026, 6, 16, 22, 0, 0, TimeSpan.Zero);
            var (viewer, state, document, obj) = await NewViewerOnAsync(
                renderer, CatalogIndex.NGC5194, ct, exposureStart: captured, latitude: 45f, longitude: -75f);

            viewer.Render(document, state);
            var (x, y) = ObjectOnScreen(viewer, state);
            TapAt(viewer, x, y);

            var selection = state.SelectedObject.ShouldNotBeNull();
            double.IsNaN(selection.AltDeg).ShouldBeFalse("both the site and the capture time are known");
            selection.TransitTime.ShouldNotBeNull();

            var site = SiteContext.Create(45.0, -75.0, captured);
            var ha = (site.LST - obj.RA) * Math.PI / 12.0;
            var expectedAlt = Math.Asin(
                (site.SinLat * Math.Sin(obj.Dec * Math.PI / 180.0))
                + (site.CosLat * Math.Cos(obj.Dec * Math.PI / 180.0) * Math.Cos(ha))) * 180.0 / Math.PI;
            selection.AltDeg.ShouldBe(expectedAlt, 1e-6);
        }

        // --- the floating panel's own BLANK area must not fall through to the picture beneath it ---

        /// <summary>
        /// A press on the panel's blank body -- not a button, not the close X -- must not fall through
        /// to the picture underneath it. The panel floats bottom-left of the image area, well away
        /// from the object at the frame's centre, so an unswallowed press there resolves "nothing
        /// here" and CLEARS the selection -- indistinguishable from a click on empty sky.
        /// </summary>
        /// <remarks>
        /// Fixed together with the atlas's own copy of this panel (<c>SkyMapInfoPanelClickTests</c>),
        /// which had the identical gap: both panels share <see cref="ObjectInfoPanel"/>'s layout, and
        /// both drew their background as a plain fill with no <c>Clickable</c> region.
        /// </remarks>
        [Fact]
        public async Task APressOnThePanelsBlankAreaDoesNotClearTheSelection()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (ox, oy) = ObjectOnScreen(viewer, state);
            TapAt(viewer, ox, oy);
            var selected = state.SelectedObject.ShouldNotBeNull();

            // Render once more so the panel's background click region is registered for THIS frame --
            // a widget's clickables live only as long as the paint that produced them.
            viewer.Render(document, state);

            // The same geometry RenderSelectionPanel itself derives: no site/capture time in this
            // fixture, so the panel is Close + Atlas only, at ObjectInfoPanel's own computed height.
            var options = new ObjectInfoPanel.PanelDisplayOptions();
            var actions = new ObjectInfoPanel.PanelActions(Close: () => { }, OpenInAtlas: () => { });
            var ph = ObjectInfoPanel.DesignHeight(in options, in actions);

            var area = viewer.ImageArea;
            var px = area.X + 22f;
            // 40% of the panel height up from its bottom edge: clear of the close X at the top and
            // the button row at the bottom.
            var py = area.Y + area.Height - 12f - (ph * 0.4f);
            TapAt(viewer, px, py);

            state.SelectedObject.ShouldBe(selected,
                "the panel's own blank area must swallow the press, not hand it to the picture underneath");
        }

        /// <summary>The panel's own design height for this fixture: no site, so Close + Atlas only.</summary>
        private static float PanelDesignHeight()
        {
            var options = new ObjectInfoPanel.PanelDisplayOptions();
            var actions = new ObjectInfoPanel.PanelActions(Close: () => { }, OpenInAtlas: () => { });
            return ObjectInfoPanel.DesignHeight(in options, in actions);
        }

        /// <summary>
        /// A CHROMELESS host draws no floating panel. The docked section this replaced was suppressed
        /// in those hosts by their own <c>ShowInfoPanel: false</c>, and a live preview embedded in a
        /// session tab is the one place a panel over the picture is an intrusion rather than the point
        /// -- the same rule the status bar, the dropdowns and the tooltip already follow.
        /// </summary>
        [Fact]
        public async Task AChromelessHostDrawsNoFloatingPanel()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, WindowH);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var (ox, oy) = ObjectOnScreen(viewer, state);
            TapAt(viewer, ox, oy);
            state.SelectedObject.ShouldNotBeNull();

            state.HideChrome = true;
            viewer.Render(document, state);

            var ph = PanelDesignHeight();
            var area = viewer.ImageArea;
            var px = area.X + 22f;
            var py = MathF.Max(area.Y, area.Y + area.Height - ph - 12f) + (ph * 0.5f);

            (viewer.HitTestAndDispatch(px, py) is HitResult.ButtonHit { Action: "SelectionPanelBackground" })
                .ShouldBeFalse("a chromeless host paints no panel, so nothing there can claim a press");
        }

        /// <summary>
        /// In a pane SHORTER than the panel, the panel's top stops at the image area rather than
        /// climbing out of it. Nothing clips this panel, so it has to overflow somewhere: the clamp
        /// picks the button row off the bottom over the object's NAME off the top.
        /// </summary>
        [Fact]
        public async Task AShortPaneKeepsThePanelsTopInsideTheImageArea()
        {
            var ct = TestContext.Current.CancellationToken;
            using var renderer = new RgbaImageRenderer(WindowW, 220);
            var (viewer, state, document, _) = await NewViewerOnAsync(renderer, CatalogIndex.NGC5194, ct);

            viewer.Render(document, state);
            var ph = PanelDesignHeight();
            var area = viewer.ImageArea;
            area.Height.ShouldBeLessThan(ph + 12f,
                "the fixture only exercises the clamp while the pane is shorter than the panel");

            var (ox, oy) = ObjectOnScreen(viewer, state);
            TapAt(viewer, ox, oy);
            state.SelectedObject.ShouldNotBeNull();

            // Render again so this frame's regions are the ones being probed.
            viewer.Render(document, state);

            var px = area.X + 22f;
            (viewer.HitTestAndDispatch(px, area.Y + 4f) is HitResult.ButtonHit { Action: "SelectionPanelBackground" })
                .ShouldBeTrue("clamped to the top of the image area, the panel starts there");
            (viewer.HitTestAndDispatch(px, area.Y - 8f) is HitResult.ButtonHit { Action: "SelectionPanelBackground" })
                .ShouldBeFalse("and it must not reach above the image area into the toolbar");
        }
    }
}

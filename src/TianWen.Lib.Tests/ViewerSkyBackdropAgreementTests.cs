using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// With the sky backdrop on, a selection ring, the catalogue marker the map draws and the object's own
/// position through the frame's WCS are ONE point on screen (#1994), for an object past the frame's edge
/// on a solved frame whose WCS was moved by a crop.
/// </summary>
[Collection("Astrometry")]
public class ViewerSkyBackdropAgreementTests
{
    private const int FrameW = 1400;
    private const int FrameH = 900;
    private const double ArcsecPerPixel = 1.58;

    /// <summary>The autocrop's own shift from the full master, as the owner's M83 file carried it.</summary>
    private const int CropX = 48;
    private const int CropY = 105;

    private static double[,] SipTerm(int i, int j, double value)
    {
        var terms = new double[3, 3];
        terms[i, j] = value;
        return terms;
    }

    private sealed class MapWithObjectOverlay(RgbaImageRenderer renderer) : SkyMapTab<RgbaImage>(renderer)
    {
        /// <summary>Where the map PAINTED each catalogue marker in its last overlay pass, read off the draw call's own items.</summary>
        internal System.Collections.Generic.Dictionary<CatalogIndex, (float X, float Y)> DrawnMarkers { get; } = [];

        protected override void DrawOverlayMarkers(
            System.Collections.Generic.IReadOnlyList<TianWen.UI.Abstractions.Overlays.OverlayCandidate> candidates,
            System.Collections.Generic.IReadOnlyList<TianWen.UI.Abstractions.Overlays.OverlayItem> items,
            int candidateVersion, float arcminToPixels, double ppr, float cxView, float cyView,
            float dpiScale, float fovAlpha, bool dimBelowHorizon, SiteContext site)
        {
            DrawnMarkers.Clear();
            foreach (var item in items)
            {
                if ((uint)item.CandidateIndex < (uint)candidates.Count)
                {
                    DrawnMarkers[candidates[item.CandidateIndex].CatalogIndex] = (item.ScreenX, item.ScreenY);
                }
            }

            base.DrawOverlayMarkers(candidates, items, candidateVersion, arcminToPixels, ppr, cxView, cyView,
                dpiScale, fovAlpha, dimBelowHorizon, site);
        }

        protected override void RenderSkyMap(
            ICelestialObjectDB db, RectF32 contentRect,
            DateTimeOffset viewingTime, double siteLat, double siteLon, SiteContext site,
            SkyMapDrawPhase phase = SkyMapDrawPhase.All)
        {
            base.RenderSkyMap(db, contentRect, viewingTime, siteLat, siteLon, site, phase);
            State.CurrentViewMatrix = State.ComputeViewMatrix();
        }

        protected override void RenderObjectOverlay(
            ICelestialObjectDB db, RectF32 contentRect,
            float baseFontSize, SiteContext site, bool dimBelowHorizon, PlannerState plannerState,
            bool showAllOverlays)
            => RenderObjectOverlayPrimitive(db, contentRect, baseFontSize,
                site, dimBelowHorizon, plannerState, showAllOverlays);
    }

    /// <summary>
    /// The owner's M83 autocrop, opened with the sky backdrop on at 132 % and panned so the frame's right edge sits in the
    /// left third of the pane and the rest is backdrop.
    /// </summary>
    private static async Task<(MapWithObjectOverlay Tab, ICelestialObjectDB Db, DateTimeOffset When)> PrepareAsync(
        ViewerE2E e2e, CancellationToken ct, bool distorted = true)
    {
        var db = await SharedCatalogDB.InitAsync(ct);

        // M83's field. The WCS is the FULL master's moved by the crop, so CRPIX is not the frame's centre.
        var scaleDeg = ArcsecPerPixel / 3600.0;
        var full = new WCS(204.2563 / 15.0, -29.8407)
        {
            CRPix1 = (FrameW / 2.0) + CropX,
            CRPix2 = (FrameH / 2.0) + CropY,
            CD1_1 = -scaleDeg,
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = scaleDeg,
            // A plate solution's own distortion: about 20 px at the frame's edge, which a rigid view cannot follow.
            SipOrder = distorted ? 2 : 0,
            SipA = distorted ? SipTerm(2, 0, 4e-5) : null,
            SipB = distorted ? SipTerm(0, 2, 4e-5) : null,
        };
        var wcs = full.CroppedTo(CropX, CropY);
        var path = e2e.WriteSolvedFits("m83-cropped.fits", FrameW, FrameH, wcs);
        await e2e.OpenAsync(path, ct);

        var when = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);
        var tab = new MapWithObjectOverlay(e2e.Renderer) { FontPath = FontResolver.ResolveSystemFont() };
        tab.State.ViewDrivenExternally = true;
        tab.State.ShowObjectOverlay = true;
        e2e.Viewer.CelestialObjectDB = new DotNext.Threading.AsyncLazy<ICelestialObjectDB>(db);
        e2e.Viewer.SkyBackdrop = tab;
        e2e.Viewer.SkyTimeProvider = new FakeTimeProviderWrapper(when);
        e2e.Viewer.SkyPlannerState = new PlannerState
        {
            ObjectDb = db,
            SiteLatitude = -34.0,
            SiteLongitude = 150.0,
            SiteTimeZone = TimeSpan.Zero,
            PlanningDate = when,
        };
        e2e.State.ShowSkyBackdrop = true;
        e2e.State.ShowOverlays = true;
        e2e.State.ZoomToFit = false;
        e2e.State.Zoom = 1.32f;
        e2e.Frame();

        // Pan so the frame's right edge sits in the left third of the pane and the rest is backdrop.
        var area = e2e.Viewer.ImageArea;
        var placed = e2e.Viewer.Placement;
        var dx = (area.X + (area.Width / 3f)) - (placed.OffsetX + placed.DrawW);
        e2e.State.PanOffset = (e2e.State.PanOffset.X + dx, e2e.State.PanOffset.Y);
        e2e.Frame();
        e2e.Frame();
        return (tab, db, when);
    }

    /// <summary>Taps the first catalogue object the map resolves in the screen band and returns the selected one.</summary>
    private static CatalogIndex SelectIn(ViewerE2E e2e, MapWithObjectOverlay tab, ICelestialObjectDB db, DateTimeOffset when,
        float x0, float x1, float y0, float y1)
    {
        (float X, float Y, CatalogIndex Index)? found = null;
        for (var x = x0; x < x1 && found is null; x += 10f)
        {
            for (var y = y0; y < y1; y += 10f)
            {
                if (SkyMapSearchActions.ResolveHoverAtScreenPoint(tab.State, db, when, x, y, null) is { IsEphemeris: false } hit)
                {
                    found = (x, y, hit.Index);
                    break;
                }
            }
        }

        var (tapX, tapY, index) = found.ShouldNotBeNull("the map has to resolve a catalogue object in the band");

        // Hover first, as a pointer does on its way to a tap, so the map's state is current for the press.
        e2e.Host.HandlePointer(new InputEvent.MouseMove(tapX, tapY));
        e2e.Frame();
        UiRouting.Route(e2e.Viewer, new InputEvent.MouseDown(tapX, tapY), new InputEvent.MouseUp(tapX, tapY));
        e2e.Frame();
        var selected = e2e.State.SelectedObject.ShouldNotBeNull($"tap ({tapX:F1},{tapY:F1}) {index}");
        e2e.Viewer.Ellipses.Clear();
        e2e.Frame();
        return selected.Index;
    }

    /// <summary>Where the frame's own mapping (SkyToPixel, then the viewer's frame-to-screen) puts a sky point.</summary>
    private static (float X, float Y) WcsScreenOf(ViewerE2E e2e, double ra, double dec)
    {
        var wcs = e2e.Controller.Document.ShouldNotBeNull().Findings.Wcs.ShouldNotBeNull();
        var px = wcs.SkyToPixel(ra, dec).ShouldNotBeNull();
        var (wx, wy, _) = e2e.Viewer.FrameToScreen(e2e.State, px.X, px.Y);
        return (wx, wy);
    }

    /// <summary>
    /// Past the picture's right edge the ring is the map's marker, three draws of one sky point (#1994): the ring the viewer
    /// strokes, the marker the map PAINTED (read off its draw call) and the frame's own mapping of the object. On the
    /// undistorted frame all three agree within 1 px. On the SIP frame the WCS position past the edge is the distortion
    /// polynomial extrapolated off the sensor, which legitimately differs from the map's rigid view by the distortion there
    /// (about 20 px), so only ring against marker is asserted there: the marker is what the user sees.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    public async Task SelectionMarkerAndWcsPositionAgreePastTheEdge(float dpi, bool distorted)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi);
        var (tab, db, when) = await PrepareAsync(e2e, ct, distorted);
        var area = e2e.Viewer.ImageArea;
        var placed = e2e.Viewer.Placement;
        var frameRight = placed.OffsetX + placed.DrawW;

        var index = SelectIn(e2e, tab, db, when, frameRight + 30f, area.X + area.Width - 20f, area.Y + 20f, area.Y + area.Height - 20f);
        db.TryLookupByIndex(index, out var obj).ShouldBeTrue();
        var ring = e2e.Viewer.Ellipses[^1];
        tab.DrawnMarkers.TryGetValue(index, out var marker).ShouldBeTrue($"the map painted no marker for {index}");
        var (wx, wy) = WcsScreenOf(e2e, obj.RA, obj.Dec);

        var message = $"ring ({ring.Cx:F1},{ring.Cy:F1}) marker ({marker.X:F1},{marker.Y:F1}) wcs ({wx:F1},{wy:F1}) for {index}";
        ((double)Math.Abs(ring.Cx - marker.X)).ShouldBeLessThan(1.0, message);
        ((double)Math.Abs(ring.Cy - marker.Y)).ShouldBeLessThan(1.0, message);
        if (!distorted)
        {
            ((double)Math.Abs(ring.Cx - wx)).ShouldBeLessThan(1.0, message);
            ((double)Math.Abs(ring.Cy - wy)).ShouldBeLessThan(1.0, message);
            ((double)Math.Abs(marker.X - wx)).ShouldBeLessThan(1.0, message);
            ((double)Math.Abs(marker.Y - wy)).ShouldBeLessThan(1.0, message);
        }
    }

    /// <summary>Inside the picture the ring is the frame's own mapping of the object, on the SIP frame as on the plain one.</summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    public async Task SelectionRingInsideThePictureIsOnTheWcsPosition(float dpi, bool distorted)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi);
        var (tab, db, when) = await PrepareAsync(e2e, ct, distorted);
        var area = e2e.Viewer.ImageArea;
        var placed = e2e.Viewer.Placement;

        var index = SelectIn(e2e, tab, db, when, area.X + 20f, placed.OffsetX + placed.DrawW - 40f, area.Y + 40f, area.Y + area.Height - 40f);
        db.TryLookupByIndex(index, out var obj).ShouldBeTrue();
        var ring = e2e.Viewer.Ellipses[^1];
        var (wx, wy) = WcsScreenOf(e2e, obj.RA, obj.Dec);

        var message = $"ring ({ring.Cx:F1},{ring.Cy:F1}) wcs ({wx:F1},{wy:F1}) for {index}";
        ((double)Math.Abs(ring.Cx - wx)).ShouldBeLessThan(1.0, message);
        ((double)Math.Abs(ring.Cy - wy)).ShouldBeLessThan(1.0, message);
    }

    /// <summary>A declination label's own value in degrees, from its text ("-30d04'" form, seconds optional).</summary>
    private static double DecOfLabel(string text)
    {
        var m = Regex.Match(text, "^([+-])(\\d+)°(?:(\\d+)')?(?:(\\d+(?:\\.\\d+)?)\")?$");
        m.Success.ShouldBeTrue($"a declination label, got '{text}'");
        var deg = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
            + (m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 60.0 : 0.0)
            + (m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / 3600.0 : 0.0);
        return m.Groups[1].Value == "-" ? -deg : deg;
    }

    /// <summary>
    /// With the backdrop on, the frame's grid and the backdrop's are ONE grid (#1994): the lines the shader draws across the
    /// pane are the frame's tangent plane (CRPIX, CD, no SIP), so a label naming a declination where its line leaves the
    /// picture must sit on that very line, and the map draws no grid of its own beside it. The labels read the frame's
    /// SIP-corrected solution and the shader does not, so on a distorted frame a label named a declination its line was
    /// not on, by the distortion at the edge.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task GridLabelsSitOnTheLinesThatContinueIntoTheBackdrop(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi);
        var (tab, _, _) = await PrepareAsync(e2e, ct);

        e2e.Viewer.PaneWideGrid.ShouldBeTrue("the pane is a few degrees across, well inside the tangent plane");
        tab.State.DrawOwnGrid.ShouldBeFalse("the map stands its own grid and its Dec labels down, so no second set shows");

        var placed = e2e.Viewer.Placement;
        var frameRight = placed.OffsetX + placed.DrawW;
        var wcs = e2e.Controller.Document.ShouldNotBeNull().Findings.Wcs.ShouldNotBeNull();

        // What the shader draws: the same reference, the same CD, and no distortion polynomial.
        var gridWcs = wcs with { SipOrder = 0 };
        var arcsecPerScreenPx = gridWcs.PixelScaleArcsec / placed.Scale;

        var rightEdge = e2e.Viewer.GridLabelPlacements
            .Where(l => l.IsDec && Math.Abs(l.LineX - frameRight) < 2f)
            .ToArray();
        rightEdge.Length.ShouldBeGreaterThanOrEqualTo(2, "two declinations leave the picture through its right edge");

        foreach (var label in rightEdge)
        {
            var px = ((label.LineX - placed.OffsetX) / placed.Scale) - 0.5;
            var py = ((label.LineY - placed.OffsetY) / placed.Scale) - 0.5;
            var sky = gridWcs.PixelToSky(px, py).ShouldNotBeNull();
            var offPx = Math.Abs(sky.Dec - DecOfLabel(label.Text)) * 3600.0 / arcsecPerScreenPx;
            offPx.ShouldBeLessThan(1.0, $"'{label.Text}' ({DecOfLabel(label.Text):F5}) is {offPx:F1} screen px off the grid line it names (line dec {sky.Dec:F5} at {px:F1},{py:F1})");
        }
    }
}

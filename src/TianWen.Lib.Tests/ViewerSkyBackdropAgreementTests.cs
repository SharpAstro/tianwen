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
        ViewerE2E e2e, CancellationToken ct)
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
            SipOrder = 2,
            SipA = SipTerm(2, 0, 4e-5),
            SipB = SipTerm(0, 2, 4e-5),
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

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task SelectionMarkerAndWcsPositionAgree(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi);
        var (tab, db, when) = await PrepareAsync(e2e, ct);
        var area = e2e.Viewer.ImageArea;
        var placed = e2e.Viewer.Placement;
        var frameRight = placed.OffsetX + placed.DrawW;

        // An object the map draws in the band past the frame's right edge, found by the map's own resolver.
        (float X, float Y, CatalogIndex Index)? found = null;
        for (var x = frameRight + 30f; x < area.X + area.Width - 20f && found is null; x += 10f)
        {
            for (var y = area.Y + 20f; y < area.Y + area.Height - 20f; y += 10f)
            {
                if (SkyMapSearchActions.ResolveHoverAtScreenPoint(tab.State, db, when, x, y, null) is { IsEphemeris: false } hit)
                {
                    found = (x, y, hit.Index);
                    break;
                }
            }
        }

        var (tapX, tapY, index) = found.ShouldNotBeNull("the map has to draw a catalogue object past the frame's edge");
        db.TryLookupByIndex(index, out var obj).ShouldBeTrue();

        // Hover first, as a pointer does on its way to a tap, so the map's state is current for the press.
        e2e.Host.HandlePointer(new InputEvent.MouseMove(tapX, tapY));
        e2e.Frame();
        UiRouting.Route(e2e.Viewer, new InputEvent.MouseDown(tapX, tapY), new InputEvent.MouseUp(tapX, tapY));
        e2e.Frame();
        var diag = $"tap ({tapX:F1},{tapY:F1}) {index} area {area} placed {placed.OffsetX:F1}+{placed.DrawW:F1} "
            + $"hostResolve {tab.ResolveCatalogObjectAt(tapX, tapY)} hover {e2e.State.HoverObject?.Name} overlays {e2e.State.ShowOverlays} rect {tab.State.LastContentRect}";
        e2e.State.SelectedObject.ShouldNotBeNull(diag).Index.ShouldBe(index);

        e2e.Viewer.Ellipses.Clear();
        e2e.Frame();
        var ring = e2e.Viewer.Ellipses[^1];

        // Three draws of one sky point: the selection ring, the map's marker and the frame's own mapping.
        var rect = tab.State.LastContentRect;
        var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, tab.State.FieldOfViewDeg);
        SkyMapProjection.ProjectWithMatrix(obj.RA, obj.Dec, tab.State.CurrentViewMatrix, ppr,
            rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f), out var markerX, out var markerY).ShouldBeTrue();

        var currentWcs = e2e.Controller.Document.ShouldNotBeNull().Findings.Wcs.ShouldNotBeNull();
        var px = currentWcs.SkyToPixel(obj.RA, obj.Dec).ShouldNotBeNull();
        var (wx, wy, _) = e2e.Viewer.FrameToScreen(e2e.State, px.X, px.Y);

        var message = $"ring ({ring.Cx:F1},{ring.Cy:F1}) marker ({markerX:F1},{markerY:F1}) wcs ({wx:F1},{wy:F1}) for {index}";
        // Past the picture the map draws the object, and a rigid view cannot follow the frame's distortion
        // there, so the WCS position (the polynomial extrapolated off the sensor) is not the reference:
        // the ring lands on the marker the user sees.
        ((double)Math.Abs(ring.Cx - markerX)).ShouldBeLessThan(1.0, message);
        ((double)Math.Abs(ring.Cy - markerY)).ShouldBeLessThan(1.0, message);
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

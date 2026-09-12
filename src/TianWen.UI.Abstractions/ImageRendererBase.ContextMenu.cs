using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// Writes text to the system clipboard. Set by the host, because a clipboard is a platform
        /// service (SDL in the desktop hosts, the browser's async API in the web one) and this
        /// assembly has no platform.
        /// <para>
        /// A null callback is not an error: the copy items are still offered and still report what
        /// they resolved in the status bar, which is the half a user can read back to you. Hiding
        /// them instead would make a host that merely forgot the wiring look like a build without
        /// the feature.
        /// </para>
        /// </summary>
        public Action<string>? SetClipboardText { get; set; }

        /// <summary>
        /// Right-click on the image: the values already under the cursor, as text one can paste.
        /// </summary>
        /// <remarks>
        /// <para><b>It copies what the readout shows, and computes nothing.</b> Every mouse move
        /// already resolves <see cref="ViewerState.CursorPixelInfo"/> -- position, per-channel sample,
        /// and RA/Dec where the document has a WCS -- so this is a formatter over state that exists.
        /// The DISPLAYED colour (the post-stretch hex) is deliberately not offered: it is a different
        /// number, the GPU owns it, and recomputing it on the CPU is a feature of its own rather than
        /// a menu item.</para>
        /// <para><b>Why a menu rather than modifier-clicks.</b> A chord has to be documented somewhere
        /// to be discovered, and the only place it could be is the panel nobody opens until something
        /// is already wrong. The menu also has somewhere to grow -- a share link needs a home.</para>
        /// <para><b>It reuses the toolbar's dropdown</b> rather than adding a second overlay type:
        /// <see cref="ViewerState.ToolbarDropdown"/> already owns keyboard claim, hover highlight,
        /// scrolling, dismissal and -- through <see cref="ViewerState.OverlayOwnsPointer"/> -- the
        /// z-order answer for hover. A parallel menu would need all of that again.</para>
        /// <para><b>Both press dispatchers must call this.</b> The viewer has two (the standalone
        /// host's own, and <c>HandleViewerMouseDown</c> for the embedded case); wiring only one of
        /// them is the split that left single-click selection broken in the standalone host.</para>
        /// </remarks>
        /// <returns><see langword="true"/> when a menu was opened, so the caller stops treating the
        /// press as the start of a pan.</returns>
        public bool TryOpenImageContextMenu(ViewerState state, float px, float py)
        {
            // Inside the image viewport only: a right-click on the toolbar reverse-cycles a button,
            // and one on the file list or a panel belongs to whatever is there. The same arranged rect
            // the pan gesture tests against.
            var area = _layout.ImageArea;
            if (px < area.X || px >= area.X + area.Width || py < area.Y || py >= area.Y + area.Height)
            {
                return false;
            }

            var items = BuildContextMenuItems(state, px, py);
            if (items.IsEmpty)
            {
                return false;
            }

            // A zero-size anchor at the cursor: OpenDropdown places the menu at bounds.Y + Height and
            // widens it to the longest label, so its top-left lands under the pointer while ClampX
            // keeps it on screen near the right edge.
            OpenDropdown(
                state,
                new RectF32(new Vector2(px, py), Vector2.Zero),
                items.Select(static i => i.Label).ToImmutableArray(),
                (index, _) =>
                {
                    if ((uint)index >= (uint)items.Length)
                    {
                        return;
                    }
                    var item = items[index];
                    switch (item.Action)
                    {
                        case ImageContextMenuAction.OpenUrl:
                            PostSignal(new OpenUrlSignal(item.Payload));
                            state.StatusMessage = $"Opening the {item.Description}...";
                            state.NeedsRedraw = true;
                            break;
                        default:
                            CopyToClipboard(state, item.Description, item.Payload);
                            break;
                    }
                });
            return true;
        }

        /// <summary>
        /// Resolves the pixel under the cursor and asks <see cref="ImageContextMenu"/> for its items.
        /// Payloads are therefore fixed WHEN THE MENU OPENS, never when an item is picked: the pointer
        /// has to move to reach an item, so a payload read on selection would describe the pixel under
        /// the menu instead of the one that was right-clicked.
        /// </summary>
        private ImmutableArray<ImageContextMenuItem> BuildContextMenuItems(ViewerState state, float px, float py)
        {
            // Resolved from THIS press rather than trusting the last mouse-move to have left one: a
            // press is not always preceded by a move over the image (a synthesized click, a touch tap,
            // a window that just took focus under the pointer), and a menu that silently fails to open
            // in those cases is indistinguishable from the feature being absent.
            //
            // ResolveSkyPixelAt asks for EVERY channel where a document can answer -- the per-move
            // readout samples only the channel on screen (deliberately, since it runs on every mouse
            // move over a large master), and a copied value naming one of three channels is the
            // ambiguity this avoids. It also answers for a press BESIDE the picture, where there is no
            // pixel but there is still a sky position, so the menu opens there too.
            var info = ResolveSkyPixelAt(state, px, py);

            if (info is not { } pixel)
            {
                return ImmutableArray<ImageContextMenuItem>.Empty;
            }

            var image = _document?.UnstretchedImage;
            var fovDeg = image is { } img
                ? SkyAtlasLink.FieldOfViewDeg(_document?.Wcs, img.Width, img.Height)
                : null;

            return ImageContextMenu.ItemsFor(
                pixel, fovDeg, image?.ImageMeta.ExposureStartTime, FindObjectAt(pixel, fovDeg),
                state.SelectedObject);
        }

        /// <summary>
        /// The catalogued deep-sky object the click landed on, or null.
        /// </summary>
        /// <remarks>
        /// <para><b>Never blocks and never triggers the catalogue load.</b> Reads
        /// <see cref="CelestialObjectDB"/> only when it has already been created -- the same test the
        /// Overlays button uses -- so a right-click on a fresh viewer costs nothing and simply offers no
        /// object entries. Waiting here would freeze the menu on the first press for a full Tycho-2
        /// init.</para>
        /// <para><b>The tolerance is a fraction of the FIELD, not a fixed radius.</b> A click is a
        /// gesture aimed at something on screen, so what counts as "on it" scales with how much sky the
        /// frame covers: 2% of the field width, floored at half an arcminute so a deep zoom still has a
        /// target, capped at half a degree so a wide field does not claim a galaxy across the frame.
        /// Without a plate scale (no <paramref name="fovDeg"/>) there is no field to take a fraction of,
        /// and the answer is nothing rather than a guess.</para>
        /// <para>Nearest wins, over every cell of the coordinate grid the tolerance reaches
        /// (<see cref="CandidatesWithin"/>) -- the same
        /// <see cref="ICelestialObjectDB.DeepSkyCoordinateGrid"/> the overlay engine gathers from, so
        /// the menu can only ever name something the overlay would have drawn.</para>
        /// </remarks>
        private ImageContextMenuObject? FindObjectAt(PixelInfo pixel, double? fovDeg)
            => FindCatalogObjectAt(pixel, fovDeg) is { } found
                ? new ImageContextMenuObject(NameOf(found.Object), found.Object.Index.ToCanonical())
                : null;

        /// <summary>
        /// How an object is NAMED, once. <see cref="CelestialObject.DisplayName"/> is the overlay
        /// label's own pick (priority, then longest-then-alphabetical), so the menu, the selection and
        /// the marker beside them all name an object the same way.
        /// </summary>
        private static string NameOf(CelestialObject obj)
            => obj.CommonNames.Count > 0 ? obj.DisplayName : obj.Index.ToCanonical();

        /// <summary>
        /// How far outside the sensor a click may still be resolved to a sky position, in degrees from
        /// the frame's tangent point.
        /// </summary>
        /// <remarks>
        /// <para><b>A bound is needed, and it is not about the projection's maths.</b> A gnomonic
        /// deprojection is exact and single-valued anywhere short of 90 degrees, so nothing breaks
        /// arithmetically just outside the frame. What does not hold is the SOLUTION: a plate solve is
        /// fitted to stars ON the sensor, and asking it about a point far beyond one answers with a
        /// confidence it never earned -- which for a click means naming an object that is not
        /// there.</para>
        /// <para>Five degrees because it comfortably covers everything the frame's overlay can DRAW
        /// outside the picture: that gather runs over the image's own bounds expanded by one degree, so
        /// any marker beside the frame is within about a degree of it. Objects further out belong to
        /// the sky map behind, which places them from ITS projection and would have to answer for them
        /// itself -- see the note in the plan.</para>
        /// </remarks>
        private const double MaxOffFrameClickAngleDeg = 5.0;

        /// <summary>
        /// The pixel a screen position names, for the purpose of asking WHERE IN THE SKY it is -- which
        /// a position outside the sensor still has an answer for.
        /// </summary>
        /// <remarks>
        /// <para><b>The bug this exists for:</b> a click beside the picture could not select anything.
        /// <see cref="ViewerActions.UpdateCursorFromScreenPosition"/> is the pixel READOUT's resolver
        /// and nulls both cursor fields off-raster -- correctly, since there is no pixel there to
        /// report -- so the selection path got nothing and cleared instead. But the overlay draws
        /// objects the gather found beside the frame as well as in it (that is what the in-frame label
        /// tier is about), and a marker you can see is a marker you expect to be able to click.</para>
        /// <para>On the raster it defers to that same resolver, so the sample, the readout and the
        /// selection cannot disagree about what is under the pointer. Off it, the pixel VALUES are
        /// genuinely absent and the position is synthesised from the WCS alone --
        /// <see cref="PixelInfo"/> carries RA/Dec independently of the values, and both consumers here
        /// read only those.</para>
        /// </remarks>
        private PixelInfo? ResolveSkyPixelAt(ViewerState state, float px, float py)
        {
            var layout = CurrentViewportLayout(state);

            ViewerActions.UpdateCursorFromScreenPosition(_document, state, px, py, layout);

            if (_document is { } document && state.CursorImagePosition is { } at)
            {
                return document.GetPixelInfo(at.X, at.Y);
            }

            if (state.CursorPixelInfo is { } reported)
            {
                return reported;
            }

            // Off the raster: the screen position back into the frame's pixel grid through the same
            // layout every overlay draws with -- its origin is the placement the quad was actually
            // drawn at, so the crop is carried -- and then the WCS.
            if (_document?.Wcs is not { HasCDMatrix: true } wcs)
            {
                return null;
            }

            if (layout.Zoom <= 0f)
            {
                return null;
            }

            var (imageX, imageY) = WcsAnnotationLayer.ScreenToImage(px, py, layout);

            var angle = SkyBackdropView.TangentAngleDeg(in wcs, imageX, imageY);
            if (!(angle <= MaxOffFrameClickAngleDeg))
            {
                // NaN lands here too, which is the answer for a frame with no usable scale.
                return null;
            }

            if (wcs.PixelToSky(imageX, imageY) is not { } sky)
            {
                return null;
            }

            // The pixel indices are reported in the readout's own 0-based convention for consistency,
            // and are deliberately outside the raster: nothing may sample them, and the empty value
            // array is what says so.
            return new PixelInfo(WcsAnnotationLayer.PixelIndex(imageX), WcsAnnotationLayer.PixelIndex(imageY),
                [], sky.RA, sky.Dec);
        }

        /// <summary>
        /// The nearest catalogued object to <paramref name="pixel"/>, as the catalogue holds it.
        /// </summary>
        /// <remarks>
        /// <b>The search itself, with nothing projected out of it yet</b>, because two callers want
        /// different halves: the context menu needs a name and a designation, while a SELECTION also
        /// needs the object's own coordinates and its full identification stack. Splitting it this way
        /// rather than widening <see cref="ImageContextMenuObject"/> keeps the menu's payload record
        /// about the menu, and keeps one implementation of "which object is under this pixel".
        /// </remarks>
        private (CelestialObject Object, CatalogIndex Index)? FindCatalogObjectAt(PixelInfo pixel, double? fovDeg)
        {
            if (pixel.RA is not { } raHours || pixel.Dec is not { } dec
                || fovDeg is not { } fov || !double.IsFinite(fov) || fov <= 0
                || LoadedCatalog is not { } db)
            {
                return null;
            }

            var toleranceDeg = Math.Clamp(0.02 * fov, 0.5 / 60.0, 0.5);
            var best = double.MaxValue;
            CelestialObject? found = null;
            foreach (var index in CandidatesWithin(db.DeepSkyCoordinateGrid, raHours, dec, toleranceDeg))
            {
                if (!db.TryLookupByIndex(index, out var candidate))
                {
                    continue;
                }
                var candidateRa = candidate.RA;
                var candidateDec = candidate.Dec;
                if (!double.IsFinite(candidateRa) || !double.IsFinite(candidateDec))
                {
                    // Solar-system bodies live in the DB at NaN/NaN by design; they are not what a
                    // click on a still frame is asking about.
                    continue;
                }

                // The overlay's OWN type gate, so this can only name something the overlay would have
                // drawn -- which is what the remarks above have always claimed and, until a
                // click-to-select test went looking for M42, was not true. The Orion Nebula's core is
                // full of catalogued objects of types the overlay deliberately filters out, and
                // "nearest wins" was picking one: a click on the middle of M42 answered HH 1146, a
                // Herbig-Haro object that is drawn nowhere, named nowhere else, and not what anyone
                // clicking a nebula is asking about. Same two predicates the overlay uses, from the
                // same class, rather than a list of types repeated here.
                var type = candidate.ObjectType;
                if (!OverlayEngine.IsExtendedObjectType(type) && !OverlayEngine.IsStarType(type))
                {
                    continue;
                }

                // Flat-sky separation with the cos(dec) term on RA: the tolerance is arcminutes, so
                // nothing here needs a spherical law of cosines.
                var dRa = (candidateRa - raHours) * 15.0 * Math.Cos(dec * Math.PI / 180.0);
                var dDec = candidateDec - dec;
                var separation = Math.Sqrt((dRa * dRa) + (dDec * dDec));
                if (separation < best && separation <= toleranceDeg)
                {
                    best = separation;
                    found = candidate;
                }
            }

            return found is { } obj ? (obj, obj.Index) : null;
        }

        /// <summary>
        /// Every entry of the coordinate grid within <paramref name="toleranceDeg"/> of a sky
        /// position -- across cell boundaries, which is the whole point.
        /// </summary>
        /// <remarks>
        /// <para><b>The grid answers for ONE cell, a degree of Dec by four minutes of RA, and the
        /// tolerance does not respect its edges.</b> Asking only the click's own cell meant an object
        /// a few arcseconds across a boundary from the click was never seen: NGC 7204A sits at
        /// Dec -31.05, so a tap 20 arcseconds north of it fell in the cell above and resolved nothing,
        /// with the object's own marker under the pointer. Walking every cell the tolerance reaches
        /// is what makes the nearest-centre search mean what its name says.</para>
        /// <para>Cell width in RA shrinks with cos(Dec), so the span is widened accordingly and
        /// capped at the whole ring near the pole; an object lives in exactly one cell, so nothing here
        /// needs deduplicating.</para>
        /// </remarks>
        private static IEnumerable<CatalogIndex> CandidatesWithin(
            IRaDecIndex grid, double raHours, double dec, double toleranceDeg)
        {
            // The grid keys Dec by truncating (dec + 90) and RA by truncating ra * 15, so a cell is
            // [d, d + 1) degrees and [k, k + 1) / 15 hours; each is asked for by its centre.
            var decLo = Math.Max(0, (int)Math.Floor(dec - toleranceDeg + 90.0));
            var decHi = Math.Min(180, (int)Math.Floor(dec + toleranceDeg + 90.0));

            var cosDec = Math.Max(Math.Cos(dec * Math.PI / 180.0), 1e-3);
            var raHalfSpanHours = Math.Min(12.0, toleranceDeg / 15.0 / cosDec);
            var raLo = (int)Math.Floor((raHours - raHalfSpanHours) * 15.0);
            var raHi = (int)Math.Floor((raHours + raHalfSpanHours) * 15.0);
            if (raHi - raLo >= 360)
            {
                raLo = 0;
                raHi = 359;
            }

            for (var d = decLo; d <= decHi; d++)
            {
                var cellDec = Math.Min(90.0, d + 0.5 - 90.0);
                for (var k = raLo; k <= raHi; k++)
                {
                    var cellRa = (((k % 360) + 360) % 360 + 0.5) / 15.0;
                    foreach (var index in grid[cellRa, cellDec])
                    {
                        yield return index;
                    }
                }
            }
        }

        /// <summary>
        /// The object whose overlay LABEL is under a screen position, or null.
        /// </summary>
        /// <remarks>
        /// <para>Asked after the markers and before the catalogue: a label is unambiguous where no
        /// marker is, since the placement pass never lets two labels overlap, so the first box
        /// containing the point is the only one, and the letters name exactly one object however far
        /// from its centre they were placed. The selected object's label is the ring's name (see
        /// <see cref="RenderOverlays"/>), so a tap on that re-selects it rather than clearing.</para>
        /// <para>Not before the markers, though, and that order was measured: NGC 7176's three-line
        /// label, placed above it, covers the centre of NGC 7173's marker 19 pixels away, and with the
        /// label asked first a tap on the middle of NGC 7173's outline answered NGC 7176. A box of
        /// letters is not what the user is pointing at when the pointer is inside an outline.</para>
        /// </remarks>
        private (CelestialObject Object, CatalogIndex Index)? FindDrawnLabelAt(float px, float py)
        {
            var drawn = _drawnOverlayObjects;
            if (drawn.IsDefaultOrEmpty || LoadedCatalog is not { } db)
            {
                return null;
            }

            foreach (var d in drawn)
            {
                if (d.LabelBox is { } box
                    && px >= box.X && px < box.X + box.W && py >= box.Y && py < box.Y + box.H
                    && db.TryLookupByIndex(d.Index, out var labelled))
                {
                    return (labelled, d.Index);
                }
            }

            return null;
        }

        /// <summary>
        /// The object whose overlay MARKER encloses a screen position, or null; among several, the
        /// one whose centre is nearest.
        /// </summary>
        /// <remarks>
        /// <para>Asked FIRST: an outline drawn around the pointer is the least ambiguous thing on the
        /// screen, more so than a box of letters that happens to reach over it (see
        /// <see cref="FindDrawnLabelAt"/> for the measured case) and than the nearest catalogue
        /// centre, which a tap deep inside a large ellipse is further from than the tolerance
        /// reaches. The slack keeps a tap a few pixels off a compact object's centre on that object
        /// rather than on whatever encloses it.</para>
        /// <para><b>Nearest centre among the enclosing outlines, not the smallest outline.</b> A
        /// galaxy drawn inside a nebula's outline is nearer a tap on it than the nebula is, so it
        /// wins either way; where the two rules part is the NGC 7204 pair, two galaxies four pixels
        /// apart under the pair's own circle, where "smallest" handed a tap on A's exact centre to B
        /// because B's outline is thinner. An exact tie -- a cluster at the centre of its nebula --
        /// goes to the smaller outline.</para>
        /// <para>Tested against what the renderer PAINTS. <see cref="DrawEllipseOverlay"/> rasterises
        /// an axis-aligned ellipse over the rotated ellipse's bounding box, so the containment test is
        /// on that bounding-box ellipse, not on the rotated one the marker describes; a circle marker
        /// carries its radius in <see cref="OverlayMarker.RadiusPx"/> alone and is round. A few pixels
        /// of slack make a hairline marker (an edge-on galaxy is under two pixels across its minor
        /// axis) hittable at all.</para>
        /// </remarks>
        private (CelestialObject Object, CatalogIndex Index)? FindDrawnMarkerAt(float px, float py)
        {
            var drawn = _drawnOverlayObjects;
            if (drawn.IsDefaultOrEmpty || LoadedCatalog is not { } db)
            {
                return null;
            }

            var slack = 3f * DpiScale;
            DrawnOverlayObject? best = null;
            var bestDistance = float.MaxValue;
            var bestExtent = float.MaxValue;
            foreach (var d in drawn)
            {
                var dx = px - d.ScreenX;
                var dy = py - d.ScreenY;
                var marker = d.Marker;
                float extent;
                bool inside;
                switch (marker.Kind)
                {
                    case OverlayMarkerKind.Cross:
                    {
                        var arm = marker.ArmPx + slack;
                        inside = MathF.Abs(dx) <= arm && MathF.Abs(dy) <= arm;
                        extent = arm * arm;
                        break;
                    }
                    case OverlayMarkerKind.Circle:
                    {
                        var radius = marker.RadiusPx + slack;
                        inside = (dx * dx) + (dy * dy) <= radius * radius;
                        extent = radius * radius;
                        break;
                    }
                    default:
                    {
                        // The painted shape is the bounding-box ellipse of the rotated one.
                        var (sin, cos) = MathF.SinCos(marker.AngleRad);
                        var a = marker.SemiMajorPx;
                        var b = marker.SemiMinorPx;
                        var halfW = MathF.Sqrt((a * a * cos * cos) + (b * b * sin * sin)) + slack;
                        var halfH = MathF.Sqrt((a * a * sin * sin) + (b * b * cos * cos)) + slack;
                        var nx = dx / halfW;
                        var ny = dy / halfH;
                        inside = (nx * nx) + (ny * ny) <= 1f;
                        extent = halfW * halfH;
                        break;
                    }
                }

                if (!inside)
                {
                    continue;
                }

                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance - 0.25f || (MathF.Abs(distance - bestDistance) <= 0.25f && extent < bestExtent))
                {
                    bestDistance = distance;
                    bestExtent = extent;
                    best = d;
                }
            }

            return best is { } hit && db.TryLookupByIndex(hit.Index, out var obj) ? (obj, hit.Index) : null;
        }

        /// <summary>
        /// Selects the catalogued object at a screen position, or clears the selection when there is
        /// nothing there. Returns whether the selection CHANGED, which is what tells the caller a
        /// repaint is owed.
        /// </summary>
        /// <remarks>
        /// <para>Reached from a tap RELEASE, never a press: a press on the picture is the start of a
        /// pan, so selecting there would fire on every drag. See <c>_pressToSelect</c>.</para>
        /// <para><b>A click on empty sky CLEARS.</b> That is what makes the selection dismissable
        /// without a second gesture to learn, and it is the reason this returns a bool rather than the
        /// selection: "nothing changed" and "nothing is selected" are different answers, and only the
        /// first one means no repaint.</para>
        /// </remarks>
        private bool TrySelectObjectAt(ViewerState state, float px, float py)
        {
            // Resolved from THIS release rather than from the last mouse-move: the pointer may never
            // have moved over the image (a synthesized click, a touch tap), and a selection that
            // silently fails then is indistinguishable from the feature being absent. Through
            // ResolveSkyPixelAt, so a click BESIDE the picture still has a sky position -- the overlay
            // draws objects out there and a marker you can see is one you expect to be able to click.
            var info = ResolveSkyPixelAt(state, px, py);

            var image = _document?.UnstretchedImage;
            var fovDeg = image is { } img
                ? SkyAtlasLink.FieldOfViewDeg(_document?.Wcs, img.Width, img.Height)
                : null;

            // In this order: the MARKER enclosing the tap, then the LABEL under it, then the nearest
            // catalogue centre -- each method's remarks say why it sits where it does. Both drawn
            // lookups answer nothing while the overlay is off, so the catalogue search is then the
            // whole resolver, as it always was.
            var resolved = FindDrawnMarkerAt(px, py)
                ?? FindDrawnLabelAt(px, py)
                ?? (info is { } pixel ? FindCatalogObjectAt(pixel, fovDeg) : null);

            if (resolved is not { } hit)
            {
                if (state.SelectedObject is null)
                {
                    return false;
                }

                state.SelectedObject = null;
                state.StatusMessage = null;
                return true;
            }

            var (obj, idx) = hit;
            var selection = BuildSelectionPanelData(obj, idx);

            if (state.SelectedObject == selection)
            {
                return false;
            }

            state.SelectedObject = selection;
            state.StatusMessage = $"Selected {selection.Name}";
            return true;
        }

        /// <summary>
        /// Bakes a selection's floating-panel payload at the CAPTURE instant, once, here -- never off a
        /// live clock, which is right for the sky backdrop's horizon but wrong for a still photograph's
        /// "where was it when this was shot".
        /// </summary>
        /// <remarks>
        /// <b>Both the site and the instant have to be known for either to be trusted.</b> Without a
        /// capture time there is no honest instant to answer "where was it" for, and
        /// <see cref="RiseTransitSetHelper"/> and <see cref="SiteContext"/> both fail SILENTLY on a NaN
        /// site (a false return / <c>IsValid: false</c>) rather than reporting the gap -- so the caller
        /// reads <see cref="SkyMapInfoPanelData.AltDeg"/> being NaN back as the one signal that the
        /// site-dependent rows are unanswerable, and this method makes sure that signal always means
        /// the same thing: "the header did not carry both cards", never a fabricated "now".
        /// </remarks>
        private SkyMapInfoPanelData BuildSelectionPanelData(CelestialObject obj, CatalogIndex idx)
        {
            var shape = LoadedCatalog is { } db && db.TryGetShape(idx, out var s)
                ? s
                : (CelestialObjectShape?)null;

            if (_document?.UnstretchedImage.ImageMeta is { } meta
                && FrameSiteResolver.FromHeader(in meta) is { IsKnown: true } site
                && FrameSiteResolver.CapturedAt(in meta) is { } capturedAt)
            {
                var siteContext = SiteContext.Create(site.LatitudeDeg, site.LongitudeDeg, capturedAt);
                return SkyMapInfoPanelData.FromCatalogObject(
                    obj, site.LatitudeDeg, site.LongitudeDeg, capturedAt, in siteContext, shape);
            }

            // Either half missing is the SAME answer, which is why there is one path out: without both
            // there is no instant to answer "where was it" for, and a NaN site is what SiteContext and
            // RiseTransitSetHelper each read as "do not answer" -- leaving the NaN altitude the panel
            // gates its site-dependent rows on.
            return SkyMapInfoPanelData.FromCatalogObject(
                obj, double.NaN, double.NaN, default, default, shape);
        }

        private void CopyToClipboard(ViewerState state, string description, string payload)
        {
            if (SetClipboardText is { } setter)
            {
                setter(payload);
                state.StatusMessage = $"Copied {description}";
            }
            else
            {
                // No clipboard on this host: still say what it was, so the value is at least readable.
                state.StatusMessage = $"{description}: {payload.Replace('\n', ' ')}";
            }
            state.NeedsRedraw = true;
        }
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
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
        /// <para>Nearest wins, over the coordinate grid's own cell -- the same
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
            var area = _layout.ImageArea;

            ViewerActions.UpdateCursorFromScreenPosition(
                _document, state, px, py, area.X, area.Y, area.Width, area.Height);

            if (_document is { } document && state.CursorImagePosition is { } at)
            {
                return document.GetPixelInfo(at.X, at.Y);
            }

            if (state.CursorPixelInfo is { } reported)
            {
                return reported;
            }

            // Off the raster: the frame's own placement back into its pixel grid, then the WCS. Through
            // _placement rather than re-deriving from Zoom and PanOffset, because that is what the
            // image quad was actually drawn with -- it carries the crop, which the readout's own
            // arithmetic does not have to.
            if (_document?.Wcs is not { HasCDMatrix: true } wcs)
            {
                return null;
            }

            var p = _placement;
            if (p.Scale <= 0f)
            {
                return null;
            }

            var imageX = ((px - p.OffsetX) / p.Scale) + 1.0;
            var imageY = ((py - p.OffsetY) / p.Scale) + 1.0;

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
            return new PixelInfo((int)Math.Floor(imageX - 1.0), (int)Math.Floor(imageY - 1.0),
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
            foreach (var index in db.DeepSkyCoordinateGrid[raHours, dec])
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
        /// <para>The identification lines come from <see cref="OverlayEngine.BuildOverlayLabel"/> at
        /// the full-zoom, in-frame tier, deliberately: the panel is the place a person goes for the
        /// FULL identity, and asking for it at the current zoom would make the panel's content change
        /// as the wheel turns.</para>
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

            var resolved = info is { } pixel ? FindCatalogObjectAt(pixel, fovDeg) : null;

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
            var designation = obj.Index.ToCanonical();
            var lines = LoadedCatalog is { } db
                ? OverlayEngine.BuildOverlayLabel(obj, idx, db, zoom: 1f).ToImmutableArray()
                : [NameOf(obj)];

            var selection = new ViewerObjectSelection(
                NameOf(obj), designation, obj.RA, obj.Dec, lines);

            if (state.SelectedObject == selection)
            {
                return false;
            }

            state.SelectedObject = selection;
            state.StatusMessage = $"Selected {selection.Name}";
            return true;
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

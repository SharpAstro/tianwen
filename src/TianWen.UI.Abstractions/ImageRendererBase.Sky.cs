using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The sky the photograph came from, drawn behind it -- the top rung of the context ladder
    /// (<see cref="ViewerOverlayLevel.Sky"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The map is HOSTED, not reimplemented.</b> <see cref="SkyMapTab{TSurface}"/> already draws the
    /// star field, the constellations, the milky way, the planets, the comets and the horizon, and it
    /// is renderer-agnostic, so the viewer supplies one and points it at the frame. Drawing any of
    /// that a second time here would be the twin the annotated export deliberately avoided
    /// (docs/plans/viewer-prerelease-fixes.md, P22) -- a second path that drifts from the first with
    /// nothing failing when it does.
    /// </para>
    /// <para>
    /// <b>Two passes, because the palette has to outrank the picture.</b> The sky is drawn first, under
    /// the image quad; the palette floats above everything. They are the two halves of one frame the
    /// map opened, so the sky pass must come first and both must run or neither.
    /// </para>
    /// </remarks>
    public abstract partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// The sky map this viewer draws behind the frame, supplied by the host. Null in a host that
        /// wired none -- the CPU export surface, the GUI's embedded preview panes -- and then the top
        /// rung of the ladder simply has nothing extra to show.
        /// </summary>
        /// <remarks>
        /// The host also owns its lifetime and its GPU resources; this class only points it and asks
        /// it to draw. Set <see cref="SkyMapState.ViewDrivenExternally"/> on its state when attaching,
        /// which is what stops the map homing on the site and rolling back to north under the frame.
        /// </remarks>
        public SkyMapTab<TSurface>? SkyBackdrop
        {
            get => _skyBackdrop;
            set
            {
                _skyBackdrop = value;
                // The composition is STATED, once, here: the palette's rows are registered on the map
                // and nothing asking only this widget would ever find them. Empty when there is no
                // backdrop, which is what makes a host that wired none behave exactly as before.
                _children = value is null ? [] : [value];
                ShareUiContext(value is null ? [] : [value]);
            }
        }

        private SkyMapTab<TSurface>? _skyBackdrop;
        private IReadOnlyList<PixelWidgetBase<TSurface>> _children = [];

        /// <inheritdoc/>
        protected override IReadOnlyList<PixelWidgetBase<TSurface>> Children => _children;

        // The sky map paints UNDER everything this widget draws -- except its palette, which paints
        // over all of it. So the composite's default z-order (this widget's own regions first, then
        // children) is the wrong one here, and these three ask the map FIRST. Each then falls through
        // to the base, which asks it again and gets the same no: one extra walk of a list that just
        // answered, in exchange for not restating the composition. A dispatch cannot double-fire on
        // that second ask, because reaching it means nothing on the map was hit.

        /// <inheritdoc/>
        public override HitResult? HitTest(float x, float y)
            => _skyBackdrop?.HitTest(x, y) ?? base.HitTest(x, y);

        /// <inheritdoc/>
        public override HitResult? HitTestAndDispatch(float x, float y, InputModifier modifiers = InputModifier.None)
            => _skyBackdrop?.HitTestAndDispatch(x, y, modifiers) ?? base.HitTestAndDispatch(x, y, modifiers);

        /// <inheritdoc/>
        public override CursorKind? HitTestCursor(float x, float y)
            => _skyBackdrop?.HitTestCursor(x, y) ?? base.HitTestCursor(x, y);

        /// <summary>
        /// What the sky map reads its catalog, site and instant from, supplied by the host. The viewer
        /// has no planner; this is a carrier, and the fields that matter are the object database, the
        /// site (for the horizon) and the planning date (the frame's own capture instant).
        /// </summary>
        public PlannerState? SkyPlannerState { get; set; }

        /// <summary>The clock the sky map runs on, supplied by the host.</summary>
        public ITimeProvider? SkyTimeProvider { get; set; }

        /// <summary>
        /// Whether the backdrop is switched on AND has everything it needs to draw: a map, a carrier
        /// with a catalog in it, a clock, and a frame with an astrometric solution to place.
        /// </summary>
        /// <remarks>
        /// The WCS requirement is what makes this honest rather than decorative: without one there is
        /// no answer to where the photograph is, and a sky drawn behind it would be a picture of the
        /// wrong part of the sky rather than a missing feature.
        /// </remarks>
        private bool SkyBackdropActive
            => _state is { ShowSkyBackdrop: true }
               && SkyBackdrop is not null
               && SkyTimeProvider is not null
               && SkyPlannerState is { ObjectDb: not null }
               && _document?.Wcs is { HasCDMatrix: true };

        /// <summary>
        /// Whether the sky map wants another frame -- a star buffer that finished building off-thread,
        /// a milky-way texture that landed, a palette mid-fade.
        /// </summary>
        /// <remarks>
        /// The map keeps its own redraw flag and the viewer keeps its own, and a host that gates on
        /// only one of them renders a frozen sky under a live image. Reading it CLEARS it, the same
        /// contract the viewer's own flag has with its frame loop.
        /// </remarks>
        public bool TakeSkyRedrawRequest()
        {
            if (SkyBackdrop is not { State.NeedsRedraw: true } tab)
            {
                return false;
            }

            tab.State.NeedsRedraw = false;
            return true;
        }

        /// <summary>
        /// Draws the sky behind the photograph, pointed at wherever the viewer has currently placed
        /// it. Called from the image pane's own paint, after the canvas ground and before the image
        /// quad. Returns whether it drew, which is what tells the palette pass there is a frame open.
        /// </summary>
        private bool RenderSkyBackdrop(ViewerState state, RectF32 area)
        {
            if (!SkyBackdropActive
                || SkyBackdrop is not { } tab
                || SkyPlannerState is not { } planner
                || SkyTimeProvider is not { } clock
                || _document?.Wcs is not { } wcs)
            {
                return false;
            }

            var p = _placement;
            if (SkyBackdropView.Solve(in wcs, area, p.OffsetX, p.OffsetY, p.Scale) is not { } solution)
            {
                return false;
            }

            SkyBackdropView.ApplyTo(tab.State, in solution);
            ApplyFrameContext(planner);

            // The map paints its own ground (a sky colour driven by the sun's altitude) across the
            // whole rect it is given, so it replaces the canvas fill rather than sitting on it -- and
            // it must be clipped to the pane, or a star field reaches under the toolbar and the file
            // list exactly as the image quad would.
            PushClip(area.X, area.Y, area.Width, area.Height);
            tab.RenderSkyBehind(planner, area, clock);
            PopClip();
            return true;
        }

        /// <summary>
        /// Hands the sky map the object catalog once it has finished loading. The map and the viewer's
        /// own overlays read the SAME instance, so the top rung costs no second load.
        /// </summary>
        /// <remarks>
        /// Per frame rather than from a continuation on the load, because the carrier is the host's and
        /// the load is not: a completion callback would have to know which state object to write to and
        /// whether it still exists. This is one null check on the common path.
        /// </remarks>
        private void PublishCatalogToSky()
        {
            if (SkyPlannerState is { ObjectDb: null } planner && CelestialObjectDB?.Value?.Value is { } db)
            {
                planner.ObjectDb = db;
            }
        }

        /// <summary>
        /// The site the sky is currently drawn for, and where that answer came from. Held across
        /// documents so a folder where only some frames carry the cards keeps its horizon; surfaced so
        /// the status bar can say which frame the horizon actually belongs to.
        /// </summary>
        public FrameSite SkySite { get; private set; } = FrameSite.Unknown;

        /// <summary>
        /// Whether the sky is drawn at the instant the shutter was open, rather than at the wall clock.
        /// </summary>
        public bool SkyIsAtCaptureTime { get; private set; }

        /// <summary>
        /// Points the sky at WHEN and WHERE the photograph was taken: the frame's own capture instant,
        /// so the planets, the comets and the horizon are where they were while the shutter was open,
        /// and its own site, so there is a horizon at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both are optional and degrade separately, which is the whole shape of this: with no instant
        /// the sky runs on the wall clock (the stars are in the right place either way -- it is the
        /// solar system and the horizon that move), and with no site there is no horizon and no Alt/Az
        /// grid and everything else is unchanged. Neither absence is an error state to report.
        /// </para>
        /// <para>
        /// The site is REMEMBERED across frames deliberately: stepping through a folder where one file
        /// carries the cards and the next does not would otherwise drop the horizon on an arrow key.
        /// <see cref="FrameSite.Source"/> says when that has happened.
        /// </para>
        /// </remarks>
        private void ApplyFrameContext(PlannerState planner)
        {
            if (_document is not { } document)
            {
                return;
            }

            var meta = document.UnstretchedImage.ImageMeta;

            var capturedAt = FrameSiteResolver.CapturedAt(in meta);
            SkyIsAtCaptureTime = capturedAt.HasValue;
            planner.PlanningDate = capturedAt;

            SkySite = FrameSiteResolver.Resolve(in meta, SkySite);
            planner.SiteLatitude = SkySite.IsKnown ? SkySite.LatitudeDeg : double.NaN;
            planner.SiteLongitude = SkySite.IsKnown ? SkySite.LongitudeDeg : double.NaN;
        }

        /// <summary>
        /// Draws the layer palette over everything the frame has drawn, when the sky is behind it.
        /// </summary>
        /// <remarks>
        /// <b>Without the key hints.</b> The palette doubles as the map's legend in a tab host, where
        /// those ten letters are the only way to reach the layers; here every one of them already
        /// means something else (<c>S</c> detects stars, <c>C</c> cycles the channel, <c>D</c> the
        /// demosaic), so printing them would be a row teaching a shortcut that does something quite
        /// different. Clicking the rows is the whole interface, which is what the palette was built to
        /// be in the first place.
        /// </remarks>
        private void RenderSkyPalette(RectF32 area)
        {
            if (SkyBackdropActive && SkyBackdrop is { } tab)
            {
                tab.RenderLayerPalette(area, includeKeyHints: false);
            }
        }
    }
}

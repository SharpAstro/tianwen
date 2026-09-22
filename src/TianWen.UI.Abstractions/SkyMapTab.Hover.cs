using System;
using DIR.Lib;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The hover highlight: a translucent wash under the object a click would select, resolved on a
    /// pointer move through the same search the click runs
    /// (<see cref="SkyMapSearchActions.ResolveHoverAtScreenPoint"/>).
    ///
    /// <para><b>It is a highlight, not hover selection.</b> The atlas's click-versus-hover question was
    /// settled for SELECTION in favour of the click (see <c>docs/plans/in-app-sky-atlas.md</c>); this
    /// answers the different question that left open -- on a field of overlapping markers, nothing told
    /// you which one a click would take, so finding out meant clicking and reading the panel. The wash
    /// says it before the press, and the press still decides.</para>
    ///
    /// <para><b>Drawn first of the annotation layers</b>, before planets, comets and the object overlay,
    /// so every marker and label it names draws ON TOP of it -- and so does the chrome, which is what
    /// makes a pointer resting over the search modal or the layer palette harmless without any of them
    /// having to claim the pointer: the wash is resolved against the sky behind the panel and then
    /// painted under it.</para>
    /// </summary>
    public partial class SkyMapTab<TSurface>
    {
        /// <summary>
        /// How far the pointer has to move before the hover is re-resolved, in DESIGN units. The
        /// resolve is a 3x3 spatial-cell walk over the deep-sky and composite grids; a mouse delivers
        /// moves at well over frame rate and most of them land on the same object, so the slop is what
        /// keeps a still-ish hand from paying for the walk on every one.
        /// </summary>
        private const float HoverResolveSlopPx = 2f;

        /// <summary>
        /// How far the viewing instant may drift before an EPHEMERIS hover target is dropped. A planet
        /// or comet resolved at one instant is at a different place at another, so a time scrub has to
        /// invalidate it -- but in live mode the clock advances every frame, and comparing exactly
        /// would drop the target before it was ever drawn. A minute of drift is invisible on screen;
        /// the smallest scrub step is ten.
        /// </summary>
        private static readonly TimeSpan HoverEphemerisStaleAfter = TimeSpan.FromMinutes(1);

        private float _hoverPointerX = float.NaN;
        private float _hoverPointerY = float.NaN;

        // At most one resolve per PAINTED frame, which is the bound that makes hover affordable at a
        // deep zoom. Measured by SkyMapHoverResolveBenchmarks (Release, win-arm64; an earlier figure
        // quoted here came from a Debug test run and was about 5x pessimistic):
        //
        //              over an object   over bare star field   (2026-09-20)       (before 2026-09-20)
        //   1 deg        ~9 us, 0 B        157 us, 0 B            (166 us, 27.6 KB)  (389 us, 225 KB)
        //   10 deg       ~9 us, 0 B        134 us, 0 B            (139 us, 27.6 KB)  (357 us, 225 KB)
        //   60 deg       ~9 us, 0 B        1.7 us, 0 B            (1.8 us,  288 B)
        //   170 deg      ~9 us, 0 B        1.6 us, 0 B            (1.7 us,  288 B)
        //
        // A resolve allocates nothing since 2026-09-21: both passes walk their nine cells through
        // IRaDecIndex.EnumerateCell (the struct RaDecCell) rather than the indexer, whose per-cell
        // List, wrapper and iterator were the 27.6 KB and whose boxed array enumerator was the 288 B.
        //
        // The paths differ by 16x (44x before), which the old single number hid, and the whole of
        // that difference is WHETHER THE STAR PASS RUNS. It is not a per-star cost that varies with
        // zoom: the nine index cells are derived from the unprojected pointer and are IDENTICAL at
        // every zoom, and hold 1094 candidates whatever the FOV. What the pass SPENT, per
        // SkyMapHoverResolveCostProbe (run with DOTNET_TieredCompilation=0, or it ranks by JIT
        // order): TryLookupByIndex for every one of those 1094 candidates was 320 of the 389 us and
        // 197 of the 225 KB -- six times the nine cell lookups (53 us) that fed it -- and over half
        // of THAT a constellation lookup precessing each star to B1875 through heap-allocated arrays,
        // for a field this resolver never reads. The star pass now reads a Tycho-2 candidate through
        // TryGetTycho2Star and looks up only the winner in full, and the precession and the base91
        // index decode allocate nothing for anyone. The cell scan reads only 6x what it keeps.
        //
        // What decides whether that is paid is the DSO pass, which short-circuits the star pass on a
        // match and floors its hit test at a FIXED 20 SCREEN PIXELS. That floor's footprint in SKY
        // runs 0.020 deg at 1 degree FOV to 4.200 at 170, so wide open it sweeps up something
        // catalogued and zoomed in it reaches nothing. At this pointing the nearest deep-sky entry is
        // HD 183919 at 0.409 deg, which is why the cliff lands between 10 and 60. The magnitude limit
        // is the minor term and runs the OTHER way: 1 degree is DEARER than 10 over identical cells,
        // because zoomed in it admits more stars to the projection. (That the cliff was the magnitude
        // limit is what this comment used to say; SkyMapHoverResolveCostProbe is what disproved it.)
        //
        // The throttle cannot be replaced by making the hover search cheaper than the click's,
        // because the highlight agreeing with the click is the entire point of the feature. The
        // click pays the same cost and always has -- once per press, where nobody can see it.
        private bool _hoverResolvedThisFrame;

        // The view the current HoverTarget was resolved against. Compared at draw time rather than
        // cleared at every call site that moves the view: a zoom, a pan, a scrub, a mode flip and a
        // deep link all move it, and a rule enforced in one place cannot be forgotten by the next one
        // added. Mirrors how the object overlay detects a moved view before placing labels.
        private double _hoverViewFov = double.NaN;
        private DateTimeOffset _hoverViewTime;

        /// <summary>Where the target projected when it was resolved, so the draw can tell whether the view moved it.</summary>
        private float _hoverTargetScreenX = float.NaN;
        private float _hoverTargetScreenY = float.NaN;

        /// <summary>
        /// How many times the pointer's position was resolved to an object. The observable for a hover
        /// test, for the reason <see cref="PrimOverlayGathers"/> is one for the gather: a re-resolve
        /// that lands on the same object draws the identical frame, so nothing in the pixels separates
        /// "resolved once and reused" from "resolved on every move".
        /// </summary>
        internal int HoverResolves { get; private set; }

        /// <summary>
        /// Resolves what the pointer is over, at most once per painted frame, and asks for the frame
        /// that shows it. Never consumes the move: a hover is something the map notices on the way
        /// past, not something it handles.
        /// </summary>
        private void TrackHoverPointer(float x, float y)
        {
            // Within the slop of the last resolve, the answer cannot have changed enough to matter.
            if (!float.IsNaN(_hoverPointerX)
                && MathF.Abs(x - _hoverPointerX) < HoverResolveSlopPx * DpiScale
                && MathF.Abs(y - _hoverPointerY) < HoverResolveSlopPx * DpiScale)
            {
                return;
            }

            // Already answered for the frame on screen. Nothing can SHOW a second answer before the
            // next paint, so the moves in between are free -- see _hoverResolvedThisFrame.
            if (_hoverResolvedThisFrame)
            {
                return;
            }

            _hoverPointerX = x;
            _hoverPointerY = y;

            if (_plannerState is not { ObjectDb: { } db } plannerState)
            {
                return;
            }

            _hoverResolvedThisFrame = true;

            HoverResolves++;
            // The CACHED pinned set, not a fresh one: this runs once per painted frame while the
            // pointer moves, and GetPinnedCatalogIndices allocates a set per call.
            var resolved = SkyMapSearchActions.ResolveHoverAtScreenPoint(
                State, db, _lastViewingTime, x, y, PinnedCatalogIndices(plannerState), plannerState.Comets);

            // Record the view alongside, so the draw can tell a target that is still current from one
            // the sky has since moved out from under: the field, and where the target itself landed.
            _hoverViewFov = State.FieldOfViewDeg;
            _hoverViewTime = _lastViewingTime;
            _hoverTargetScreenX = float.NaN;
            _hoverTargetScreenY = float.NaN;
            if (resolved is { } target)
            {
                var rect = State.LastContentRect;
                var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, State.FieldOfViewDeg);
                if (SkyMapProjection.ProjectWithMatrix(target.RA, target.Dec, State.CurrentViewMatrix, ppr,
                        rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f), out var tx, out var ty))
                {
                    _hoverTargetScreenX = tx;
                    _hoverTargetScreenY = ty;
                }
            }

            State.HoverTarget = resolved;

            // Every resolve asks for the frame, even one that landed on the same object and will
            // draw an identical wash. That is what closes the loop the throttle above opens: the
            // flag is cleared by a PAINT, so a resolve that scheduled no paint would be the last one
            // until something else happened to repaint, and the highlight would sit on whatever the
            // pointer was over minutes ago. Resolve -> frame -> clear -> resolve is self-limiting;
            // "resolve only when the answer changed" is not, and the pointer moving over the map is
            // an interaction, which is what the redraw gate is for rather than what it guards
            // against.
            State.NeedsRedraw = true;
        }

        /// <summary>Drops the hover target, if there is one, and asks for the frame without it.</summary>
        private void ClearHoverTarget()
        {
            _hoverPointerX = float.NaN;
            _hoverPointerY = float.NaN;
            if (State.HoverTarget is not null)
            {
                State.HoverTarget = null;
                State.NeedsRedraw = true;
            }
        }

        /// <summary>
        /// Paints the wash under the hovered object, or drops the target when the view has moved since
        /// it was resolved. One <c>FillEllipse</c>, which every renderer implements natively (the
        /// Vulkan and WebGL ones as a single distance-field quad), so this needs no instance stream, no
        /// cache key and no shader of its own.
        /// </summary>
        private void DrawHoverSpot(RectF32 contentRect, double pixelsPerRadian, float cx, float cy)
        {
            // Released FIRST, before every early return: this runs once per frame whether or not
            // there is anything to draw, and it is what re-opens the one-resolve-per-frame budget.
            _hoverResolvedThisFrame = false;

            if (State.HoverTarget is not { } hover)
            {
                return;
            }

            if (!SkyMapProjection.ProjectWithMatrix(hover.RA, hover.Dec, State.CurrentViewMatrix,
                    pixelsPerRadian, cx, cy, out var sx, out var sy))
            {
                return;
            }

            // Has the view moved the sky out from under the pointer since the resolve? Judged by the
            // TARGET's own movement on screen and by the field, never by the centre being
            // bit-identical: in Horizon mode the centre drifts with sidereal time on every frame,
            // so a centre test dropped the wash one frame after each resolve and it flashed under a
            // moving pointer (reported on the SMC, 2026-09-22). Sidereal drift moves a target by a
            // fraction of a pixel a frame; a pan or a zoom moves it by many. And a moved view is
            // RE-TESTED at the pointer's last position rather than merely dropped, so a wash under
            // a still pointer survives the drift: it re-resolves once the drift has added up to the
            // slop, which is once every few seconds, and lands on the same object.
            var slop = HoverResolveSlopPx * DpiScale;
            var viewMoved = State.FieldOfViewDeg != _hoverViewFov
                || float.IsNaN(_hoverTargetScreenX)
                || MathF.Abs(sx - _hoverTargetScreenX) > slop
                || MathF.Abs(sy - _hoverTargetScreenY) > slop;
            var ephemerisStale = hover.IsEphemeris
                && (_lastViewingTime - _hoverViewTime).Duration() > HoverEphemerisStaleAfter;
            if (viewMoved || ephemerisStale)
            {
                var px = _hoverPointerX;
                var py = _hoverPointerY;
                _hoverPointerX = float.NaN;
                _hoverPointerY = float.NaN;
                State.HoverTarget = null;
                if (float.IsNaN(px))
                {
                    return;
                }

                TrackHoverPointer(px, py);
                if (State.HoverTarget is not { } retested
                    || !SkyMapProjection.ProjectWithMatrix(retested.RA, retested.Dec, State.CurrentViewMatrix,
                        pixelsPerRadian, cx, cy, out sx, out sy))
                {
                    return;
                }

                hover = retested;
            }

            // The wash takes the object's own SHAPE where it has one: the ellipse the selection ring
            // and the [O] overlay draw for it, from the same solver, filled. A spot of the hit radius
            // over M31 read as a mark ON the galaxy rather than the galaxy lit, and it was clamped to
            // 36 px besides, so the bigger the object the less of it the wash said. The fill is the
            // affine ellipse on the abstraction (DIR.Lib 10.4): one call on every backend, as the
            // circle was. The circle stays for a shapeless object, and for a star, which the solver
            // declines however stray a shape it carries.
            var dpiScale = DpiScale;
            if (hover.Shape is { } shape
                && TrySolveShapeEllipse(hover.ObjType, in shape, hover.RA, hover.Dec, pixelsPerRadian, cx, cy,
                    out var semiAxisU, out var semiAxisV))
            {
                // Culled on the shape's own reach, for the reason the circle's margin below gives.
                var reach = MathF.Sqrt((semiAxisU.X * semiAxisU.X) + (semiAxisU.Y * semiAxisU.Y));
                if (sx < contentRect.X - reach || sx >= contentRect.X + contentRect.Width + reach
                    || sy < contentRect.Y - reach || sy >= contentRect.Y + contentRect.Height + reach)
                {
                    return;
                }

                Renderer.FillEllipse((sx, sy), semiAxisU, semiAxisV, OverlayEngine.HoverSpotColor);
                return;
            }

            // Culled with a MARGIN, not at the rect edge. An object is resolved by its shape radius,
            // so at a deep zoom into a large nebula -- NGC 7000, M 42, exactly the crowded fields this
            // feature is for -- the CENTRE is commonly off screen while the object fills the view. An
            // edge test dropped the wash there: hover resolved, nothing drew, and the click still
            // selected. The margin mirrors ProjectSkyMapCandidatesInto, which keeps a candidate up to
            // 100 px plus its semi-major axis outside the rect for the same reason; the renderer clips
            // whatever hangs over.
            var margin = OverlayEngine.HoverSpotMaxRadiusPx * DpiScale;
            if (sx < contentRect.X - margin || sx >= contentRect.X + contentRect.Width + margin
                || sy < contentRect.Y - margin || sy >= contentRect.Y + contentRect.Height + margin)
            {
                return;
            }

            var radius = Math.Clamp(
                hover.HitRadiusPx,
                OverlayEngine.HoverSpotMinRadiusPx * dpiScale,
                OverlayEngine.HoverSpotMaxRadiusPx * dpiScale);

            Renderer.FillEllipse(
                new RectInt(
                    new PointInt((int)MathF.Ceiling(sx + radius), (int)MathF.Ceiling(sy + radius)),
                    new PointInt((int)MathF.Floor(sx - radius), (int)MathF.Floor(sy - radius))),
                OverlayEngine.HoverSpotColor);
        }
    }
}

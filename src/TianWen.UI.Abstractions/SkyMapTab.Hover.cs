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

        /// <summary>
        /// The least time between two resolves while no frame is pending. The bound on the resolve rate
        /// once an unchanged answer stopped asking for a frame (see <see cref="TrackHoverPointer"/>):
        /// half a 60 Hz frame, so the highlight still follows a moving pointer within a frame, and at
        /// most 125 resolves a second however fast the mouse reports, which at the worst measured
        /// resolve (157 us, below) is 2 percent of one core.
        /// </summary>
        private static readonly TimeSpan HoverResolveMinInterval = TimeSpan.FromMilliseconds(8);

        private float _hoverPointerX = float.NaN;
        private float _hoverPointerY = float.NaN;

        // When the last resolve ran, on the app clock; 0 before the first. See HoverResolveMinInterval.
        private long _hoverResolveTimestamp;

        // At most one resolve per PAINTED frame while a frame is pending, and at most one per
        // HoverResolveMinInterval otherwise: together, the bound that makes hover affordable at a
        // deep zoom. Measured by SkyMapHoverResolveBenchmarks (Release, win-arm64; an earlier figure
        // quoted here came from a Debug test run and was about 5x pessimistic):
        //
        //              over an object   over bare star field   (2026-09-20)       (before 2026-09-20)
        //   1 deg        ~9 us, 0 B        157 us, 0 B            (166 us, 27.6 KB)  (389 us, 225 KB)
        //   10 deg       ~9 us, 0 B        134 us, 0 B            (139 us, 27.6 KB)  (357 us, 225 KB)
        //   60 deg       ~9 us, 0 B        1.7 us, 0 B            (1.8 us,  288 B)
        //   170 deg      ~9 us, 0 B        1.6 us, 0 B            (1.7 us,  288 B)
        //
        // Re-measured 2026-09-28, main against the tiered pick in SkyMapSearchActions and equal within
        // noise: over an object 26-27 us at every field, over bare star field 170 / 153 / 17 / 16 us at
        // 1 / 10 / 60 / 170 degrees, still 0 B. What grew since the table above is not attributed yet.
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
        //
        // True from a resolve that CHANGED the answer, and so asked for a frame, until that frame paints.
        private bool _hoverFramePending;

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
        public int HoverResolves { get; private set; }

        /// <summary>
        /// How many resolves changed the answer, each of which asks for a frame unless it changes nothing on screen
        /// (onto an object whose wash is withheld, from bare sky). The observable for "a hover
        /// that changes nothing costs nothing": <see cref="SkyMapState.NeedsRedraw"/> cannot answer it,
        /// because a render sets that flag for reasons of its own (a zoom does).
        /// </summary>
        public int HoverFrameRequests { get; private set; }

        /// <summary>
        /// Whether the host repaints the rectangle a pointer move STATES and nothing else
        /// (<see cref="IPointerMoveChange"/>, #2130). The GUI does. A changed hover answer then states the extent of
        /// the wash it takes off and the one it puts on, and the frame repaints that alone. Elsewhere it raises
        /// <see cref="SkyMapState.NeedsRedraw"/>, as it always did, and the whole map is drawn again.
        /// </summary>
        /// <remarks>
        /// Zoomed out, nearly every point of the map is within the pick tolerance of a drawn star, so a moving
        /// pointer changes the answer at almost every resolve: 205 of 212 in five seconds at a 135 degree field,
        /// each a repaint of the whole map, which held the Adreno X1-85 at 40 percent (2026-11-14).
        /// </remarks>
        public bool HostRepaintsStatedMoves { get; set; }

        /// <summary>
        /// Set by the host for a frame that repaints part of the window: the map is drawn at the instant of the
        /// last frame, so what is repainted agrees with what is kept around it.
        /// </summary>
        public bool HoldViewingTime { get; set; }

        // Where the last frame painted the wash, or null when it painted none: what a changed answer has to repaint
        // to take it off.
        private RectF32? _hoverDrawnBounds;

        // The extent of the hover changes no frame has drawn yet, for the host to take.
        private RectF32? _hoverChanged;

        /// <inheritdoc/>
        public RectF32? TakePointerMoveChanged()
        {
            var changed = _hoverChanged;
            _hoverChanged = null;
            return changed;
        }

        /// <summary>
        /// Resolves what the pointer is over and, when the answer CHANGED, asks for the frame that
        /// shows it. Never consumes the move: a hover is something the map notices on the way past,
        /// not something it handles.
        /// </summary>
        /// <param name="x">Pointer position, device pixels.</param>
        /// <param name="y">Pointer position, device pixels.</param>
        /// <param name="retestForMovedView">The draw's re-test of a target the view moved (sidereal
        /// drift in Horizon mode, a zoom): exempt from the clock bound, which would otherwise leave a
        /// stale target standing, and already bounded by the slop the drift has to add up to.</param>
        private void TrackHoverPointer(float x, float y, bool retestForMovedView = false)
        {
            // Within the slop of the last resolve, the answer cannot have changed enough to matter.
            if (!float.IsNaN(_hoverPointerX)
                && MathF.Abs(x - _hoverPointerX) < HoverResolveSlopPx * DpiScale
                && MathF.Abs(y - _hoverPointerY) < HoverResolveSlopPx * DpiScale)
            {
                return;
            }

            // A changed answer is already on its way to the screen. Nothing can SHOW a second answer
            // before that paint, so the moves in between are free -- see _hoverFramePending.
            if (_hoverFramePending)
            {
                return;
            }

            // And with nothing pending, no faster than the clock bound, since a resolve that finds the
            // same answer no longer schedules the paint that used to be its throttle.
            if (!retestForMovedView && _timeProvider is { } clock && _hoverResolveTimestamp != 0
                && clock.GetElapsedTime(_hoverResolveTimestamp) < HoverResolveMinInterval)
            {
                return;
            }

            _hoverPointerX = x;
            _hoverPointerY = y;

            if (_plannerState is not { ObjectDb: { } db } plannerState)
            {
                return;
            }

            _hoverResolveTimestamp = _timeProvider?.GetTimestamp() ?? 0;

            HoverResolves++;
            // The CACHED pinned set, not a fresh one: this runs once per painted frame while the
            // pointer moves, and GetPinnedCatalogIndices allocates a set per call.
            var resolved = SkyMapSearchActions.ResolveHoverAtScreenPoint(
                State, db, _lastViewingTime, x, y, PinnedCatalogIndices(plannerState), plannerState.Comets);

            // Record the view alongside, so the draw can tell a target that is still current from one
            // the sky has since moved out from under: the field, and where the target itself landed.
            var screenX = float.NaN;
            var screenY = float.NaN;
            if (resolved is { } target)
            {
                var rect = State.LastContentRect;
                var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, State.FieldOfViewDeg);
                if (SkyMapProjection.ProjectWithMatrix(target.RA, target.Dec, State.CurrentViewMatrix, ppr,
                        rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f), out var tx, out var ty))
                {
                    screenX = tx;
                    screenY = ty;
                }
            }

            // Only a CHANGED answer asks for a frame: another object, or onto or off one. A resolve
            // that lands on the object already washed, or on the bare sky already unwashed, would
            // draw the identical frame, and it used to ask for one anyway. Every pointer move over
            // the atlas then repainted the whole map -- Milky Way, horizon fill, grids, some twenty
            // thousand stars, overlays and labels -- at display rate, which held the Adreno X1-85 at
            // up to 70 percent for a hover that changed nothing on screen (2026-09-23). It was done
            // because the per-frame budget above was released only by a paint, so a resolve that
            // scheduled none would have been the last one; the clock bound is what releases it now.
            if (IsSameHoverObject(State.HoverTarget, resolved))
            {
                // The same object: refresh its record (a later instant, a moved view).
                SetDisplayedHover(resolved, State.FieldOfViewDeg, _lastViewingTime, screenX, screenY);
                return;
            }

            ShowHover(resolved, State.FieldOfViewDeg, _lastViewingTime, screenX, screenY, duringDraw: retestForMovedView);
        }

        /// <summary>Puts a changed answer on screen, and asks for the frame that shows it.</summary>
        /// <param name="duringDraw">The draw's own re-test of a moved view: the frame being drawn has its region
        /// already, so the change asks for a whole one.</param>
        private void ShowHover(SkyMapHoverTarget? target, double fov, DateTimeOffset viewTime, float screenX, float screenY,
            bool duringDraw = false)
        {
            SetDisplayedHover(target, fov, viewTime, screenX, screenY);
            HoverFrameRequests++;
            _hoverFramePending = true;
            if (!duringDraw && HostRepaintsStatedMoves)
            {
                StateHoverChange(target, screenX, screenY);
                return;
            }

            State.NeedsRedraw = true;
        }

        /// <summary>
        /// Records what a changed answer changes on screen: the wash the last frame painted and the one the next
        /// will, which is all a hover draws. When neither exists (onto an object whose wash is withheld, from bare
        /// sky) the screen stays as it is, and no frame is owed.
        /// </summary>
        private void StateHoverChange(SkyMapHoverTarget? target, float screenX, float screenY)
        {
            RectF32? next = null;
            if (target is { } hover && !float.IsNaN(screenX))
            {
                var rect = State.LastContentRect;
                var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, State.FieldOfViewDeg);
                if (TryPlaceWash(in hover, screenX, screenY, rect, ppr,
                        rect.X + (rect.Width * 0.5f), rect.Y + (rect.Height * 0.5f), out var wash))
                {
                    next = wash.Bounds(rect);
                }
            }

            var changed = (_hoverDrawnBounds, next) switch
            {
                ({ } was, { } now) => Union(was, now),
                ({ } was, null) => was,
                (null, { } now) => now,
                _ => null as RectF32?,
            };
            if (changed is not { } extent)
            {
                // Nothing to paint, so no paint will come to re-open the resolve budget.
                _hoverFramePending = false;
                return;
            }

            _hoverChanged = _hoverChanged is { } held ? Union(held, extent) : extent;
        }

        private static RectF32 Union(RectF32 a, RectF32 b)
        {
            var x0 = MathF.Min(a.X, b.X);
            var y0 = MathF.Min(a.Y, b.Y);
            var x1 = MathF.Max(a.X + a.Width, b.X + b.Width);
            var y1 = MathF.Max(a.Y + a.Height, b.Y + b.Height);
            return new RectF32(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// What the wash paints: the object's own ellipse (<see cref="Shaped"/>), or a circle of <see cref="Radius"/>.
        /// Placed ONCE, by <see cref="TryPlaceWash"/>, for the paint and for the extent a changed answer repaints.
        /// </summary>
        private readonly record struct HoverWash(
            float X, float Y, bool Shaped, (float X, float Y) SemiAxisU, (float X, float Y) SemiAxisV, float Radius)
        {
            /// <summary>The wash's extent inside <paramref name="contentRect"/>, with room for its soft edge; null
            /// when none of it is inside.</summary>
            public RectF32? Bounds(RectF32 contentRect)
            {
                const float Edge = 2f;
                var halfW = Shaped ? MathF.Sqrt((SemiAxisU.X * SemiAxisU.X) + (SemiAxisV.X * SemiAxisV.X)) : Radius;
                var halfH = Shaped ? MathF.Sqrt((SemiAxisU.Y * SemiAxisU.Y) + (SemiAxisV.Y * SemiAxisV.Y)) : Radius;
                var x0 = MathF.Max(MathF.Floor(X - halfW - Edge), contentRect.X);
                var y0 = MathF.Max(MathF.Floor(Y - halfH - Edge), contentRect.Y);
                var x1 = MathF.Min(MathF.Ceiling(X + halfW + Edge), contentRect.X + contentRect.Width);
                var y1 = MathF.Min(MathF.Ceiling(Y + halfH + Edge), contentRect.Y + contentRect.Height);
                return x1 > x0 && y1 > y0 ? new RectF32(x0, y0, x1 - x0, y1 - y0) : null;
            }
        }

        private void SetDisplayedHover(SkyMapHoverTarget? target, double fov, DateTimeOffset viewTime, float screenX, float screenY)
        {
            State.HoverTarget = target;
            _hoverViewFov = fov;
            _hoverViewTime = viewTime;
            _hoverTargetScreenX = screenX;
            _hoverTargetScreenY = screenY;
        }

        /// <summary>
        /// Whether two answers name the same object, which is all the wash depends on: its shape and
        /// place come from the object, so a new RA/Dec for the same ephemeris body (a later instant)
        /// or a different hit radius at the same view draws the same wash.
        /// </summary>
        private static bool IsSameHoverObject(SkyMapHoverTarget? a, SkyMapHoverTarget? b)
            => a is { } x
                ? b is { } y && x.Index == y.Index && x.IsEphemeris == y.IsEphemeris
                : b is null;

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
            _hoverFramePending = false;
            _hoverDrawnBounds = null;

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
                if (float.IsNaN(px))
                {
                    State.HoverTarget = null;
                    return;
                }

                // The old target stays in place for the re-test to compare against, so the drift
                // re-test that lands on the same object -- every few seconds in Horizon mode under a
                // still pointer -- asks for no frame; clearing it first made every one a "change".
                TrackHoverPointer(px, py, retestForMovedView: true);
                if (State.HoverTarget is not { } retested
                    || !SkyMapProjection.ProjectWithMatrix(retested.RA, retested.Dec, State.CurrentViewMatrix,
                        pixelsPerRadian, cx, cy, out sx, out sy))
                {
                    return;
                }

                hover = retested;
            }

            if (!TryPlaceWash(in hover, sx, sy, contentRect, pixelsPerRadian, cx, cy, out var wash))
            {
                return;
            }

            _hoverDrawnBounds = wash.Bounds(contentRect);
            if (wash.Shaped)
            {
                Renderer.FillEllipse((wash.X, wash.Y), wash.SemiAxisU, wash.SemiAxisV, OverlayEngine.HoverSpotColor);
                return;
            }

            Renderer.FillEllipse(
                new RectInt(
                    new PointInt((int)MathF.Ceiling(wash.X + wash.Radius), (int)MathF.Ceiling(wash.Y + wash.Radius)),
                    new PointInt((int)MathF.Floor(wash.X - wash.Radius), (int)MathF.Floor(wash.Y - wash.Radius))),
                OverlayEngine.HoverSpotColor);
        }

        /// <summary>
        /// Where and how large the wash for <paramref name="hover"/> is, given where the object projected
        /// (<paramref name="sx"/>, <paramref name="sy"/>). False when no wash is painted for it: off the map, or an
        /// object that fills the view.
        /// </summary>
        private bool TryPlaceWash(in SkyMapHoverTarget hover, float sx, float sy, RectF32 contentRect,
            double pixelsPerRadian, float cx, float cy, out HoverWash wash)
        {
            wash = default;

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
                    return false;
                }

                // An object that FILLS the view is the view, not something in it: zoomed into the LMC
                // (646' across at a 4.5 degree field) every pointer position was inside it, so the whole
                // screen washed under any hover, which highlights nothing and reads as a flash (reported
                // 2026-09-23). Judged on the ellipse's SHORT semi-axis against half the view's short
                // side, i.e. whether it covers the view across its narrow direction. Only the paint is
                // withheld: the object is still the resolver's answer, so a click still selects it and
                // the highlight never names a different object from the click.
                var semiMinor = MathF.Min(reach,
                    MathF.Sqrt((semiAxisV.X * semiAxisV.X) + (semiAxisV.Y * semiAxisV.Y)));
                if (semiMinor >= MathF.Min(contentRect.Width, contentRect.Height) * 0.5f)
                {
                    return false;
                }

                wash = new HoverWash(sx, sy, Shaped: true, semiAxisU, semiAxisV, Radius: 0f);
                return true;
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
                return false;
            }

            var radius = Math.Clamp(
                hover.HitRadiusPx,
                OverlayEngine.HoverSpotMinRadiusPx * dpiScale,
                OverlayEngine.HoverSpotMaxRadiusPx * dpiScale);

            wash = new HoverWash(sx, sy, Shaped: false, default, default, radius);
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // Full toolbar button set (label, action, group) -- the standalone FITS viewer (tianwen-fits) shows
        // all of these. Group breaks insert extra spacing: 0 file, 1 stretch, 2 channel/debayer/curves,
        // 3 zoom, 4 astrometry/stars/colour.
        private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> CoreToolbarButtons =
        [
            ("Open", ToolbarAction.Open, 0),
            ("Save", ToolbarAction.Save, 0),
            ("STF", ToolbarAction.StretchToggle, 1),
            ("Link", ToolbarAction.StretchLink, 1),
            ("Params", ToolbarAction.StretchParams, 1),
            ("Channel", ToolbarAction.Channel, 2),
            ("Debayer", ToolbarAction.Debayer, 2),
            ("Boost", ToolbarAction.CurvesBoost, 2),
            ("HDR", ToolbarAction.Hdr, 2),
            ("A/B", ToolbarAction.Compare, 2),
            // One control, not two: the label is computed per frame (Fit / 1:1 / a percentage), so the
            // text here is only the measurement seed and the widest state it has to fit.
            ("Fit", ToolbarAction.Zoom, 3),
            ("Solve", ToolbarAction.PlateSolve, 4),
            ("Grid", ToolbarAction.Grid, 4),
            ("Objects", ToolbarAction.Overlays, 4),
            ("Stars", ToolbarAction.Stars, 4),
            ("Calibrate", ToolbarAction.ColorCalibrate, 4),
            ("NeutBg", ToolbarAction.BackgroundNeutralize, 4),
            ("SPCC", ToolbarAction.SpccCalibrate, 4),
        ];

        // The default set: the core plus the trailing help button. "?" is appended HERE rather than
        // living in the core table so every variant below can keep it last -- see the Enhance table,
        // which used to Add() past it and so ran group 5 before group 4.
        private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> DefaultToolbarButtons =
            CoreToolbarButtons.Add(("?", ToolbarAction.Shortcuts, 5));

        // The full set plus the AI "Enhance" button (group 4). A separate static array (not an
        // append-per-frame) keeps the per-frame render + hit-test loops allocation-free. Selected by
        // ToolbarButtons when the host sets EnhanceAvailable.
        private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> DefaultToolbarButtonsWithEnhance =
            CoreToolbarButtons.Add(("Enhance", ToolbarAction.Enhance, 4)).Add(("?", ToolbarAction.Shortcuts, 5));

        /// <summary>
        /// Set by the host when an AI <see cref="TianWen.Lib.Imaging.Enhancement.SharpenPipeline"/> is wired
        /// (e.g. tianwen-fits with AddRcAstroAi). When false the Enhance button is hidden entirely -- per the
        /// "hide what can never apply" rule -- so a viewer with no AI services never shows a dead button.
        /// </summary>
        public bool EnhanceAvailable { get; set; }

        /// <summary>
        /// The toolbar buttons this viewer surfaces, in order. The base (tianwen-fits) shows the full set
        /// (plus Enhance when <see cref="EnhanceAvailable"/>); a subclass embedding the viewer for a narrower
        /// job overrides this to a relevant subset, so buttons that can never apply (e.g. plate solve / star
        /// detection / colour calibration on a featureless planetary disk) are <b>hidden</b> rather than
        /// shown-but-disabled. The render + hit-test loops read this property, so both stay in lock-step.
        /// </summary>
        protected virtual ImmutableArray<(string Label, ToolbarAction Action, int Group)> ToolbarButtons =>
            EnhanceAvailable ? DefaultToolbarButtonsWithEnhance : DefaultToolbarButtons;

        /// <summary>
        /// Actions pulled out of the left-to-right run and laid out from the RIGHT edge instead, in
        /// visual order.
        /// </summary>
        /// <remarks>
        /// <para>Help earns a fixed corner. Its x must not depend on how wide "Auto" or
        /// "Stars: 5893" happened to render this frame, or the one button whose whole job is to be
        /// findable becomes the one that moves whenever something else relabels.</para>
        /// <para>The block is <b>measured before the left run is placed</b>, so the left run stops
        /// short of it. An overlapped button is worse than an absent one: it is still registered, so
        /// it silently takes the click that was aimed at whatever is drawn on top of it.</para>
        /// </remarks>
        protected virtual ImmutableArray<ToolbarAction> RightAlignedToolbarActions { get; } =
            [ToolbarAction.Shortcuts];


        // -----------------------------------------------------------------------
        // Toolbar
        // -----------------------------------------------------------------------

        private void RenderToolbar(AstroImageDocument? document, ViewerState state)
        {
            var tb = _layout.Toolbar;
            FillRect(tb.X, tb.Y, tb.Width, tb.Height, ViewerTheme.ToolbarBg);

            _toolbarButtonBounds.Clear();
            // Cleared per frame and refilled from the SAME rect the button paints, so the tooltip can
            // never point somewhere the button is not. No ViewerState field: the toolbar already
            // derives `hovered` per button per frame (it drives the highlight), so the tooltip is a
            // by-product of that, not new state to keep in sync.
            _hoveredTooltip = null;

            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            LayOutToolbarButtons(tb, document, state);
            PaintToolbarButtons(state);
        }

        /// <summary>One toolbar button, already positioned.</summary>
        private readonly record struct ToolbarButtonBox(
            string Label, ToolbarAction Action, float MarkWidth, RectF32 Rect, bool Enabled, bool Active);

        /// <summary>
        /// A button whose label and width are known but whose position is not yet. Measured once per
        /// frame, then read by both the wrap walk and the placement pass -- so the run is walked twice
        /// but no label is ever measured twice.
        /// </summary>
        private readonly record struct ToolbarMeasure(
            string Label, ToolbarAction Action, int Group, float MarkWidth, float Width);

        // Per-frame scratch, all reused: this runs every frame on the render thread and nothing in it
        // outlives the frame.
        private readonly List<ToolbarButtonBox> _toolbarBoxes = new();
        private readonly List<ToolbarMeasure> _toolbarLeftRun = new();
        private readonly List<ToolbarMeasure> _toolbarRightRun = new();

        /// <summary>Where the wrap walk put each <see cref="_toolbarLeftRun"/> entry, by index. Shorter
        /// than the run when buttons had to be dropped.</summary>
        private readonly List<(int Row, float X)> _toolbarSlots = new();

        /// <summary>
        /// Rows the toolbar occupies this frame. 1 unless the run did not fit, which is the whole point:
        /// a narrow window wraps rather than silently dropping the buttons that did not fit.
        /// </summary>
        private int _toolbarRows = 1;

        /// <summary>
        /// How far the run may wrap. Two, because that is what a narrow window needs and because the band
        /// is taken out of the image pane -- an unbounded wrap turns a small window into a toolbar with a
        /// picture attached. Past this the tail is still dropped, and at that width the open question is
        /// which panel should yield instead (docs/todo/ui.md), not how tall the toolbar may grow.
        /// </summary>
        private const int MaxToolbarRows = 2;

        /// <summary>
        /// Measures the toolbar run and decides how many rows it needs, BEFORE the layout pass, because
        /// the band height is an input to that pass. Nothing is positioned here -- positions derive from
        /// the arranged band, which does not exist yet.
        /// </summary>
        private void PrepareToolbarLayout(AstroImageDocument? document, ViewerState state)
        {
            _toolbarLeftRun.Clear();
            _toolbarRightRun.Clear();
            _toolbarSlots.Clear();
            _toolbarRows = 1;

            // No toolbar at all (an embedded chromeless preview), or no font to measure with -- in which
            // case RenderToolbar bails before laying anything out. Either way the band keeps its one-row
            // height, so an unconfigured widget is byte-identical to before.
            if (state.HideChrome || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var buttons = ToolbarButtons;
            var rightAligned = RightAlignedToolbarActions;
            for (var i = 0; i < buttons.Length; i++)
            {
                var entry = buttons[i];
                var (label, markW, width) = MeasureToolbarButton(entry, document, state);
                var measured = new ToolbarMeasure(label, entry.Action, entry.Group, markW, width);
                (rightAligned.Contains(entry.Action) ? _toolbarRightRun : _toolbarLeftRun).Add(measured);
            }

            // The toolbar is a Top dock at the root, so its band is as wide as the whole content region.
            _toolbarRows = WalkToolbarRows(ContentRegion.Width, MaxToolbarRows);
        }

        /// <summary>Width the right-aligned block reserves, including the gaps between its members.</summary>
        private float RightBlockWidth()
        {
            var width = 0f;
            foreach (var measure in _toolbarRightRun)
            {
                width += measure.Width;
            }
            if (_toolbarRightRun.Count > 1)
            {
                width += ButtonSpacing * (_toolbarRightRun.Count - 1);
            }
            return width;
        }

        /// <summary>
        /// Walks the left run over <paramref name="bandWidth"/>, filling <see cref="_toolbarSlots"/> with
        /// each button row and band-relative x, and returns the rows used.
        /// </summary>
        /// <remarks>
        /// Pure arithmetic over the already-measured widths, which is what lets the row COUNT be asked
        /// before the layout pass and the POSITIONS be derived after it from the arranged band, without
        /// measuring twice or letting the two answers drift. <paramref name="maxRows"/> is the cap: the
        /// placement pass passes the rows that were actually reserved, so it can never paint a row the
        /// band has no room for.
        /// </remarks>
        private int WalkToolbarRows(float bandWidth, int maxRows)
        {
            _toolbarSlots.Clear();

            var rightWidth = RightBlockWidth();
            // The right block sits on the first row, so only that row must stop short of it.
            var firstRowLimit = _toolbarRightRun.Count > 0
                ? bandWidth - PanelPadding - rightWidth - ButtonGroupSpacing
                : bandWidth - PanelPadding;
            var wrappedRowLimit = bandWidth - PanelPadding;

            var row = 0;
            var x = PanelPadding;
            var prevGroup = -1;

            for (var i = 0; i < _toolbarLeftRun.Count; i++)
            {
                var measure = _toolbarLeftRun[i];
                var limit = row == 0 ? firstRowLimit : wrappedRowLimit;
                var gap = prevGroup >= 0 && measure.Group != prevGroup ? ButtonGroupSpacing : 0f;

                // Out of room on this row. Wrap -- unless the row is still empty, in which case the
                // button fits no row at all and another one would not help.
                if (x + gap + measure.Width > limit && row + 1 < maxRows && x > PanelPadding)
                {
                    row++;
                    x = PanelPadding;
                    limit = wrappedRowLimit;
                    // No leading group gap: a wrapped row starts flush under the one above, and the row
                    // break already says everything the gap would have.
                    gap = 0f;
                }

                x += gap;
                prevGroup = measure.Group;

                // Out of rows as well as out of room. Drop it entirely -- no paint, no hit -- rather than
                // let it slide under the right block, where it would still be registered and would eat
                // the click aimed at what is drawn over it. Everything after is further along, so stop.
                if (x + measure.Width > limit)
                {
                    break;
                }

                _toolbarSlots.Add((row, x));
                x += measure.Width + ButtonSpacing;
            }

            return row + 1;
        }

        /// <summary>
        /// The ONE pass that decides where each toolbar button goes. The paint reads these rects and
        /// re-derives nothing, and <see cref="HitTestToolbar"/> answers from the rects that were
        /// actually painted -- so "draw == hit" holds by construction rather than by two loops being
        /// kept in step by hand. The second loop is what let the RGGB swatch widen a button in the
        /// paint while the hover query still measured the old width.
        /// </summary>
        private void LayOutToolbarButtons(in RectF32 tb, AstroImageDocument? document, ViewerState state)
        {
            _toolbarBoxes.Clear();

            // Capped at the rows PrepareToolbarLayout reserved, so a band narrower than the region it was
            // measured against drops the tail rather than painting outside itself.
            WalkToolbarRows(tb.Width, _toolbarRows);

            var rowH = tb.Height / Math.Max(_toolbarRows, 1);
            var btnH = rowH - ButtonSpacing * 2;

            for (var i = 0; i < _toolbarSlots.Count; i++)
            {
                var (row, x) = _toolbarSlots[i];
                var measure = _toolbarLeftRun[i];
                var y = tb.Y + row * rowH + ButtonSpacing;
                AddToolbarBox(measure, new RectF32(tb.X + x, y, measure.Width, btnH), document, state);
            }

            // The right block, in the table own order, on the FIRST row from the start its measured width
            // reserved. Deliberately not the last row: help earns a fixed corner, and a corner that moves
            // down whenever the wrap count changes is the drift the pin exists to prevent.
            var rx = tb.Right - PanelPadding - RightBlockWidth();
            var ry = tb.Y + ButtonSpacing;
            foreach (var measure in _toolbarRightRun)
            {
                AddToolbarBox(measure, new RectF32(rx, ry, measure.Width, btnH), document, state);
                rx += measure.Width + ButtonSpacing;
            }
        }

        private void AddToolbarBox(in ToolbarMeasure measure, in RectF32 rect,
            AstroImageDocument? document, ViewerState state)
            => _toolbarBoxes.Add(new ToolbarButtonBox(measure.Label, measure.Action, measure.MarkWidth, rect,
                IsToolbarButtonEnabled(measure.Action, document),
                IsToolbarButtonActive(measure.Action, document, state)));

        /// <summary>Resolves a button's label and the width it needs. The one place a button width is
        /// computed.</summary>
        private (string Label, float MarkWidth, float Width) MeasureToolbarButton(
            (string Label, ToolbarAction Action, int Group) entry, AstroImageDocument? document, ViewerState state)
        {
            var label = GetToolbarButtonLabel(entry.Label, entry.Action, document, state);
            var markW = ToolbarMarkWidth(entry.Action, state);
            return (label, markW,
                markW + MarkGap(markW, label) + MeasureText(label, ToolbarFontSize) + ButtonPaddingH * 2);
        }

        /// <summary>
        /// The space between a mark and the label after it, which exists only when there is both.
        /// </summary>
        /// <remarks>
        /// Derived here rather than folded into the mark width, because a mark that REPLACES its label
        /// (Channel) would otherwise carry a trailing gap and sit visibly off-centre in its own button.
        /// Measure and paint both call this, so the two cannot drift -- the class of bug that puts a
        /// label one gap away from where its button was sized for it.
        /// </remarks>
        private float MarkGap(float markWidth, string label)
            => markWidth > 0f && label.Length > 0 ? ButtonPaddingH : 0f;

        private void PaintToolbarButtons(ViewerState state)
        {
            var mouse = state.MouseScreenPosition;

            foreach (var box in _toolbarBoxes)
            {
                var r = box.Rect;
                // Centred in the button OWN rect, not in the band: with a wrapped toolbar the band holds
                // two rows and its centre is the gap between them.
                var textY = r.Y + (r.Height - ToolbarFontSize) / 2f;
                var hovered = box.Enabled && !state.OverlayOwnsPointer && r.Contains(mouse.X, mouse.Y);

                if (!box.Enabled)
                {
                    FillRect(r.X, r.Y, r.Width, r.Height, ToolbarButtonDisabledBg);
                }
                else if (box.Active && hovered)
                {
                    // Active + hover = the brightest selection blue (matches ViewerTheme's selected role).
                    FillRect(r.X, r.Y, r.Width, r.Height, ViewerTheme.Palette.Selection);
                }
                else if (box.Active)
                {
                    FillRect(r.X, r.Y, r.Width, r.Height, ToolbarButtonActiveBg);
                }
                else if (hovered)
                {
                    FillRect(r.X, r.Y, r.Width, r.Height, ToolbarButtonHoverBg);
                }
                else
                {
                    FillRect(r.X, r.Y, r.Width, r.Height, ToolbarButtonBg);
                }

                var textBrightness = box.Enabled ? 0.9f : 0.45f;
                var inkColor = RGBAColor32.FromFloat(textBrightness, textBrightness, textBrightness, 1f);

                if (box.MarkWidth > 0f)
                {
                    DrawToolbarMark(box.Action, r.X + ButtonPaddingH, r.Y, r.Height, state, inkColor, box.Enabled);
                }

                DrawText(box.Label, r.X + ButtonPaddingH + box.MarkWidth + MarkGap(box.MarkWidth, box.Label),
                    textY, ToolbarFontSize, inkColor);

                if (hovered && GetToolbarButtonTooltip(box.Action, state, _document) is { Length: > 0 } tip)
                {
                    _hoveredTooltip = (tip, r.X, r.Bottom, null);
                }

                if (box.Enabled)
                {
                    RegisterClickable(r.X, r.Y, r.Width, r.Height, new HitResult.ButtonHit(box.Action.ToString()));
                    // Capture rect so left-click can anchor the dropdown beneath the
                    // button (see OpenToolbarDropdown). Only enabled buttons can be
                    // clicked, so we only need their bounds.
                    _toolbarButtonBounds[box.Action] = r;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Toolbar dropdowns: single shared overlay (only one open at a time)
        // -----------------------------------------------------------------------

        /// <summary>Captured bounds of each enabled toolbar button this frame; 
        /// used as the anchor when opening that button's dropdown.</summary>
        private readonly Dictionary<ToolbarAction, RectF32> _toolbarButtonBounds = new();

        /// <summary>
        /// The rect a toolbar button was PAINTED at this frame. Test seam (InternalsVisibleTo), because
        /// alignment is the one thing a pixel assertion cannot check: a rendered bar cannot tell a
        /// button that merely sits near the right edge from one that is pinned to it, nor prove that it
        /// stayed put when a neighbour relabelled.
        /// </summary>
        internal bool TryGetPaintedToolbarRect(ToolbarAction action, out RectF32 rect)
            => _toolbarButtonBounds.TryGetValue(action, out rect);

        /// <summary>Every button placed this frame, in layout order. Test seam.</summary>
        internal IEnumerable<(ToolbarAction Action, RectF32 Rect)> PaintedToolbarButtons
        {
            get
            {
                foreach (var box in _toolbarBoxes)
                {
                    yield return (box.Action, box.Rect);
                }
            }
        }

        /// <summary>Cycle order + dropdown order for the stretch-mode selector.
        /// Mirrors <see cref="ViewerActions.StretchLinkModes"/> 1:1 so the click
        /// handler can index back into the enum array.</summary>
        private static readonly ImmutableArray<string> StretchLinkModeLabels = BuildLabels(
            ViewerActions.StretchLinkModes, m => m.ToString());

        /// <summary>Channel-view selector: Composite/Red/Green/Blue. Only
        /// surfaced for 3+ channel images (gated by <see cref="IsToolbarButtonEnabled"/>).</summary>
        private static readonly ChannelView[] ChannelViewOrder =
            [ChannelView.Composite, ChannelView.Red, ChannelView.Green, ChannelView.Blue];

        private static readonly ImmutableArray<string> ChannelViewLabels = BuildLabels(
            ChannelViewOrder, v => v switch { ChannelView.Composite => "RGB", _ => v.ToString() });

        /// <summary>Debayer-algorithm selector, all algorithms always shown. The click handler
        /// indexes this array directly, so the order is independent of the enum's numeric values.
        /// MHC sits next to the other Bayer-to-RGB algorithms; for the GPU live (RawBayer) path it
        /// and VNG/AHD all resolve to the shader's MHC demosaic (see <see cref="GpuDebayerMode"/>).</summary>
        private static readonly DebayerAlgorithm[] DebayerAlgorithmOrder =
            [DebayerAlgorithm.None, DebayerAlgorithm.BilinearMono, DebayerAlgorithm.MHC, DebayerAlgorithm.VNG, DebayerAlgorithm.AHD];

        private static readonly ImmutableArray<string> DebayerLabels = BuildLabels(
            DebayerAlgorithmOrder, a => a.DisplayName);

        /// <summary>Stretch-parameter preset labels: 8 (Factor, ShadowsClipping) presets.</summary>
        private static readonly ImmutableArray<string> StretchParamsLabels = BuildLabels(
            StretchParameters.Presets, p => p.ToString());

        /// <summary>Curves-boost preset labels: 0/25/50/100/150 %.</summary>
        private static readonly ImmutableArray<string> CurvesBoostLabels = BuildLabels(
            ViewerState.CurvesBoostPresets, b => b > 0f ? UiFormat.Percent0(b) : "Off");

        /// <summary>HDR preset labels: "Off" + 4 (amount, knee) combos.</summary>
        private static readonly ImmutableArray<string> HdrLabels = BuildLabels(
            ViewerState.HdrPresets, p => p.Amount > 0f ? $"{p.Amount:F1} / {p.Knee:F2}" : "Off");

        /// <summary>Background-neutralization preset table; combines method × strength
        /// into one flat dropdown. <c>null</c> method = "Off" (disable). Mean has a
        /// strength variant to demonstrate the lerp plumbing; the other methods stay
        /// at full strength until a separate strength slider lands.</summary>
        private static readonly (string Label, BackgroundNeutralizationMethod? Method, float Strength)[] BackgroundNeutralizationPresets =
        [
            ("Off",          null,                                          0f),
            ("Mean",         BackgroundNeutralizationMethod.Mean,           1f),
            ("Mean (50%)",   BackgroundNeutralizationMethod.Mean,           0.5f),
            ("Green pivot",  BackgroundNeutralizationMethod.GreenPivot,     1f),
            ("Min pivot",    BackgroundNeutralizationMethod.MinPivot,       1f),
        ];

        private static readonly ImmutableArray<string> BackgroundNeutralizationLabels = BuildLabels(
            BackgroundNeutralizationPresets, p => p.Label);

        private static string ShortMethodLabel(BackgroundNeutralizationMethod m) => m switch
        {
            BackgroundNeutralizationMethod.GreenPivot => "Green",
            BackgroundNeutralizationMethod.MinPivot   => "Min",
            _                                         => "Mean",
        };

        private static ImmutableArray<string> BuildLabels<T>(System.Collections.Generic.IReadOnlyList<T> items, Func<T, string> selector)
        {
            var arr = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                arr[i] = selector(items[i]);
            }
            return ImmutableArray.Create(arr);
        }

        // Fit plus every ratio Ctrl+1..Ctrl+9 reaches. The index is the denominator (entry 3 is "1:3"),
        // which is what lets the select handler be arithmetic instead of a parallel table that can drift
        // out of step with these strings.
        //
        // BUILT from ViewerActions.MaxZoomRatioDenominator rather than written out, because the wheel
        // steps the same ladder (ViewerActions.StepZoomRatio) and a hand-written list here could offer a
        // rung the wheel cannot reach, or stop short of one it can. One number, three consumers.
        private static readonly ImmutableArray<string> ZoomMenuLabels = BuildZoomMenuLabels();

        private static ImmutableArray<string> BuildZoomMenuLabels()
        {
            var builder = ImmutableArray.CreateBuilder<string>(ViewerActions.MaxZoomRatioDenominator + 1);
            builder.Add("Fit");
            for (var n = 1; n <= ViewerActions.MaxZoomRatioDenominator; n++)
            {
                builder.Add($"1:{n}");
            }
            return builder.MoveToImmutable();
        }

        // Zoom is a float, so "am I at 1:3" is a near-comparison. Tight enough that a wheel notch away
        // from a ratio does not claim to BE it -- ZoomStepFactor is 1.15, so the nearest neighbour is
        // 15% off and this is nowhere near catching it.
        private const float ZoomMatchTolerance = 0.001f;

        /// <summary>
        /// Which <see cref="ZoomMenuLabels"/> row the current zoom is, or -1 for a zoom that is none of
        /// them (a wheel zoom), so the menu highlights nothing rather than the nearest row.
        /// </summary>
        private static int CurrentZoomMenuIndex(ViewerState state)
        {
            if (state.ZoomToFit)
            {
                return 0;
            }
            for (var n = 1; n < ZoomMenuLabels.Length; n++)
            {
                if (MathF.Abs(state.Zoom - (1f / n)) < ZoomMatchTolerance)
                {
                    return n;
                }
            }
            return -1;
        }

        /// <summary>
        /// Opens the appropriate dropdown overlay for <paramref name="action"/>
        /// anchored below its toolbar button. Returns <c>true</c> if a dropdown
        /// was opened (caller must not also dispatch the action's cycle).
        /// Right-click on the same buttons still falls through to
        /// <see cref="ViewerActions.HandleToolbarAction"/> for reverse-cycle.
        /// </summary>
        public bool OpenToolbarDropdown(ViewerState state, ToolbarAction action)
        {
            if (!_toolbarButtonBounds.TryGetValue(action, out var bounds))
            {
                return false;
            }

            switch (action)
            {
                case ToolbarAction.Zoom:
                    // Index IS the ratio denominator, so "1:N" selects ZoomTo(1/N) with no lookup table
                    // and entry 0 is the one special case. Same set the keyboard reaches, deliberately:
                    // a menu offering zooms no shortcut has (or missing ones it does) would be a second
                    // vocabulary for one control.
                    OpenDropdown(state, bounds, ZoomMenuLabels, (idx, _) =>
                    {
                        if (idx == 0)
                        {
                            ViewerActions.ZoomToFit(state);
                        }
                        else if (idx > 0 && idx < ZoomMenuLabels.Length)
                        {
                            ViewerActions.ZoomTo(state, 1f / idx);
                        }
                        // Deliberately NO status message, and this is the ONE dropdown without one --
                        // StretchLink and Channel both set theirs, so the omission is a choice rather
                        // than an oversight. Two reasons. The status bar is the last-ACTION slot, and it
                        // persists until something replaces it, so "Zoom: 1:4" is still sitting there
                        // after the wheel has moved the zoom somewhere else; that is tolerable for a
                        // mode whose control is a word, but this control's label is the live value, so
                        // the message can contradict the button an inch above it. And putting a zoom
                        // string in the bottom row is the thing removing the zoom readout from the
                        // status bar was for -- the button is the readout now, and the image visibly
                        // rescaling is the confirmation a message would otherwise be giving.
                        state.NeedsRedraw = true;
                    }, CurrentZoomMenuIndex(state));
                    return true;

                case ToolbarAction.Shortcuts:
                    // A list, not a menu: selecting a row does nothing. The dropdown is reused because
                    // it already solves the two hard parts -- painting over everything, and scrolling
                    // when the list outgrows the window (DIR.Lib 6.19).
                    //
                    // Remember the bounds: when the AI probe lands a moment later, the panel is
                    // usually still open and has to be rebuilt to show the result, and Open needs
                    // somewhere to anchor.
                    _shortcutsBounds = bounds;
                    StartAiCapabilityProbe();
                    OpenDropdown(state, bounds, BuildHelpLines(), (_, _) => { });
                    return true;

                case ToolbarAction.StretchLink:
                    OpenDropdown(state, bounds, StretchLinkModeLabels, (idx, _) =>
                    {
                        var modes = ViewerActions.StretchLinkModes;
                        if ((uint)idx < (uint)modes.Length)
                        {
                            state.StretchMode = modes[idx];
                            state.NeedsRedraw = true;
                        }
                    }, Array.IndexOf(ViewerActions.StretchLinkModes, state.StretchMode));
                    return true;

                case ToolbarAction.Channel:
                    OpenDropdown(state, bounds, ChannelViewLabels, (idx, _) =>
                    {
                        if ((uint)idx < (uint)ChannelViewOrder.Length)
                        {
                            state.ChannelView = ChannelViewOrder[idx];
                            state.NeedsTextureUpdate = true;
                        }
                    }, Array.IndexOf(ChannelViewOrder, state.ChannelView));
                    return true;

                case ToolbarAction.Debayer:
                    OpenDropdown(state, bounds, DebayerLabels, (idx, _) =>
                    {
                        if ((uint)idx < (uint)DebayerAlgorithmOrder.Length)
                        {
                            state.DebayerAlgorithm = DebayerAlgorithmOrder[idx];
                            // RawBayer (SER / raw Bayer FITS) re-derives the GPU demosaic mode in
                            // UploadDocumentTextures, so the bilinear<->MHC switch is live; a CPU-debayered
                            // colour FITS is unaffected (it was demosaiced at load).
                            state.NeedsTextureUpdate = true;
                        }
                    }, Array.IndexOf(DebayerAlgorithmOrder, state.DebayerAlgorithm));
                    return true;

                case ToolbarAction.StretchParams:
                    OpenDropdown(state, bounds, StretchParamsLabels, (idx, _) =>
                    {
                        var presets = StretchParameters.Presets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.StretchPresetIndex = idx;
                            state.StretchParameters = presets[idx];
                            state.NeedsRedraw = true;
                        }
                    }, state.StretchPresetIndex);
                    return true;

                case ToolbarAction.CurvesBoost:
                    OpenDropdown(state, bounds, CurvesBoostLabels, (idx, _) =>
                    {
                        var presets = ViewerState.CurvesBoostPresets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.CurvesBoostIndex = idx;
                            state.CurvesBoost = presets[idx];
                            state.NeedsRedraw = true;
                        }
                    }, state.CurvesBoostIndex);
                    return true;

                case ToolbarAction.Hdr:
                    OpenDropdown(state, bounds, HdrLabels, (idx, _) =>
                    {
                        var presets = ViewerState.HdrPresets;
                        if ((uint)idx < (uint)presets.Length)
                        {
                            state.HdrPresetIndex = idx;
                            state.HdrAmount = presets[idx].Amount;
                            state.HdrKnee = presets[idx].Knee;
                            state.NeedsRedraw = true;
                        }
                    }, state.HdrPresetIndex);
                    return true;

                case ToolbarAction.BackgroundNeutralize:
                    OpenDropdown(state, bounds, BackgroundNeutralizationLabels, (idx, _) =>
                    {
                        var presets = BackgroundNeutralizationPresets;
                        if ((uint)idx >= (uint)presets.Length)
                        {
                            return;
                        }
                        var (label, method, strength) = presets[idx];
                        state.BackgroundNeutralizationStrength = strength;
                        if (method is { } m)
                        {
                            state.BackgroundNeutralizationMethod = m;
                            // Compute (or hit per-method cache) and pin gain onto document.
                            // User picked a method explicitly, so the toolbar reflects that
                            // even if this image's gain happens to land near identity.
                            var gain = _document?.ComputeBackgroundNeutralization(m, state.ColorCalibrationEnabled);
                            state.BackgroundNeutralizationEnabled = true;
                            state.StatusMessage = gain is { } g
                                // FOUR decimals, because two are useless here and actively
                                // misleading. The gain is affine about 1.0 (out = v*g + (1-g)) while
                                // an astro sky background sits around 0.002, so the gain needed to
                                // move it is a hair either side of unity: the measured triple that
                                // takes a 2.66x blue-over-red post-WB cast to exactly neutral is
                                // (0.9981, 1.0003, 1.0005), which at F2 prints as three 1.00s and
                                // reads as "this did nothing" over an image it visibly fixed.
                                ? $"NeutBg: {label}  R={g.R:F4} G={g.G:F4} B={g.B:F4}"
                                : $"NeutBg: {label} (no background data)";
                        }
                        else
                        {
                            // "Off" entry: drop the document gain so the uniform reverts to identity
                            if (_document is not null)
                            {
                                _document.BackgroundNeutralization = null;
                            }
                            state.BackgroundNeutralizationEnabled = false;
                            state.StatusMessage = "NeutBg: Off";
                        }
                        state.NeedsRedraw = true;
                    });
                    return true;

                default:
                    return false;
            }
        }

        private void OpenDropdown(ViewerState state, RectF32 bounds, ImmutableArray<string> labels, Action<int, string> onSelect, int selectedIndex = -1)
        {
            // Width = max(button width, widest label + horizontal padding).
            // RenderDropdownMenu draws each label with 0.5*fontSize padding per
            // side, so budget a full fontSize of slack to avoid edge clipping.
            var width = bounds.Width;
            var fontSize = ToolbarFontSize;
            foreach (var label in labels)
            {
                var labelWidth = MeasureText(label, fontSize) + fontSize;
                if (labelWidth > width)
                {
                    width = labelWidth;
                }
            }
            // These entries ARE their labels, and the callers still think in indices, so the value carried
            // is the label and the index comes from the array. Open seeds the highlight with the current
            // selection directly -- it used to be assigned straight afterwards to undo Open resetting it.
            var items = labels.Select(DropdownItem.Text).ToImmutableArray();
            // Keep the menu on screen. The help button is pinned to the right EDGE and its menu is far
            // wider than the button, so anchoring on bounds.X alone put most of every line past the
            // window. Only x is clamped: the menu scrolls itself when it is too tall, so lifting y would
            // fight that.
            var x = OverlayPlacement.ClampX(bounds.X, width, Width);

            state.ToolbarDropdown.Open(
                x,
                bounds.Y + bounds.Height,
                width,
                items,
                item => onSelect(items.IndexOf(item), item.Value),
                highlightIndex: selectedIndex);
            state.NeedsRedraw = true;
        }

        private bool IsToolbarButtonEnabled(ToolbarAction action, AstroImageDocument? document) => action switch
        {
            // Gate on the active source's sensor type, not on AstroImageDocument -- a SER is a
            // SerPreviewSource (document == null) but is a raw RGGB Bayer source the GPU debayers,
            // so the demosaic selector must stay enabled for it too.
            ToolbarAction.Debayer => _source?.SensorType is SensorType.RGGB,
            ToolbarAction.Channel => document is not null && document.UnstretchedImage.ChannelCount > 1,
            ToolbarAction.CurvesBoost => document?.Stars is { Count: > 0 },
            ToolbarAction.Hdr => document is not null,
            // There is nothing to write without a document, and a mark-only button that does nothing
            // when clicked is worse than a dimmed one: with no label, the status line is the only
            // thing that could have explained the no-op.
            ToolbarAction.Save => document is not null,
            ToolbarAction.StretchToggle => document is not null,
            ToolbarAction.StretchLink or ToolbarAction.StretchParams => document is not null,
            ToolbarAction.Grid => document?.Wcs is { HasCDMatrix: true },
            ToolbarAction.Overlays => document?.Wcs is { HasCDMatrix: true } && CelestialObjectDB?.IsValueCreated == true,
            ToolbarAction.Stars => document?.Stars is { Count: > 0 },
            ToolbarAction.ColorCalibrate => document?.Stars is { Count: >= 5 }
                && document.Stars.StarMask is not null
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.BackgroundNeutralize => document?.PerChannelBackground is { Length: >= 3 }
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.SpccCalibrate => document?.Stars is { Count: >= 3 }
                && document.IsPlateSolved
                && (document.UnstretchedImage.ChannelCount >= 3
                    || document.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB),
            ToolbarAction.PlateSolve => document is not null && !document.IsPlateSolved,
            ToolbarAction.ZoomFit or ToolbarAction.ZoomActual or ToolbarAction.Zoom => document is not null,
            // Only in the button set when EnhanceAvailable, so the gate here is just "have an image".
            // Re-click while a pass runs is harmless -- the controller guards on IsEnhancing.
            ToolbarAction.Enhance => document is not null,
            // Pixels, not a document: the pinned-settings comparison is just as useful on a SER
            // sequence (which has no document at all) as on a still.
            ToolbarAction.Compare => ImageWidth > 0,
            // Always available: it documents keys that work with nothing loaded (F11, Esc, L, I).
            ToolbarAction.Shortcuts => true,
            _ => true,
        };

        private bool IsToolbarButtonActive(ToolbarAction action, AstroImageDocument? document, ViewerState state)
        {
            return action switch
            {
                ToolbarAction.StretchToggle or ToolbarAction.StretchLink or ToolbarAction.StretchParams
                    => state.StretchMode is not StretchMode.None,
                // Highlight whenever a Bayer source is loaded and a demosaic is selected -- the GPU
                // applies state.DebayerAlgorithm live (re-derived in UploadDocumentTextures), so it's
                // never stale against an immutable document.DebayerAlgorithm. Works for SER + Bayer FITS.
                ToolbarAction.Debayer => _source?.SensorType is SensorType.RGGB
                    && state.DebayerAlgorithm is not DebayerAlgorithm.None,
                ToolbarAction.CurvesBoost => state.CurvesBoost > 0f,
                ToolbarAction.Hdr => state.HdrAmount > 0f,
                ToolbarAction.Grid => state.ShowGrid,
                ToolbarAction.Overlays => state.ShowOverlays,
                ToolbarAction.Stars => state.ShowStarOverlay,
                ToolbarAction.ColorCalibrate => state.ColorCalibrationEnabled,
                ToolbarAction.BackgroundNeutralize => state.BackgroundNeutralizationEnabled,
                ToolbarAction.SpccCalibrate => state.ColorCalibrationEnabled,
                // Zoom is deliberately absent: it is a mode DISPLAY, like Channel and StretchLink, and
                // its label already names the state a highlight would be hinting at.
                // Lit while running AND while an enhanced result is on screen: the highlight is what
                // says the toggle is ON, which is why the label below does not spell it out (same rule
                // the A/B button follows).
                ToolbarAction.Enhance => state.IsEnhancing || state.IsEnhanced,
                ToolbarAction.Compare => Split.IsOn,
                // A solution is a RESULT rather than a toggle, but it is the state the eye looks
                // for first: whether this frame has a WCS decides what the grid and the object
                // overlay can draw at all, so it is worth carrying across the bar as a highlight.
                ToolbarAction.PlateSolve => document?.IsPlateSolved == true,

                _ => false,
            };
        }

        // The hovered button's tooltip and the anchor to hang it from, captured during the toolbar
        // paint. Render-thread only, rebuilt every frame.
        /// <summary>
        /// The hover tooltip for this frame: its text, its anchor, and -- for a tooltip belonging to a
        /// LIST ROW -- that row's height.
        ///
        /// The row case is placed differently on purpose. A tooltip dropped below its anchor lands
        /// exactly on top of the NEXT row, so a full file name appears against a different file's
        /// position and reads as that file's name. Given the row's height it is instead drawn over the
        /// row itself, left edge and text inset matching, which reads as the row widening to fit its
        /// own name -- the thing the reader actually asked for.
        /// </summary>
        private (string Text, float X, float Y, float? RowHeight)? _hoveredTooltip;

        /// <summary>
        /// Draws the hovered toolbar button's tooltip. Called LAST in the frame so it paints over every
        /// other piece of chrome -- a tooltip that the file list or the info panel draws over is worse
        /// than none, because it looks like a rendering fault rather than a missing feature.
        /// </summary>
        private void RenderHoverTooltip(ViewerState state)
        {
            // An open dropdown owns the pointer, so the button underneath must not also explain itself.
            // Stated ONCE on the state (see ViewerState.OverlayOwnsPointer) rather than as a term here,
            // which is how the cursor predicate this codebase retired went wrong.
            if (_hoveredTooltip is not { } tip || state.OverlayOwnsPointer || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            // A row tooltip stands in for the row's own text, so it must use the LIST's font size and
            // the list's baseline -- at the toolbar's size it sat a couple of pixels off and the
            // underscores in a file name doubled up against the row beneath.
            var fontSize = tip.RowHeight is null ? ToolbarFontSize : FontSize;
            var textWidth = MeasureText(tip.Text, fontSize);

            RectF32 box;
            float textX;
            if (tip.RowHeight is { } rowHeight)
            {
                // Over the row, not below it. PanelPadding rather than the tooltip's own padding so
                // the revealed text starts at the same x as the truncated text it replaces -- a
                // different inset would make the name appear to jump sideways on hover.
                var width = textWidth + PanelPadding * 2f;
                var x = OverlayPlacement.ClampX(tip.X, width, Width);
                var y = OverlayPlacement.ClampY(tip.Y, rowHeight, Height);
                box = new RectF32(x, y, width, rowHeight);
                textX = x + PanelPadding;
            }
            else
            {
                var placed = OverlayPlacement.Place(OverlayPlacement.Anchor.Below, tip.X, tip.Y,
                    textWidth, fontSize, DpiScale, Width, Height);
                box = placed.Box;
                textX = placed.TextX;
            }

            FillRect(box.X - 1f, box.Y - 1f, box.Width + 2f, box.Height + 2f, ViewerTheme.Palette.SeparatorStrong);
            FillRect(box.X, box.Y, box.Width, box.Height, ViewerTheme.Palette.PanelBg);
            if (tip.RowHeight is { } rowTextHeight)
            {
                // Top-aligned at the same +2 inset RenderFileList uses, NOT vertically centred in the
                // box: centring is what put it off the row's baseline. Same helper, same offset, so
                // the revealed name lands exactly where the truncated one was.
                DrawText(tip.Text, textX, RowTextY(box.Y, rowTextHeight), fontSize, ViewerTheme.Palette.BodyText);
            }
            else
            {
                DrawText(tip.Text.AsSpan(), FontPath, textX, box.Y, box.Width, box.Height,
                    fontSize, ViewerTheme.Palette.BodyText, TextAlign.Near, TextAlign.Center);
            }
        }

        /// <summary>Design-unit edge of a toolbar mark, sized to the toolbar text beside it.</summary>
        /// <remarks>
        /// One size for every mark, so a row of them shares a baseline and an optical weight. 13 units
        /// against a ToolbarFontSize label is roughly its cap height, which is what stops a mark from
        /// standing taller than the words it sits among.
        /// </remarks>
        private const float BaseToolbarMarkSize = 13f;

        /// <summary>
        /// The ink width a button's mark needs, or 0 for a button that has none.
        /// </summary>
        /// <remarks>
        /// <para>Each of these is a picture because the picture says something its label could not, which
        /// is the bar the Bayer swatch set. They stay app-drawn rather than becoming DIR.Lib
        /// <c>IconKind</c>s on that enum's own rule -- a kind earns its place by having a consumer on
        /// both surfaces, and colour is the information in all of them, which the single-colour icon
        /// model cannot carry.</para>
        /// <para>Every one is <b>conditional on the frame actually having the thing depicted</b>. A CFA
        /// swatch over a mono sensor, or an RGB triple over Channel1 of a multi-channel stack, is a
        /// picture of something the pixels do not have -- so those cases fall back to the text label,
        /// which can say what the mark cannot.</para>
        /// </remarks>
        private float ToolbarMarkWidth(ToolbarAction action, ViewerState state)
            => HasToolbarMark(action, state) ? BaseToolbarMarkSize * DpiScale : 0f;

        private bool HasToolbarMark(ToolbarAction action, ViewerState state) => action switch
        {
            // The two file actions are marks INSTEAD of words, which is the whole point: they sit at
            // the head of a bar that had already run out of room and wrapped, and "Open" / "Save" are
            // the two labels a picture replaces without losing anything -- unlike a stateful label
            // such as the zoom's, they never had a value to say.
            ToolbarAction.Open => true,
            ToolbarAction.Save => true,
            ToolbarAction.Debayer => _source?.SensorType is SensorType.RGGB,
            // Composite / R / G / B are the three bars and which of them is lit. Channel0..2 are not
            // colours at all, so the mark would be inventing one.
            ToolbarAction.Channel => state.ChannelView
                is ChannelView.Composite or ChannelView.Red or ChannelView.Green or ChannelView.Blue,
            ToolbarAction.Grid => true,
            ToolbarAction.Overlays => true,
            ToolbarAction.Stars => true,
            ToolbarAction.Enhance => true,
            // The telescope says which button this is, so the label spends itself entirely on the
            // state -- "?" / an ellipsis / a tick, instead of "Solve" / "Solving..." / "Solved".
            ToolbarAction.PlateSolve => true,
            // The mark is what makes the tri-state label affordable: "Fit" / "1:1" / "43%" needs no word
            // saying it is a zoom, so the label spends all its width on the value.
            ToolbarAction.Zoom => true,
            _ => false,
        };

        private void DrawToolbarMark(ToolbarAction action, float x, float btnY, float btnH,
            ViewerState state, RGBAColor32 ink, bool enabled)
        {
            switch (action)
            {
                case ToolbarAction.Open: DrawFolderMark(x, btnY, btnH, ink); break;
                case ToolbarAction.Save: DrawSaveMark(x, btnY, btnH, ink); break;
                case ToolbarAction.Debayer: DrawBayerSwatch(x, btnY, btnH, enabled); break;
                case ToolbarAction.Channel: DrawChannelBars(x, btnY, btnH, state, enabled); break;
                case ToolbarAction.Grid: DrawBakedMark(BakedIcons.Globe, x, btnY, btnH, ink); break;
                case ToolbarAction.Overlays: DrawGalaxyMark(x, btnY, btnH, ink); break;
                case ToolbarAction.Stars: DrawStarMark(x, btnY, btnH, ink); break;
                case ToolbarAction.Enhance: DrawBakedMark(BakedIcons.Sparkles, x, btnY, btnH, ink); break;
                case ToolbarAction.Zoom: DrawBakedMark(BakedIcons.Magnifier, x, btnY, btnH, ink); break;
                case ToolbarAction.PlateSolve: DrawBakedMark(BakedIcons.Telescope, x, btnY, btnH, ink); break;
            }
        }

        /// <summary>
        /// Three bars in R / G / B, with the inactive ones dimmed: the channel view, and which channel.
        /// </summary>
        /// <remarks>
        /// This mark REPLACES its label rather than sitting beside one, which is what earns it its place
        /// -- "Channel: RGB" is a dozen characters saying what three bars say at a glance, on a toolbar
        /// that had already run out of room and wrapped to a second row. The unlit bars are drawn rather
        /// than omitted so the mark keeps ONE silhouette in every state: the eye then reads a colour
        /// change, which is quick, instead of a shape change, which is a re-read.
        /// </remarks>
        private void DrawChannelBars(float x, float btnY, float btnH, ViewerState state, bool enabled)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            var y = btnY + (btnH - size) / 2f;
            var barW = size / 4f;
            var gap = (size - barW * 3f) / 2f;

            for (var i = 0; i < 3; i++)
            {
                // Red = 1, Green = 2, Blue = 3 in ChannelView, so the enum's own order indexes the bars.
                var lit = state.ChannelView is ChannelView.Composite || (int)state.ChannelView == i + 1;
                var color = lit
                    ? i switch { 0 => BayerSwatchRed, 1 => BayerSwatchGreen, _ => BayerSwatchBlue }
                    : ChannelBarUnlit;
                FillRect(x + i * (barW + gap), y, barW, size, DimIfDisabled(color, enabled));
            }
        }

        // Dark enough to read as "off" against the button, but not invisible -- the unlit bars are what
        // keep the mark the same shape in every state.
        private static readonly RGBAColor32 ChannelBarUnlit = RGBAColor32.FromFloat(0.28f, 0.30f, 0.33f, 1f);

        /// <summary>
        /// A barred spiral: a central bar with two arms sweeping off its ends.
        /// </summary>
        /// <remarks>
        /// <para>The bar is what makes this legible at icon size. A plain two-arm spiral collapses into a
        /// pinwheel or a comma once the arms get short, whereas a bar reads as a definite object with
        /// structure hanging off it, and it carries the inclination the way the earlier solid lens did.
        /// Arms are point-symmetric about the centre, which is what real barred spirals do and what stops
        /// the mark reading as a single hook.</para>
        /// <para>Two things this must not become, both learned by drawing them: an OUTLINE with anything
        /// inside it reads as an eye -- iris in a lid -- so nothing here is a stroked closed curve; and
        /// the trick of filling an ellipse by over-thickening its stroke only works when both radii are
        /// equal, because a stroke inks half its thickness either side of the path and so leaves a
        /// lens-shaped hole along the MAJOR axis. That hole is what brought the eye back the second time.
        /// The bar is therefore a thick straight stroke, not a filled ellipse.</para>
        /// <para>The arms are a logarithmic spiral, r = r0.exp(b.phi), sampled as a polyline and tapering
        /// outward -- the abstract renderer seam offers lines and ellipses, so a curve is always a
        /// polyline here, and it needs enough segments not to show its corners (two segments is what made
        /// the graticule read as a hexagon).</para>
        /// </remarks>
        /// <summary>
        /// A spiral, baked from a glyph at build time (see <c>tools/BakeIcons</c>).
        /// </summary>
        /// <remarks>
        /// <para>Baked rather than drawn, because a spiral is past what this many pixels can carry as
        /// geometry and a font designer had already solved it. Three attempts failed first and the two
        /// interesting failures are worth not repeating: an OUTLINE with anything inside it reads as an
        /// eye -- iris in a lid -- whatever the inner shape is; and filling that outline by
        /// over-thickening its stroke does not work either, because a stroke inks half its thickness
        /// either side of the path, so it closes along the minor axis but leaves a lens-shaped hole along
        /// the MAJOR one, which is a ring with a dark middle, i.e. an eye again. Only a shape swept from
        /// CIRCLES fills, since the trick needs both radii equal.</para>
        /// <para>See <see cref="DrawBakedMark"/> for why a baked glyph rather than a runtime one.</para>
        /// </remarks>
        private void DrawGalaxyMark(float x, float btnY, float btnH, RGBAColor32 ink)
            => DrawBakedMark(BakedIcons.Spiral, x, btnY, btnH, ink);

        /// <summary>
        /// Draws a baked glyph mark, centred in the button and sized to the DPI.
        /// </summary>
        /// <remarks>
        /// <para>Baked rather than drawn from the font AT RUNTIME for three reasons the runtime path
        /// actually cost: no emoji face is bundled here, so a Linux host resolved none and the mark drew
        /// NOTHING; a COLRv1 glyph carries its own palette and so cannot be tinted, meaning it could not
        /// dim on a disabled button the way every other mark does; and the drawn result varied with
        /// whichever face the host happened to resolve.</para>
        /// <para>Runs are horizontal spans of constant coverage, so this is a loop of
        /// <see cref="ImageRendererBase{TSurface}.FillRect"/> and needs nothing new on the renderer seam.
        /// The mask is picked at the nearest baked size and scaled, so rows stay contiguous (they tile by
        /// construction) and a fractional scale cannot open gaps between them.</para>
        /// <para><b>Not every glyph survives baking, and the failures all look alike.</b> The bake keeps
        /// the ALPHA silhouette, so structure drawn in COLOUR is discarded while structure drawn in
        /// TRANSPARENCY survives -- a folder flap, a target's rings and a double triangle are each colour
        /// against colour and bake to a solid rectangle. Judge a candidate from the MASK, never from the
        /// emoji: in any colour preview those all look like perfectly good icons.</para>
        /// </remarks>
        private void DrawBakedMark(ImmutableArray<IconBaker.CoverageMask> masks, float x, float btnY,
            float btnH, RGBAColor32 ink)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            DrawCoverageMask(IconBaker.NearestSize(masks, size),
                x, btnY + (btnH - size) / 2f, size, ink);
        }

        /// <summary>
        /// A four-armed star with a bright core: detected stars.
        /// </summary>
        /// <remarks>
        /// <para>The arms TAPER, and that is the whole difference between a star and a crosshair. Two
        /// crossing lines of even thickness is a reticle -- the mark for "aim here" -- which is the wrong
        /// meaning on a button that counts what it found. Thickness falling from the core outward reads as
        /// light spilling from a point source, which is also what a bright star looks like through a
        /// telescope, so the mark is drawn from the subject rather than from an icon set.</para>
        /// <para>Tapered by stacking segments of decreasing thickness, because the abstract renderer seam
        /// offers lines and ellipses and no triangle -- DIR.Lib 7.26 added <c>DrawTriangles</c> to the
        /// Renderer, but <see cref="ImageRendererBase{TSurface}"/> does not expose it, and widening that
        /// seam for one toolbar mark would be the tail wagging the dog.</para>
        /// </remarks>
        /// <summary>
        /// A folder: an empty rectangle with a tab on its top-left corner.
        /// </summary>
        /// <remarks>
        /// <para><b>Hand-drawn because no glyph survives the bake.</b> Baked at 13 px against
        /// Noto-COLRv1, U+1F4C1 file-folder, U+1F4C4 page, U+1F4BE floppy and U+2B07 down-arrow all
        /// come out as solid blocks (78% to 85% of the square inked, most of it fully opaque) --
        /// the same failure <c>icons.recipe</c> already records for U+1F4C2, whose structure is
        /// drawn in COLOUR and so vanishes when only the alpha silhouette is kept. U+1F5C1 and
        /// U+21E9 are absent from the face entirely. The two tray glyphs (U+1F4E4 / U+1F4E5) do
        /// survive with structure, and were still rejected: at 13 px they differ from each other
        /// only in which way a two-pixel stem tapers, and a PAIR of marks has to differ in
        /// silhouette, not in detail.</para>
        /// <para>So the pair is drawn: a box for Open against an arrow for Save. Strokes only, and
        /// nothing sits INSIDE the rectangle -- an outline with anything in it reads as an eye at
        /// this size, which is the trap the spiral mark was redrawn three times to escape.</para>
        /// </remarks>
        private void DrawFolderMark(float x, float btnY, float btnH, RGBAColor32 ink)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            var y = btnY + (btnH - size) / 2f;
            var t = MathF.Max(1f, DpiScale);

            var left = x + size * 0.05f;
            var right = x + size * 0.95f;
            var tabRight = x + size * 0.40f;
            var bodyTop = y + size * 0.36f;
            var tabTop = y + size * 0.22f;
            var bottom = y + size * 0.82f;

            DrawLineOverlay(left, tabTop, tabRight, tabTop, ink, t);              // tab
            DrawLineOverlay(tabRight, tabTop, x + size * 0.52f, bodyTop, ink, t); // tab riser
            DrawLineOverlay(x + size * 0.52f, bodyTop, right, bodyTop, ink, t);   // body top
            DrawLineOverlay(left, tabTop, left, bottom, ink, t);
            DrawLineOverlay(right, bodyTop, right, bottom, ink, t);
            DrawLineOverlay(left, bottom, right, bottom, ink, t);
        }

        /// <summary>
        /// A down arrow into a tray: the file goes down, onto the disk.
        /// </summary>
        /// <remarks>
        /// Paired with <see cref="DrawFolderMark"/> and differing from it in SILHOUETTE -- an arrow
        /// against a box -- rather than in detail, which is the whole reason both are drawn rather
        /// than baked. The tray is open at the top (two walls and a floor, no lid) so it cannot read
        /// as a second rectangle beside the folder's.
        /// </remarks>
        private void DrawSaveMark(float x, float btnY, float btnH, RGBAColor32 ink)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            var y = btnY + (btnH - size) / 2f;
            var t = MathF.Max(1f, DpiScale);
            var cx = x + size / 2f;

            var tip = y + size * 0.56f;
            DrawLineOverlay(cx, y + size * 0.08f, cx, y + size * 0.50f, ink, t);        // shaft
            DrawLineOverlay(cx - size * 0.24f, y + size * 0.32f, cx, tip, ink, t);      // head, left
            DrawLineOverlay(cx + size * 0.24f, y + size * 0.32f, cx, tip, ink, t);      // head, right

            var left = x + size * 0.08f;
            var right = x + size * 0.92f;
            var floor = y + size * 0.86f;
            DrawLineOverlay(left, y + size * 0.66f, left, floor, ink, t);
            DrawLineOverlay(right, y + size * 0.66f, right, floor, ink, t);
            DrawLineOverlay(left, floor, right, floor, ink, t);
        }

        private void DrawStarMark(float x, float btnY, float btnH, RGBAColor32 ink)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            var cx = x + size / 2f;
            var cy = btnY + btnH / 2f;
            var t = MathF.Max(1f, DpiScale);
            var arm = size * 0.46f;

            // Four arms, each three segments thick-to-thin from the centre out.
            const int Steps = 3;
            for (var dir = 0; dir < 4; dir++)
            {
                var dx = dir == 0 ? 1f : dir == 1 ? -1f : 0f;
                var dy = dir == 2 ? 1f : dir == 3 ? -1f : 0f;
                for (var s = 0; s < Steps; s++)
                {
                    var r0 = arm * s / Steps;
                    var r1 = arm * (s + 1) / Steps;
                    var thick = t * (Steps - s) / Steps * 1.6f;
                    DrawLineOverlay(cx + dx * r0, cy + dy * r0, cx + dx * r1, cy + dy * r1,
                        ink, MathF.Max(1f, thick));
                }
            }

            // The core, filled: an ellipse whose stroke exceeds its radii inks solid.
            DrawEllipseOverlay(cx, cy, size * 0.1f, size * 0.1f, 0f, ink, size * 0.12f);
        }

        /// <summary>
        /// Draws the sensor's colour-filter-array quad: four cells in the phase the frame actually has.
        /// </summary>
        /// <remarks>
        /// <para>This is the one toolbar action whose meaning IS a picture, and the picture says something
        /// the old "Debayer: AHD" label could not say at all: which quadrant carries red. That is
        /// <see cref="ImageRendererBase{TSurface}.BayerOffsetX"/> / <c>BayerOffsetY</c>, and getting it
        /// wrong swaps the red and blue channels of the whole image -- so having it visible on the button
        /// is a correctness affordance, not decoration.</para>
        /// <para>Drawn here rather than promoted to a DIR.Lib <c>IconKind</c>, on that enum's own rule:
        /// a kind "earns its place by having a consumer on both" surfaces, and a terminal cannot say
        /// red/green/green/blue in one glyph. Its doc names the alternative -- "a one-off pictogram
        /// belongs in a Content.Fill the app draws itself". The colour here is not styling, it is the
        /// information, which is exactly why the single-colour icon model cannot carry it.</para>
        /// </remarks>
        /// <summary>The factor a colour-carrying mark is scaled by when its button is disabled.</summary>
        /// <remarks>
        /// A monochrome mark just takes the dimmed ink the label takes. A colour mark cannot -- its hue is
        /// the information, so it has to dim by losing brightness rather than by losing hue. The ratio is
        /// the label's own (0.45 / 0.9), so a disabled mark and a disabled word fade together.
        /// <para>Without this a disabled button with NO label reads as live, which is exactly what the
        /// Channel button did the moment its text was removed: a one-channel frame disables channel
        /// selection (there is no red to pick out of a mono image), the button correctly registers no
        /// click region at all, and it still painted three fully saturated bars.</para>
        /// </remarks>
        private const float DisabledMarkScale = 0.5f;

        private static RGBAColor32 DimIfDisabled(RGBAColor32 c, bool enabled)
            => enabled
                ? c
                : new RGBAColor32(
                    (byte)(c.Red * DisabledMarkScale),
                    (byte)(c.Green * DisabledMarkScale),
                    (byte)(c.Blue * DisabledMarkScale),
                    c.Alpha);

        private void DrawBayerSwatch(float x, float btnY, float btnH, bool enabled)
        {
            var size = BaseToolbarMarkSize * DpiScale;
            var cell = size / 2f;
            var y = btnY + (btnH - size) / 2f;

            for (var cy = 0; cy < 2; cy++)
            {
                for (var cx = 0; cx < 2; cx++)
                {
                    // Phase in SENSOR coordinates, so the swatch rotates with the frame's CFA offset
                    // instead of always drawing a nominal RGGB.
                    var sx = (cx + BayerOffsetX) & 1;
                    var sy = (cy + BayerOffsetY) & 1;
                    var color = (sx, sy) switch
                    {
                        (0, 0) => BayerSwatchRed,
                        (1, 1) => BayerSwatchBlue,
                        _ => BayerSwatchGreen,
                    };
                    FillRect(x + cx * cell, y + cy * cell, cell, cell, DimIfDisabled(color, enabled));
                }
            }
        }

        // Muted rather than saturated: the swatch sits in a row of text and must read as a label, not
        // as an alert.
        private static readonly RGBAColor32 BayerSwatchRed = RGBAColor32.FromFloat(0.78f, 0.28f, 0.28f, 1f);
        private static readonly RGBAColor32 BayerSwatchGreen = RGBAColor32.FromFloat(0.36f, 0.70f, 0.36f, 1f);
        private static readonly RGBAColor32 BayerSwatchBlue = RGBAColor32.FromFloat(0.34f, 0.46f, 0.82f, 1f);

        /// <summary>
        /// What a button does and the key that does it. Declared beside the label because it is the other
        /// half of the same statement -- and it is what makes the short labels above safe: "RGB" alone is
        /// terse, "RGB" plus "Channel view (C cycles)" on hover is not.
        /// </summary>
        /// <remarks>
        /// Takes the document as well as the state because the one thing a calibration button most
        /// needs to report -- what the calibration MEASURED -- lives on the document, not the state.
        /// </remarks>
        private static string? GetToolbarButtonTooltip(
            ToolbarAction action, ViewerState state, AstroImageDocument? document) => action switch
        {
            ToolbarAction.Open => "Open a FITS / TIFF / SER file",
            ToolbarAction.Save => "Save the image as displayed (PNG / JPEG / TIFF), at full resolution",
            ToolbarAction.StretchToggle => "Screen transfer function on / off (T)",
            ToolbarAction.StretchLink => "Stretch mode: auto picks linked when calibrated and unlinked otherwise; linked keeps colour, unlinked neutralises the background, luma stretches luminance",
            ToolbarAction.StretchParams => "Stretch strength preset (+ / -)",
            ToolbarAction.Channel => "Channel view: RGB or one channel (C cycles)",
            ToolbarAction.Debayer => "Demosaic algorithm; the swatch is the sensor's CFA phase (D cycles)",
            ToolbarAction.CurvesBoost => "Curves boost; right-click switches curve mode (B / Shift+B)",
            ToolbarAction.Hdr => "HDR highlight compression (H cycles)",
            ToolbarAction.Compare => "Before / after split; right-click re-pins (A / Shift+A)",
            ToolbarAction.ZoomFit => "Fit the image to the window (F / Ctrl+0)",
            ToolbarAction.ZoomActual => "Zoom to 1:1 (R / Ctrl+1)",
            // Fitting is the only state whose label is a word, so the tooltip is where its actual scale
            // lives -- it is the reason the status bar no longer needs a zoom readout at all.
            ToolbarAction.Zoom when state.ZoomToFit =>
                $"Zoom: fitting at {UiFormat.Percent0(state.Zoom)} -- click or Z to pick 1:1 / 1:N, right-click for 1:1 (F, R, Ctrl+0..9)",
            ToolbarAction.Zoom => "Zoom: click or Z to pick fit / 1:1 / 1:N, right-click fits (F, R, Ctrl+0..9)",
            ToolbarAction.PlateSolve => "Plate solve this frame (P)",
            ToolbarAction.Grid => "WCS coordinate grid (G)",
            ToolbarAction.Overlays => "Deep-sky object overlays (O)",
            ToolbarAction.Stars => "Detect stars and show HFD / FWHM (S)",
            // A calibration that has RUN reports what it measured. This is the whole answer to "did
            // SPCC do anything, and can I trust it": the triple, the survivor count and the white
            // reference, which is the one number a PixInsight user can line up against their own.
            // The button label only has room for R and B, and the info panel is a different place
            // from the pointer -- so the tooltip is where the full statement belongs.
            //
            // Gated on ColorCalibrationEnabled, not merely on the summary being present, so a
            // switched-OFF calibration does not describe a correction the image is not receiving.
            ToolbarAction.ColorCalibrate or ToolbarAction.SpccCalibrate
                when state.ColorCalibrationEnabled && document?.ColorCalibrationSummary is { } done =>
                $"{done.Describe()} -- click to turn off (W)",
            ToolbarAction.ColorCalibrate => "Photometric colour calibration (W)",
            ToolbarAction.BackgroundNeutralize => "Neutralise the background (N)",
            ToolbarAction.SpccCalibrate => "Spectrophotometric colour calibration (W)",
            ToolbarAction.Enhance when state.IsEnhancing => "Cancel this enhance (E)",
            ToolbarAction.Enhance when state.IsEnhanced => "Turn the enhancement off (E); right-click cycles the backend",
            ToolbarAction.Enhance => "AI enhance; right-click cycles the backend (E)",
            ToolbarAction.Shortcuts => "All keyboard shortcuts",
            _ => null,
        };

        /// <summary>
        /// Every shortcut with no button of its own to carry it in a tooltip. This is the residue the
        /// tooltips do not cover, and it is why deleting the info panel's Controls block needed a home
        /// rather than just a delete: zoom ratios, playback and the panel toggles are otherwise
        /// undiscoverable.
        /// </summary>
        /// <summary>Keyed tracker slot for the capability probe. One key means repeated "?" opens
        /// share the one probe instead of each launching another round of process spawns.</summary>
        private const string AiProbeKey = "viewer.ai-capabilities";

        private ImmutableArray<string> _aiLines = ImmutableArray<string>.Empty;
        private bool _aiProbeStarted;
        private RectF32? _shortcutsBounds;

        /// <summary>
        /// Starts the capability probe under <see cref="AiProbeKey"/>, at most once.
        /// <para>
        /// Guarded on <c>IsRunning</c> rather than just calling <c>RunExclusive</c>, because
        /// RunExclusive CANCELS its predecessor -- which is right for a superseding query like a
        /// re-search, and wrong here: reopening the panel while the probe is in flight would kill it
        /// and restart from nothing, so a user clicking "?" twice would never see a result.
        /// </para>
        /// </summary>
        private void StartAiCapabilityProbe()
        {
            if (_aiProbeStarted || AiCapabilityProbe is not { } probe || Tracker is not { } tracker)
            {
                return;
            }

            if (tracker.IsRunning(AiProbeKey))
            {
                return;
            }

            _aiProbeStarted = true;
            tracker.RunExclusive<IReadOnlyList<string>>(
                AiProbeKey,
                async ct => (IReadOnlyList<string>?)await probe(ct).ConfigureAwait(false),
                AppToken,
                Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                "AI capability probe",
                // A failed probe must not leave the panel saying "probing..." forever, and it is not
                // worth a dialog: the panel is where a user looks when something is already odd.
                onError: ex => _aiLines = [$"probe failed: {ex.GetType().Name}: {ex.Message}"]);
        }

        /// <summary>
        /// Collects a finished probe and refreshes the panel if it is still open. Called once per
        /// frame; <c>TryCollect</c> is a no-op until the work completes.
        /// </summary>
        private void CollectAiCapabilities(ViewerState state)
        {
            if (Tracker is not { } tracker || !tracker.TryCollect<IReadOnlyList<string>>(AiProbeKey, out var lines))
            {
                return;
            }

            _aiLines = lines is null ? ImmutableArray<string>.Empty : [.. lines];
            state.NeedsRedraw = true;

            // Rebuild an open panel in place. Reopening resets its scroll, which is why the probe is
            // not started earlier and speculatively: it lands within a moment of the first open, so
            // the reset happens once and before anyone has scrolled.
            if (state.ToolbarDropdown.IsOpen && _shortcutsBounds is { } bounds)
            {
                OpenDropdown(state, bounds, BuildHelpLines(), (_, _) => { });
            }
        }

        /// <summary>
        /// The "?" panel: what this build IS, what it can do, then how to drive it.
        /// <para>
        /// Provenance leads because it is the half a user can read back to you. The keyboard list was
        /// the whole panel before, which meant the one screen a user opens when something looks wrong
        /// could not tell them which version they were running.
        /// </para>
        /// </summary>
        private ImmutableArray<string> BuildHelpLines()
        {
            var lines = ImmutableArray.CreateBuilder<string>(ShortcutLines.Length + _aiLines.Length + 6);
            lines.Add($"TianWen {TianWen.Lib.BuildInfo.Describe()}");
            lines.Add(Ellipsize(TianWen.Lib.BuildInfo.InstallFolder));
            lines.Add("");

            lines.Add("AI enhancement");
            if (AiCapabilityProbe is null)
            {
                lines.Add("  no AI stack configured in this build");
            }
            else if (_aiLines.IsEmpty)
            {
                lines.Add("  probing...");
            }
            else
            {
                foreach (var line in _aiLines)
                {
                    lines.Add(Ellipsize("  " + line));
                }
            }
            lines.Add("");

            lines.AddRange(ShortcutLines);
            return lines.ToImmutable();
        }

        /// <summary>
        /// Shortens a line to fit the window, ellipsing in the MIDDLE, via DIR.Lib's
        /// <see cref="TextFit"/>.
        /// <para>
        /// Middle rather than end because every long line here is a path, and a path's two informative
        /// ends are the drive/root and the file name. Why that is the right policy, and why a cell
        /// surface honours it too, is stated once on <see cref="TextTrim.Middle"/> -- this was a private
        /// re-implementation until the policy was upstreamed, which is the same two-copies-of-a-rule
        /// mistake the slider primitives and the window activation both had to be walked back from.
        /// </para>
        /// <para>Budget is the window minus a margin, not the dropdown's own width, because the
        /// dropdown's width is DERIVED from these labels -- asking it first would be circular.</para>
        /// </summary>
        private string Ellipsize(string line)
            => Ellipsize(line, Width - (4 * ToolbarFontSize), ToolbarFontSize);

        /// <summary>
        /// Middle-ellipsis to an explicit pixel budget and font size, for a caller whose width is not
        /// the window's -- the info panel, which has its own column and its own font size.
        /// </summary>
        private string Ellipsize(string line, float budget, float fontSize)
            => TextFit.ForWidth(Renderer, line, FontPath, FontFallback, fontSize, budget, TextTrim.Middle).Text;

        private static readonly ImmutableArray<string> ShortcutLines =
        [
            "Wheel / Ctrl+Wheel   Zoom",
            "Ctrl + / -           Zoom in / out",
            "Ctrl+2 .. Ctrl+9     Zoom 1:N",
            "Z                    Zoom menu (fit / 1:1 / 1:N)",
            "V / Shift+V          Histogram / log scale",
            "I                    Info panel",
            "L                    File list",
            "K                    Raw / stacked view (sequence)",
            "Space                Play / pause (sequence), else blink the file list",
            "Shift+Space          Hold / release the display across frames",
            "Left / Right         Step one frame",
            "Home / End           First / last frame",
            "Up / Down            Previous / next file",
            "F11                  Fullscreen",
            "Esc                  Quit",
        ];

        // What Auto resolved to for the frame on screen, named for the StretchLink button. Mirrors the
        // producer's inputs: colour vs mono, and whether a calibration is actually being applied.
        private static string ResolvedAutoLabel(AstroImageDocument? document, ViewerState state)
        {
            var isColour = document is { } d
                && (d.UnstretchedImage.ChannelCount >= 3
                    || d.UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB);
            var calibrationActive = state.ColorCalibrationEnabled && document?.ColorCalibration is not null;
            return StretchMode.Auto.ResolveAuto(isColour, calibrationActive) switch
            {
                StretchMode.Unlinked => "Unlinked",
                _ => "Linked",
            };
        }

        private string GetToolbarButtonLabel(string baseLabel, ToolbarAction action, AstroImageDocument? document, ViewerState state)
        {
            return action switch
            {
                ToolbarAction.StretchToggle => "STF",
                ToolbarAction.StretchLink => state.StretchMode switch
                {
                    // Auto names what it resolved to, so the mode it picked is never a mystery.
                    StretchMode.Auto => $"Auto ({ResolvedAutoLabel(document, state)})",
                    StretchMode.Linked => "Linked",
                    StretchMode.Luma => "Luma",
                    _ => "Unlinked"
                },
                ToolbarAction.StretchParams => $"{state.StretchParameters}",
                // Empty when the bars are drawn: the mark IS the label. Channel0..2 get no mark (they
                // are not colours), so they still name themselves.
                ToolbarAction.Channel => HasToolbarMark(ToolbarAction.Channel, state)
                    ? string.Empty
                    : $"{state.ChannelView}",
                // No label at all: the folder and the tray say it, and the tooltip carries the rest.
                ToolbarAction.Open or ToolbarAction.Save => string.Empty,
                ToolbarAction.Debayer => state.DebayerAlgorithm.DisplayName,
                ToolbarAction.CurvesBoost => state.CurvesBoost > 0f ? $"Boost {UiFormat.Percent0(state.CurvesBoost)}" : "Boost",
                ToolbarAction.Hdr => state.HdrAmount > 0f ? $"HDR: {state.HdrAmount:F1}" : "HDR",
                // Tri-state, and the third state is the point: at any zoom that is neither fit nor 1:1
                // the old pair of buttons showed nothing at all, so the one number a zoom control exists
                // to report was the one thing the toolbar could not say.
                //
                // The button and its menu speak ONE vocabulary. A zoom that is exactly a menu ratio
                // names itself with that ratio, so picking "1:4" reads back as "1:4" rather than as
                // "25%" -- the same value in a different language, which reads as the control having
                // ignored the choice. Only a zoom with no ratio (the wheel) falls through to a
                // percentage. This also folds 1:1 in as just the n=1 case instead of a special branch.
                ToolbarAction.Zoom when state.ZoomToFit => "Fit",
                ToolbarAction.Zoom when CurrentZoomMenuIndex(state) is var zoomRow && zoomRow > 0 =>
                    ZoomMenuLabels[zoomRow],
                ToolbarAction.Zoom => UiFormat.Percent0(state.Zoom),
                // Mark-only, like Channel: the point of a mark on this toolbar is the WIDTH it gives
                // back, and a mark sitting beside the word it replaces gives back nothing. The tooltip
                // carries the name.
                ToolbarAction.Grid => string.Empty,
                // The ellipsis survives on its own, because it is not the button's NAME -- it is the
                // warning that the first press pays for loading the object database. Three characters
                // keeps a signal that the mark cannot draw and the tooltip only shows on hover, which
                // is too late for something whose whole job is to set an expectation before the click.
                ToolbarAction.Overlays when CelestialObjectDB is { IsValueCreated: false } => "...",
                ToolbarAction.Overlays => string.Empty,
                // The mark says what these are, so the label only has to say how many. Before the pass
                // has run there is no number, and the WORD standing where a count will be is what marks
                // it as not-yet-run -- which is why this drops the "..." that Objects keeps: Objects is a
                // toggle with no count to switch to, so there the ellipsis is the only such signal.
                ToolbarAction.Stars when document?.Stars is null => "Stars",
                ToolbarAction.Stars when document?.Stars is { } s => $"{s.Count}",
                ToolbarAction.BackgroundNeutralize when state.BackgroundNeutralizationEnabled =>
                    state.BackgroundNeutralizationStrength >= 0.9999f
                        ? $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)}"
                        : $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)} {UiFormat.Percent0(state.BackgroundNeutralizationStrength)}",
                ToolbarAction.SpccCalibrate when state.ColorCalibrationEnabled => $"SPCC: {document?.ColorCalibration?.R:F2}/{document?.ColorCalibration?.B:F2}",
                // The mark says WHAT this button is, so the label is free to say only the state --
                // and the state is the whole question a plate solve answers. The tick is paired
                // with the activated highlight deliberately: whether this frame carries a WCS
                // decides what the grid and the object overlay can draw at all, so it is worth
                // saying twice rather than leaving it to a highlight that every other button
                // also uses for "on".
                ToolbarAction.PlateSolve when state.IsPlateSolving => "\u2026",
                ToolbarAction.PlateSolve when document?.IsPlateSolved == true => "\u2714",
                // Unsolved is a QUESTION, because that is what an unsolved frame is -- and this
                // button is what answers it. The word "Solve" is gone because the telescope mark
                // says which button this is and the tooltip names the action.
                ToolbarAction.PlateSolve => "?",
                // The sparkles mark says "AI enhance", so the label is free to say only the part it
                // cannot: which backend, or how far along. This is the Channel rule again -- a mark that
                // sits beside the word it replaces gives no width back.
                ToolbarAction.Enhance when state.IsEnhancing => $"{state.EnhanceProgressPct:F0}%",
                // Show the selected backend (right-click cycles it); left-click runs the enhance.
                ToolbarAction.Enhance => state.PreferredEnhanceBackend switch
                {
                    EnhanceBackend.ForceRcAstro => "RC",
                    EnhanceBackend.ForceSas => "SAS",
                    EnhanceBackend.N2n => "N2N",
                    _ => "Auto",
                },
                // No ":Pinned" suffix: pinned settings are the DEFAULT comparison, so the activated
                // highlight already says it. "Before" stays named, because that is a different thing
                // being compared (the pre-enhance pixels) rather than the same thing at other settings.
                ToolbarAction.Compare when Split.ComparesPixels && Split.IsOn => "A/B: Before",
                _ => baseLabel,
            };
        }

        /// <summary>
        /// The enabled toolbar button under a point, or null.
        /// </summary>
        /// <remarks>
        /// Answers from <see cref="_toolbarButtonBounds"/> -- the rects the buttons were PAINTED at this
        /// frame -- so it cannot disagree with what is on screen. It used to re-run the whole sizing
        /// walk (label, text width, swatch, group spacing) as a second implementation, which is the
        /// draw-vs-hit split the widget taxonomy's rule 3 forbids; right-aligning a button would have
        /// needed the alignment mirrored there too. Only enabled buttons are registered, so an empty
        /// answer covers "over a disabled button" and "over no button" alike -- both mean nothing to
        /// hover.
        /// </remarks>
        public ToolbarAction? HitTestToolbar(float screenX, float screenY)
        {
            foreach (var (action, bounds) in _toolbarButtonBounds)
            {
                if (bounds.Contains(screenX, screenY))
                {
                    return action;
                }
            }

            return null;
        }

    }
}

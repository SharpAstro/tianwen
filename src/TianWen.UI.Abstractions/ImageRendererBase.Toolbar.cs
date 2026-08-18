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
            ("STF", ToolbarAction.StretchToggle, 1),
            ("Link", ToolbarAction.StretchLink, 1),
            ("Params", ToolbarAction.StretchParams, 1),
            ("Channel", ToolbarAction.Channel, 2),
            ("Debayer", ToolbarAction.Debayer, 2),
            ("Boost", ToolbarAction.CurvesBoost, 2),
            ("HDR", ToolbarAction.Hdr, 2),
            ("A/B", ToolbarAction.Compare, 2),
            ("Fit", ToolbarAction.ZoomFit, 3),
            ("1:1", ToolbarAction.ZoomActual, 3),
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
        /// <para>Help earns a fixed corner. Its x must not depend on how wide "AI: Auto" or
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

            // Recompute button bounds every frame: labels can change width
            // (e.g. "Stars" -> "Stars: 5893") which shifts later buttons.
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
            PaintToolbarButtons(tb, state);
        }

        /// <summary>One toolbar button, already positioned.</summary>
        private readonly record struct ToolbarButtonBox(
            string Label, ToolbarAction Action, float SwatchWidth, RectF32 Rect, bool Enabled, bool Active);

        // Per-frame scratch, reused: this runs every frame on the render thread and nothing in it
        // outlives the frame.
        private readonly List<ToolbarButtonBox> _toolbarBoxes = new();

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

            var btnH = tb.Height - ButtonSpacing * 2;
            var btnY = tb.Y + ButtonSpacing;
            var buttons = ToolbarButtons;
            var rightAligned = RightAlignedToolbarActions;

            // Measure the right block FIRST: the left run needs to know where it must stop, and a
            // right-aligned button cannot be placed until its own width is known.
            var rightWidth = 0f;
            var rightCount = 0;
            for (var i = 0; i < buttons.Length; i++)
            {
                if (!rightAligned.Contains(buttons[i].Action))
                {
                    continue;
                }
                rightWidth += MeasureToolbarButton(buttons[i], document, state).Width;
                rightCount++;
            }
            if (rightCount > 1)
            {
                rightWidth += ButtonSpacing * (rightCount - 1);
            }

            var rightStart = tb.Right - PanelPadding - rightWidth;
            var leftLimit = rightCount > 0 ? rightStart - ButtonGroupSpacing : tb.Right - PanelPadding;

            var x = tb.X + PanelPadding;
            var prevGroup = -1;
            for (var i = 0; i < buttons.Length; i++)
            {
                var entry = buttons[i];
                if (rightAligned.Contains(entry.Action))
                {
                    continue;
                }

                if (prevGroup >= 0 && entry.Group != prevGroup)
                {
                    x += ButtonGroupSpacing;
                }
                prevGroup = entry.Group;

                var (label, swatchW, btnW) = MeasureToolbarButton(entry, document, state);

                // Out of room. Drop it entirely -- no paint, no hit -- rather than let it slide under
                // the right block, where it would still be registered and would eat the click aimed at
                // what is drawn on top. Everything after this is further right, so stop. The real fix
                // for a narrow window is wrapping to a second row, which needs the band height decided
                // in ComputeLayout; see docs/todo/ui.md.
                if (x + btnW > leftLimit)
                {
                    break;
                }

                AddToolbarBox(entry.Action, label, swatchW, new RectF32(x, btnY, btnW, btnH), document, state);
                x += btnW + ButtonSpacing;
            }

            // The right block, in the table's own order, from the start its measured width reserved.
            var rx = rightStart;
            for (var i = 0; i < buttons.Length; i++)
            {
                var entry = buttons[i];
                if (!rightAligned.Contains(entry.Action))
                {
                    continue;
                }

                var (label, swatchW, btnW) = MeasureToolbarButton(entry, document, state);
                AddToolbarBox(entry.Action, label, swatchW, new RectF32(rx, btnY, btnW, btnH), document, state);
                rx += btnW + ButtonSpacing;
            }
        }

        private void AddToolbarBox(ToolbarAction action, string label, float swatchWidth, in RectF32 rect,
            AstroImageDocument? document, ViewerState state)
            => _toolbarBoxes.Add(new ToolbarButtonBox(label, action, swatchWidth, rect,
                IsToolbarButtonEnabled(action, document),
                IsToolbarButtonActive(action, document, state)));

        /// <summary>Resolves a button's label and the width it needs. The one place a button width is
        /// computed.</summary>
        private (string Label, float SwatchWidth, float Width) MeasureToolbarButton(
            (string Label, ToolbarAction Action, int Group) entry, AstroImageDocument? document, ViewerState state)
        {
            var label = GetToolbarButtonLabel(entry.Label, entry.Action, document, state);
            var swatchW = BayerSwatchWidth(entry.Action);
            return (label, swatchW, MeasureText(label, ToolbarFontSize) + swatchW + ButtonPaddingH * 2);
        }

        private void PaintToolbarButtons(in RectF32 tb, ViewerState state)
        {
            var mouse = state.MouseScreenPosition;
            var textY = tb.Y + (tb.Height - ToolbarFontSize) / 2f;

            foreach (var box in _toolbarBoxes)
            {
                var r = box.Rect;
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

                if (box.SwatchWidth > 0f)
                {
                    DrawBayerSwatch(r.X + ButtonPaddingH, r.Y, r.Height);
                }

                var textBrightness = box.Enabled ? 0.9f : 0.45f;
                DrawText(box.Label, r.X + ButtonPaddingH + box.SwatchWidth, textY, ToolbarFontSize,
                    RGBAColor32.FromFloat(textBrightness, textBrightness, textBrightness, 1f));

                if (hovered && GetToolbarButtonTooltip(box.Action, state) is { Length: > 0 } tip)
                {
                    _hoveredTooltip = (tip, r.X, r.Bottom);
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
            ViewerState.CurvesBoostPresets, b => b > 0f ? $"{b:P0}" : "Off");

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
                case ToolbarAction.Shortcuts:
                    // A list, not a menu: selecting a row does nothing. The dropdown is reused because
                    // it already solves the two hard parts -- painting over everything, and scrolling
                    // when the list outgrows the window (DIR.Lib 6.19).
                    OpenDropdown(state, bounds, ShortcutLines, (_, _) => { });
                    return true;

                case ToolbarAction.StretchLink:
                    OpenDropdown(state, bounds, StretchLinkModeLabels, (idx, _) =>
                    {
                        var modes = ViewerActions.StretchLinkModes;
                        if ((uint)idx < (uint)modes.Length)
                        {
                            state.StretchMode = modes[idx];
                            state.StatusMessage = $"Stretch: {state.StretchMode}";
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
                            state.StatusMessage = $"Channel: {state.ChannelView}";
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
                            state.StatusMessage = $"Debayer: {state.DebayerAlgorithm.DisplayName}";
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
                            state.StatusMessage = $"Stretch: {state.StretchParameters}";
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
                            state.StatusMessage = state.CurvesBoost > 0f ? $"Curves Boost: {state.CurvesBoost:P0}" : "Curves Boost: Off";
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
                            state.StatusMessage = presets[idx].Amount > 0f
                                ? $"HDR: {presets[idx].Amount:F1} (knee {presets[idx].Knee:F2})"
                                : "HDR: Off";
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
                            var gain = _document?.ComputeBackgroundNeutralization(m);
                            state.BackgroundNeutralizationEnabled = true;
                            state.StatusMessage = gain is { } g
                                ? $"NeutBg: {label}  R={g.R:F2} G={g.G:F2} B={g.B:F2}"
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
            ToolbarAction.ZoomFit or ToolbarAction.ZoomActual => document is not null,
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
                ToolbarAction.ZoomFit => state.ZoomToFit,
                ToolbarAction.ZoomActual => !state.ZoomToFit && MathF.Abs(state.Zoom - 1f) < 0.001f,
                ToolbarAction.Enhance => state.IsEnhancing,
                ToolbarAction.Compare => Split.IsOn,

                _ => false,
            };
        }

        // The hovered button's tooltip and the anchor to hang it from, captured during the toolbar
        // paint. Render-thread only, rebuilt every frame.
        private (string Text, float X, float Y)? _hoveredTooltip;

        /// <summary>
        /// Draws the hovered toolbar button's tooltip. Called LAST in the frame so it paints over every
        /// other piece of chrome -- a tooltip that the file list or the info panel draws over is worse
        /// than none, because it looks like a rendering fault rather than a missing feature.
        /// </summary>
        private void RenderToolbarTooltip(ViewerState state)
        {
            // An open dropdown owns the pointer, so the button underneath must not also explain itself.
            // Stated ONCE on the state (see ViewerState.OverlayOwnsPointer) rather than as a term here,
            // which is how the cursor predicate this codebase retired went wrong.
            if (_hoveredTooltip is not { } tip || state.OverlayOwnsPointer || string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var fontSize = ToolbarFontSize;
            var textWidth = MeasureText(tip.Text, fontSize);
            var placed = OverlayPlacement.Place(OverlayPlacement.Anchor.Below, tip.X, tip.Y,
                textWidth, fontSize, DpiScale, Width, Height);
            var box = placed.Box;

            FillRect(box.X - 1f, box.Y - 1f, box.Width + 2f, box.Height + 2f, ViewerTheme.Palette.SeparatorStrong);
            FillRect(box.X, box.Y, box.Width, box.Height, ViewerTheme.Palette.PanelBg);
            DrawText(tip.Text.AsSpan(), FontPath, placed.TextX, box.Y, box.Width, box.Height,
                fontSize, ViewerTheme.Palette.BodyText, TextAlign.Near, TextAlign.Center);
        }

        /// <summary>Design-unit edge of the Bayer swatch, sized to the toolbar text beside it.</summary>
        private const float BaseBayerSwatchSize = 13f;

        // Extra width the Debayer button needs for its CFA swatch, or 0 when there is nothing to show.
        // The swatch is only meaningful for an actual Bayer mosaic; a mono or already-colour source
        // would get a picture of a pattern its pixels do not have.
        private float BayerSwatchWidth(ToolbarAction action)
            => action is ToolbarAction.Debayer && _source?.SensorType is SensorType.RGGB
                ? BaseBayerSwatchSize * DpiScale + ButtonPaddingH
                : 0f;

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
        private void DrawBayerSwatch(float x, float btnY, float btnH)
        {
            var size = BaseBayerSwatchSize * DpiScale;
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
                    FillRect(x + cx * cell, y + cy * cell, cell, cell, color);
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
        private static string? GetToolbarButtonTooltip(ToolbarAction action, ViewerState state) => action switch
        {
            ToolbarAction.Open => "Open a FITS / TIFF / SER file",
            ToolbarAction.StretchToggle => "Screen transfer function on / off (T)",
            ToolbarAction.StretchLink => "Stretch mode: unlinked, linked or luma",
            ToolbarAction.StretchParams => "Stretch strength preset (+ / -)",
            ToolbarAction.Channel => "Channel view: RGB or one channel (C cycles)",
            ToolbarAction.Debayer => "Demosaic algorithm; the swatch is the sensor's CFA phase (D cycles)",
            ToolbarAction.CurvesBoost => "Curves boost; right-click switches curve mode (B / Shift+B)",
            ToolbarAction.Hdr => "HDR highlight compression (H cycles)",
            ToolbarAction.Compare => "Before / after split; right-click re-pins (A / Shift+A)",
            ToolbarAction.ZoomFit => "Fit the image to the window (F / Ctrl+0)",
            ToolbarAction.ZoomActual => "Zoom to 1:1 (R / Ctrl+1)",
            ToolbarAction.PlateSolve => "Plate solve this frame (P)",
            ToolbarAction.Grid => "WCS coordinate grid (G)",
            ToolbarAction.Overlays => "Deep-sky object overlays (O)",
            ToolbarAction.Stars => "Detect stars and show HFD / FWHM (S)",
            ToolbarAction.ColorCalibrate => "Photometric colour calibration (W)",
            ToolbarAction.BackgroundNeutralize => "Neutralise the background (N)",
            ToolbarAction.SpccCalibrate => "Spectrophotometric colour calibration (W)",
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
        private static readonly ImmutableArray<string> ShortcutLines =
        [
            "Wheel / Ctrl+Wheel   Zoom",
            "Ctrl + / -           Zoom in / out",
            "Ctrl+2 .. Ctrl+9     Zoom 1:N",
            "V / Shift+V          Histogram / log scale",
            "I                    Info panel",
            "L                    File list",
            "K                    Raw / stacked view (sequence)",
            "Space                Play / pause (sequence)",
            "Left / Right         Step one frame",
            "Home / End           First / last frame",
            "Up / Down            Previous / next file",
            "F11                  Fullscreen",
            "Esc                  Quit",
        ];

        private string GetToolbarButtonLabel(string baseLabel, ToolbarAction action, AstroImageDocument? document, ViewerState state)
        {
            return action switch
            {
                ToolbarAction.StretchToggle => "STF",
                ToolbarAction.StretchLink => state.StretchMode switch
                {
                    StretchMode.Linked => "Linked",
                    StretchMode.Luma => "Luma",
                    _ => "Unlinked"
                },
                ToolbarAction.StretchParams => $"{state.StretchParameters}",
                ToolbarAction.Channel => state.ChannelView is ChannelView.Composite ? "RGB" : $"{state.ChannelView}",
                ToolbarAction.Debayer => state.DebayerAlgorithm.DisplayName,
                ToolbarAction.CurvesBoost => state.CurvesBoost > 0f ? $"Boost {state.CurvesBoost:P0}" : "Boost",
                ToolbarAction.Hdr => state.HdrAmount > 0f ? $"HDR: {state.HdrAmount:F1}" : "HDR",
                ToolbarAction.ZoomFit => "Fit",
                ToolbarAction.ZoomActual => "1:1",
                ToolbarAction.Grid => "Grid",
                ToolbarAction.Overlays when CelestialObjectDB is { IsValueCreated: false } => "Objects...",
                ToolbarAction.Overlays => "Objects",
                ToolbarAction.Stars when document?.Stars is null => "Stars...",
                ToolbarAction.Stars when document?.Stars is { Count: > 0 } s => $"Stars: {s.Count}",
                ToolbarAction.Stars => "Stars: 0",
                ToolbarAction.BackgroundNeutralize when state.BackgroundNeutralizationEnabled =>
                    state.BackgroundNeutralizationStrength >= 0.9999f
                        ? $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)}"
                        : $"NeutBg: {ShortMethodLabel(state.BackgroundNeutralizationMethod)} {state.BackgroundNeutralizationStrength:P0}",
                ToolbarAction.SpccCalibrate when state.ColorCalibrationEnabled => $"SPCC: {document?.ColorCalibration?.R:F2}/{document?.ColorCalibration?.B:F2}",
                ToolbarAction.PlateSolve when state.IsPlateSolving => "Solving...",
                ToolbarAction.PlateSolve when document?.IsPlateSolved == true => "Solved",
                ToolbarAction.Enhance when state.IsEnhancing => $"AI {state.EnhanceProgressPct:F0}%",
                // Show the selected backend (right-click cycles it); left-click runs the enhance.
                ToolbarAction.Enhance => state.PreferredEnhanceBackend switch
                {
                    EnhanceBackend.ForceRcAstro => "AI: RC",
                    EnhanceBackend.ForceSas => "AI: SAS",
                    EnhanceBackend.N2n => "AI: N2N",
                    _ => "AI: Auto",
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

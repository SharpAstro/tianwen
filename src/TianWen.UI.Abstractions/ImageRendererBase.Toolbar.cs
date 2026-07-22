using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> DefaultToolbarButtons =
        [
            ("Open", ToolbarAction.Open, 0),
            ("STF", ToolbarAction.StretchToggle, 1),
            ("Link", ToolbarAction.StretchLink, 1),
            ("Params", ToolbarAction.StretchParams, 1),
            ("Channel", ToolbarAction.Channel, 2),
            ("Debayer", ToolbarAction.Debayer, 2),
            ("Boost", ToolbarAction.CurvesBoost, 2),
            ("HDR", ToolbarAction.Hdr, 2),
            ("Fit", ToolbarAction.ZoomFit, 3),
            ("1:1", ToolbarAction.ZoomActual, 3),
            ("Plate Solve", ToolbarAction.PlateSolve, 4),
            ("Grid", ToolbarAction.Grid, 4),
            ("Objects", ToolbarAction.Overlays, 4),
            ("Stars", ToolbarAction.Stars, 4),
            ("Calibrate", ToolbarAction.ColorCalibrate, 4),
            ("NeutBg", ToolbarAction.BackgroundNeutralize, 4),
            ("SPCC", ToolbarAction.SpccCalibrate, 4),
        ];

        // The full set plus the AI "Enhance" button (group 4). A separate static array (not an
        // append-per-frame) keeps the per-frame render + hit-test loops allocation-free. Selected by
        // ToolbarButtons when the host sets EnhanceAvailable.
        private static readonly ImmutableArray<(string Label, ToolbarAction Action, int Group)> DefaultToolbarButtonsWithEnhance =
            DefaultToolbarButtons.Add(("Enhance", ToolbarAction.Enhance, 4));

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


        // -----------------------------------------------------------------------
        // Toolbar
        // -----------------------------------------------------------------------

        private void RenderToolbar(AstroImageDocument? document, ViewerState state)
        {
            var tb = _layout.Toolbar;
            FillRect(tb.X, tb.Y, tb.Width, tb.Height, ViewerTheme.ToolbarBg);

            // Recompute button bounds every frame — labels can change width
            // (e.g. "Stars" -> "Stars: 5893") which shifts later buttons.
            _toolbarButtonBounds.Clear();

            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var mouseX = state.MouseScreenPosition.X;
            var mouseY = state.MouseScreenPosition.Y;

            var x = tb.X + PanelPadding;
            var btnH = tb.Height - ButtonSpacing * 2;
            var btnY = tb.Y + ButtonSpacing;
            var textY = tb.Y + (tb.Height - ToolbarFontSize) / 2f;
            var prevGroup = -1;

            for (var i = 0; i < ToolbarButtons.Length; i++)
            {
                var (label, action, group) = ToolbarButtons[i];

                if (prevGroup >= 0 && group != prevGroup)
                {
                    x += ButtonGroupSpacing;
                }
                prevGroup = group;

                var displayLabel = GetToolbarButtonLabel(label, action, document, state);
                var textWidth = MeasureText(displayLabel, ToolbarFontSize);
                var btnW = textWidth + ButtonPaddingH * 2;
                var enabled = IsToolbarButtonEnabled(action, document);
                var active = IsToolbarButtonActive(action, document, state);

                var hovered = enabled && !state.ToolbarDropdown.IsOpen && mouseX >= x && mouseX < x + btnW && mouseY >= btnY && mouseY < btnY + btnH;

                if (!enabled)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonDisabledBg);
                }
                else if (active && hovered)
                {
                    // Active + hover = the brightest selection blue (matches ViewerTheme's selected role).
                    FillRect(x, btnY, btnW, btnH, ViewerTheme.Palette.Selection);
                }
                else if (active)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonActiveBg);
                }
                else if (hovered)
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonHoverBg);
                }
                else
                {
                    FillRect(x, btnY, btnW, btnH, ToolbarButtonBg);
                }

                var textBrightness = enabled ? 0.9f : 0.45f;
                DrawText(displayLabel, x + ButtonPaddingH, textY, ToolbarFontSize,
                    RGBAColor32.FromFloat(textBrightness, textBrightness, textBrightness, 1f));

                if (enabled)
                {
                    RegisterClickable(x, btnY, btnW, btnH, new HitResult.ButtonHit(action.ToString()));
                    // Capture rect so left-click can anchor the dropdown beneath the
                    // button (see OpenToolbarDropdown). Only enabled buttons can be
                    // clicked, so we only need their bounds.
                    _toolbarButtonBounds[action] = new RectF32(x, btnY, btnW, btnH);
                }

                x += btnW + ButtonSpacing;
            }
        }

        // -----------------------------------------------------------------------
        // Toolbar dropdowns — single shared overlay (only one open at a time)
        // -----------------------------------------------------------------------

        /// <summary>Captured bounds of each enabled toolbar button this frame —
        /// used as the anchor when opening that button's dropdown.</summary>
        private readonly Dictionary<ToolbarAction, RectF32> _toolbarButtonBounds = new();

        /// <summary>Cycle order + dropdown order for the stretch-mode selector.
        /// Mirrors <see cref="ViewerActions.StretchLinkModes"/> 1:1 so the click
        /// handler can index back into the enum array.</summary>
        private static readonly ImmutableArray<string> StretchLinkModeLabels = BuildLabels(
            ViewerActions.StretchLinkModes, m => m.ToString());

        /// <summary>Channel-view selector — Composite/Red/Green/Blue. Only
        /// surfaced for 3+ channel images (gated by <see cref="IsToolbarButtonEnabled"/>).</summary>
        private static readonly ChannelView[] ChannelViewOrder =
            [ChannelView.Composite, ChannelView.Red, ChannelView.Green, ChannelView.Blue];

        private static readonly ImmutableArray<string> ChannelViewLabels = BuildLabels(
            ChannelViewOrder, v => v switch { ChannelView.Composite => "RGB", _ => v.ToString() });

        /// <summary>Debayer-algorithm selector — all algorithms always shown. The click handler
        /// indexes this array directly, so the order is independent of the enum's numeric values.
        /// MHC sits next to the other Bayer-to-RGB algorithms; for the GPU live (RawBayer) path it
        /// and VNG/AHD all resolve to the shader's MHC demosaic (see <see cref="GpuDebayerMode"/>).</summary>
        private static readonly DebayerAlgorithm[] DebayerAlgorithmOrder =
            [DebayerAlgorithm.None, DebayerAlgorithm.BilinearMono, DebayerAlgorithm.MHC, DebayerAlgorithm.VNG, DebayerAlgorithm.AHD];

        private static readonly ImmutableArray<string> DebayerLabels = BuildLabels(
            DebayerAlgorithmOrder, a => a.DisplayName);

        /// <summary>Stretch-parameter preset labels — 8 (Factor, ShadowsClipping) presets.</summary>
        private static readonly ImmutableArray<string> StretchParamsLabels = BuildLabels(
            StretchParameters.Presets, p => p.ToString());

        /// <summary>Curves-boost preset labels — 0/25/50/100/150 %.</summary>
        private static readonly ImmutableArray<string> CurvesBoostLabels = BuildLabels(
            ViewerState.CurvesBoostPresets, b => b > 0f ? $"{b:P0}" : "Off");

        /// <summary>HDR preset labels — "Off" + 4 (amount, knee) combos.</summary>
        private static readonly ImmutableArray<string> HdrLabels = BuildLabels(
            ViewerState.HdrPresets, p => p.Amount > 0f ? $"{p.Amount:F1} / {p.Knee:F2}" : "Off");

        /// <summary>Background-neutralization preset table — combines method × strength
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
                            // "Off" entry — drop the document gain so the uniform reverts to identity
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
            state.ToolbarDropdown.Open(
                bounds.X,
                bounds.Y + bounds.Height,
                width,
                labels,
                onSelect);
            // Mark the current selection so the menu shows the active item on open
            // (RenderDropdownMenu highlights HighlightIndex; Open resets it to -1).
            state.ToolbarDropdown.HighlightIndex = selectedIndex;
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
                _ => false,
            };
        }

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
                ToolbarAction.Channel => $"Channel: {(state.ChannelView is ChannelView.Composite ? "RGB" : state.ChannelView)}",
                ToolbarAction.Debayer => $"Debayer: {state.DebayerAlgorithm.DisplayName}",
                ToolbarAction.CurvesBoost => $"Boost: {state.CurvesBoost:P0}",
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
                ToolbarAction.Enhance when state.IsEnhancing => $"Enhancing {state.EnhanceProgressPct:F0}%",
                // Show the selected backend (right-click cycles it); left-click runs the enhance.
                ToolbarAction.Enhance => state.PreferredEnhanceBackend switch
                {
                    EnhanceBackend.ForceRcAstro => "Enhance (RC)",
                    EnhanceBackend.ForceSas => "Enhance (SAS)",
                    _ => "Enhance (Auto)",
                },
                _ => baseLabel,
            };
        }

        /// <summary>
        /// Hit-tests the toolbar using actual rendered button widths for the current state.
        /// </summary>
        public ToolbarAction? HitTestToolbar(float screenX, float screenY, AstroImageDocument? document, ViewerState state)
        {
            var tb = _layout.Toolbar;
            if (screenY < tb.Y + ButtonSpacing || screenY >= tb.Y + tb.Height - ButtonSpacing || string.IsNullOrEmpty(FontPath))
            {
                return null;
            }

            var x = tb.X + PanelPadding;
            var prevGroup = -1;

            for (var i = 0; i < ToolbarButtons.Length; i++)
            {
                var (label, action, group) = ToolbarButtons[i];

                if (prevGroup >= 0 && group != prevGroup)
                {
                    x += ButtonGroupSpacing;
                }
                prevGroup = group;

                var displayLabel = GetToolbarButtonLabel(label, action, document, state);
                var textWidth = MeasureText(displayLabel, ToolbarFontSize);
                var btnW = textWidth + ButtonPaddingH * 2;

                if (screenX >= x && screenX < x + btnW)
                {
                    return IsToolbarButtonEnabled(action, document) ? action : null;
                }
                x += btnW + ButtonSpacing;
            }

            return null;
        }

    }
}

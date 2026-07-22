using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Preview mode: mini-viewer toolbar + stretch cycling, preview timeline, per-OTA capture controls, mount section.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        // Per-frame Fill-leaf painter dispatch for the OTA-panels' single RenderLayout -- shared by BOTH the
        // preview path (RenderPreviewOTAPanels) and the running path (RenderOTAPanels), which are mutually
        // exclusive per frame. Keyed by the Fill leaf's key; populated while building the column nodes and
        // drained by DispatchOtaPanelFill. Render-thread-only (both run on the paint path), so a plain
        // Dictionary is safe.
        private readonly Dictionary<string, Action<RectF32>> _otaPanelFills = new();

        private void DispatchOtaPanelFill(Layout.Content.Fill fill, RectF32 r)
        {
            if (fill.Key is { } k && _otaPanelFills.TryGetValue(k, out var painter)) painter(r);
        }

        /// <summary>
        /// Cycles the preview viewer's stretch mode None -&gt; Unlinked -&gt; Linked -&gt; Luma -&gt; None (the
        /// embedded preview's [T] button + key). Preserves the old mini viewer's 4-way cycle; the full viewer's
        /// own [T] is a 2-way toggle, but the preview keeps the cycle so all modes stay reachable chromeless.
        /// </summary>
        private static void CyclePreviewStretch(ViewerState s)
        {
            s.StretchMode = s.StretchMode switch
            {
                StretchMode.None => StretchMode.Unlinked,
                StretchMode.Unlinked => StretchMode.Linked,
                StretchMode.Linked => StretchMode.Luma,
                StretchMode.Luma => StretchMode.None,
                _ => StretchMode.Unlinked
            };
        }

        private void RenderMiniViewerToolbar(ViewerState vs, RectF32 rect, float fontSize)
        {
            RenderLayout(Layout.Builder.Spacer().Bg(HeaderBg), rect);

            var dpiScale = DpiScale;
            var pad = BasePadding * dpiScale;
            var btnW = 36f * dpiScale;
            var btnFs = fontSize * 0.8f;

            var activeBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);

            // The whole strip is ONE HStack of Clickable button nodes (was an `x += btnW + pad` cursor +
            // per-button RenderButton). Sizes are already device px, so the tree renders at dpiScale:1f.
            // A status Text takes the middle Star cell, which naturally right-aligns the OTA buttons.
            Layout.Node Btn(string label, float w, RGBAColor32 bg, RGBAColor32 fg, string action, Action<InputModifier> onClick) =>
                Layout.Builder.Text(label, btnFs, fg, TextAlign.Center, TextAlign.Center)
                    .WFixed(w).HStar().Bg(bg)
                    .Clickable(new HitResult.ButtonHit(action), onClick);

            var stretchLabel = vs.StretchMode switch
            {
                StretchMode.None => "Raw",
                StretchMode.Linked => "Lnk",
                StretchMode.Unlinked => "Unl",
                StretchMode.Luma => "Lum",
                _ => "T"
            };

            var nodes = new List<Layout.Node>
            {
                // [Fit] zoom to fit
                Btn("Fit", btnW, vs.ZoomToFit ? activeBg : inactiveBg, BodyText, "ViewerFit",
                    _ => { vs.ZoomToFit = true; }),
                // [1:1] actual pixels
                Btn("1:1", btnW, !vs.ZoomToFit && MathF.Abs(vs.Zoom - 1f) < 0.01f ? activeBg : inactiveBg, BodyText, "Viewer1to1",
                    _ => { vs.ZoomToFit = false; vs.Zoom = 1f; vs.PanOffset = (0, 0); }),
                // [T] cycle stretch mode
                Btn(stretchLabel, btnW, vs.StretchMode is not StretchMode.None ? activeBg : inactiveBg, BodyText, "ViewerStretch",
                    _ => { CyclePreviewStretch(vs); }),
                // [S] cycle stretch preset
                Btn("S", btnW * 0.8f, inactiveBg, BodyText, "ViewerPreset",
                    _ => { ViewerActions.CycleStretchPreset(vs); }),
                // [B] cycle boost
                Btn("B", btnW * 0.8f, vs.CurvesBoost > 0 ? activeBg : inactiveBg, BodyText, "ViewerBoost",
                    _ => { ViewerActions.CycleCurvesBoost(vs); }),
            };

            // [G] -- WCS coordinate grid overlay. Enabled only once the preview frame has been plate-solved
            // (we need a WCS to project RA/Dec lines). Lit when active. Polar-alignment mode switching now
            // lives on the top-strip mode pill dropdown -- the toolbar stays focused on viewer chrome.
            if (State is { } liveState)
            {
                var hasWcs = liveState.PreviewPlateSolveResult?.Solution is not null;
                var gridFg = hasWcs || vs.ShowGrid ? BodyText : DimText;
                nodes.Add(Btn("G", btnW * 0.6f, vs.ShowGrid ? activeBg : inactiveBg, gridFg, "ViewerGrid",
                    _ =>
                    {
                        if (hasWcs)
                        {
                            vs.ShowGrid = !vs.ShowGrid;
                            liveState.NeedsRedraw = true;
                        }
                    }));
            }

            // Status text (stretch info) fills the middle Star cell, pushing the OTA buttons to the right edge.
            var infoText = $"{vs.StretchMode} {vs.StretchParameters}";
            if (vs.CurvesBoost > 0)
            {
                infoText += $" Boost:{vs.CurvesBoost:F2}";
            }
            nodes.Add(Layout.Builder.Text(infoText, fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center).WStar().HStar());

            // OTA selector buttons (right-aligned) -- works in both session and preview mode.
            var otaButtonCount = State?.OtaCount ?? 0;
            if (otaButtonCount > 1)
            {
                for (var oi = 0; oi < otaButtonCount; oi++)
                {
                    var idx = oi; // capture
                    nodes.Add(Btn($"#{idx + 1}", btnW * 0.8f, vs.SelectedCameraIndex == idx ? activeBg : inactiveBg, BodyText, $"ViewerOTA{idx}",
                        _ => { vs.SelectedCameraIndex = vs.SelectedCameraIndex == idx ? -1 : idx; }));
                }
            }

            // Inset the row 2px vertically (the old btnY/btnH) inside the already-painted HeaderBg strip.
            var inner = new RectF32(rect.X + pad, rect.Y + 2f * dpiScale, rect.Width - pad * 2f, rect.Height - 4f * dpiScale);
            RenderLayout(Layout.Builder.HStack([.. nodes]).WithGap(pad), inner, dpiScale: 1f);
        }

        // Preview-mode twilight timeline moved to the paint-owning SessionTimelineRenderer
        // (RenderTwilightTimeline); RenderTimeline dispatches to it.

        // -----------------------------------------------------------------------
        // Bottom strip: per-OTA capture controls + mount section (ONE arranged tree)
        // -----------------------------------------------------------------------

        /// <summary>
        /// The preview per-OTA region is one arranged tree: a horizontal Stack of per-OTA column VStacks
        /// (1px dividers between) with the mount status docked to the bottom (full width). The engine lays
        /// the columns + rows out (no <c>px = i * panelW</c> column cursor, no <c>y += rowH</c> row cursor,
        /// no <c>maxY</c> reservation -- Dock.Bottom gives the columns the area above the mount strip and the
        /// panel clips overflow). The focuser-goto text input is a keyed <see cref="Layout.Content.Fill"/>
        /// leaf painted through <see cref="_otaPanelFills"/>; the capture progress bar is a declarative
        /// <see cref="FormRowLayout.ProgressBar"/> node.
        /// </summary>
        private void RenderPreviewOTAPanels(LiveSessionState state, RectF32 rect,
            float fontSize, float pad, float rowH, ITimeProvider timeProvider)
        {
            var fontPath = FontPath;
            var dpiScale = DpiScale;
            var preview = state.PreviewOTATelemetry;
            var otaCount = preview.Length;
            if (otaCount == 0)
            {
                DrawText("No OTAs configured in profile", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            _otaPanelFills.Clear();

            // Columns: one VStack per OTA, 1px full-height dividers between them.
            var columns = new List<Layout.Node>(otaCount * 2);
            for (var i = 0; i < otaCount; i++)
            {
                if (i > 0)
                {
                    columns.Add(Layout.Builder.Spacer().WFixed(1f).HStar().Bg(SeparatorColor));
                }
                columns.Add(BuildPreviewOtaColumn(state, i, fontSize, timeProvider).WStar());
            }
            var columnsRow = Layout.Builder.HStack([.. columns]);

            // Mount section pinned to the bottom (Dock.Bottom), gated on enough vertical room (mirrors the
            // old "return if the mount strip would eat more than 65% of the height" guard).
            var mountHpx = (BaseRowHeight * 4 + BasePadding) * dpiScale;
            var showMount = rect.Y + rect.Height - mountHpx > rect.Y + rect.Height * 0.35f;
            const float mountHDesign = BaseRowHeight * 4 + BasePadding * 2 + 1f;

            var tree = showMount
                ? Layout.Builder.Dock(columnsRow, Layout.Builder.Bottom(BuildPreviewMountSection(state), mountHDesign))
                : columnsRow;

            Renderer.PushClip(new RectInt(
                new PointInt((int)(rect.X + rect.Width), (int)(rect.Y + rect.Height)),
                new PointInt((int)rect.X, (int)rect.Y)));
            RenderLayout(tree, rect, drawFill: DispatchOtaPanelFill);
            Renderer.PopClip();
        }

        /// <summary>
        /// Builds one per-OTA preview column: camera name, temperature, focuser readout + jog + goto,
        /// filter, and capture controls, as a padded VStack. Focuser jog / goto and capture buttons carry
        /// their own click hits; the goto text-input is a keyed Fill.
        /// </summary>
        private Layout.Node BuildPreviewOtaColumn(LiveSessionState state, int i,
            float fontSize, ITimeProvider timeProvider)
        {
            var fontPath = FontPath;
            var tel = state.PreviewOTATelemetry[i];
            var smallFs = fontSize * 0.85f;
            var rows = new List<Layout.Node>();

            // Camera name (dim if not connected)
            rows.Add(Layout.Builder.Text(tel.CameraDisplayName, BaseFontSize, tel.CameraConnected ? HeaderText : DimText)
                .RowH(BaseRowHeight));

            // Temperature
            if (!double.IsNaN(tel.CcdTempC))
            {
                var tempColor = CameraTempColors[i % CameraTempColors.Length];
                var tempText = $"{tel.CcdTempC:F0}\u00b0C  {tel.CoolerPowerPct:F0}%";
                if (!double.IsNaN(tel.SetpointC))
                {
                    tempText += $"  \u2192 {tel.SetpointC:F0}\u00b0C";
                }
                rows.Add(Layout.Builder.Text(tempText, BaseFontSize * 0.85f, tempColor).RowH(BaseRowHeight));
            }
            rows.Add(Layout.Builder.Spacer().RowH(BasePadding));

            // Focuser readout + jog controls (fine +-10, coarse +-100) + goto row
            if (tel.FocuserConnected)
            {
                var focLabel = $"Foc: {tel.FocusPosition}";
                if (!double.IsNaN(tel.FocuserTempC))
                {
                    focLabel += $"  {tel.FocuserTempC:F1}\u00b0C";
                }
                if (tel.FocuserIsMoving)
                {
                    focLabel += "  \u21c4";
                }
                rows.Add(Layout.Builder.Text(focLabel, BaseFontSize, tel.FocuserIsMoving ? StatusSlewing : BodyText).RowH(BaseRowHeight));

                var capturedI = i;
                var jogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);

                // Jog buttons row: [<<] [<] "10 | 100" [>] [>>] as one HStack.
                Layout.Node JogBtn(string glyph, string action, int delta) =>
                    Layout.Builder.Text(glyph, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(32f).HStar().Bg(jogBg)
                        .Clickable(new HitResult.ButtonHit(action), _ => PostSignal(new JogFocuserSignal(capturedI, delta)));

                rows.Add(Layout.Builder.HStack(
                        JogBtn("\u00ab", $"FocCoarseIn{capturedI}", -100),
                        JogBtn("\u2039", $"FocFineIn{capturedI}", -10),
                        Layout.Builder.Text("10 | 100", BaseFontSize * 0.85f * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                        JogBtn("\u203a", $"FocFineOut{capturedI}", 10),
                        JogBtn("\u00bb", $"FocCoarseOut{capturedI}", 100))
                    .WithGap(2f).RowH(BaseRowHeight));

                // Goto-position row: numeric input pre-filled with the current focuser step + a "Go" button.
                if (capturedI < state.FocuserGotoInputs.Length)
                {
                    var input = state.FocuserGotoInputs[capturedI];
                    if (!input.IsActive && string.IsNullOrEmpty(input.Text))
                    {
                        input.Text = tel.FocusPosition.ToString();
                        input.CursorPos = input.Text.Length;
                    }
                    input.OnCommit = text =>
                    {
                        if (int.TryParse(text, out var pos))
                        {
                            PostSignal(new GotoFocuserSignal(capturedI, pos));
                        }
                        return Task.CompletedTask;
                    };

                    var gotoKey = $"focGoto:{capturedI}";
                    _otaPanelFills[gotoKey] = r => RenderTextInput(input, r, fontPath, smallFs);
                    rows.Add(Layout.Builder.HStack(
                            Layout.Builder.Fill(key: gotoKey).Stretch(),
                            Layout.Builder.Spacer().WFixed(4f).HStar(),
                            Layout.Builder.Text("Go", BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                                .WFixed(32f).HStar().Bg(jogBg)
                                .Clickable(new HitResult.ButtonHit($"FocGoto{capturedI}"), _ =>
                                {
                                    if (int.TryParse(input.Text, out var pos))
                                    {
                                        PostSignal(new GotoFocuserSignal(capturedI, pos));
                                    }
                                }))
                        .RowH(BaseRowHeight));
                }
            }
            else
            {
                rows.Add(Layout.Builder.Text("Foc: \u2014", BaseFontSize, DimText).RowH(BaseRowHeight));
            }

            // Filter
            rows.Add(tel.FilterWheelConnected
                ? Layout.Builder.Text($"FW: {tel.FilterName}", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight)
                : Layout.Builder.Text("FW: \u2014", BaseFontSize * 0.85f, DimText).RowH(BaseRowHeight));

            // Capture controls
            rows.Add(Layout.Builder.Spacer().RowH(BasePadding));
            rows.Add(BuildPreviewCaptureControls(state, i, fontSize, timeProvider));

            return Layout.Builder.VStack([.. rows]).Pad(BasePadding);
        }

        /// <summary>
        /// Builds the per-OTA capture-controls block: either a "Capturing x/ys" line + progress bar (while a
        /// preview exposure runs), or an exposure stepper + [Capture], an optional gain stepper, and optional
        /// [Save]/[Solve] once a preview image exists. Returned as a VStack node (the progress bar is a keyed
        /// Fill leaf; everything else is declarative).
        /// </summary>
        private Layout.Node BuildPreviewCaptureControls(LiveSessionState state, int otaIndex,
            float fontSize, ITimeProvider timeProvider)
        {
            var smallFs = fontSize * 0.85f;
            var rows = new List<Layout.Node>();
            var isCapturing = otaIndex < state.PreviewCapturing.Length && state.PreviewCapturing[otaIndex];

            if (isCapturing)
            {
                // Progress bar + elapsed/total
                var start = state.PreviewCaptureStart[otaIndex];
                var dur = state.PreviewExposureDuration[otaIndex];
                var elapsed = timeProvider.GetUtcNow() - start;
                var fraction = dur.TotalSeconds > 0
                    ? (float)Math.Min(elapsed.TotalSeconds / dur.TotalSeconds, 1.0)
                    : 0f;
                rows.Add(Layout.Builder.Text($"Capturing {elapsed.TotalSeconds:F0}/{dur.TotalSeconds:F0}s",
                    BaseFontSize * 0.85f, HeaderText).RowH(BaseRowHeight));

                rows.Add(FormRowLayout.ProgressBar(fraction, ProgressBg, ProgressFill).RowH(BaseProgressBarH));
                return Layout.Builder.VStack([.. rows]).WStar();
            }

            // Exposure row: [-] value [+]   [Capture]. The stepper fills the row minus the right-anchored
            // [Capture] button; the [-] value [+] control is one declarative node (each cell its own draw==hit).
            var expSec = otaIndex < state.PreviewExposureSeconds.Length
                ? state.PreviewExposureSeconds[otaIndex] : 5.0;
            var expCtrl = FormRowLayout.StepperControl(PreviewStepperStyle,
                "-", $"ExpDec{otaIndex}",
                _ =>
                {
                    if (otaIndex >= state.PreviewExposureSeconds.Length) return;
                    state.PreviewExposureSeconds[otaIndex] = LiveSessionActions.StepExposure(
                        state.PreviewExposureSeconds[otaIndex], direction: -1);
                },
                "+", $"ExpInc{otaIndex}",
                _ =>
                {
                    if (otaIndex >= state.PreviewExposureSeconds.Length) return;
                    state.PreviewExposureSeconds[otaIndex] = LiveSessionActions.StepExposure(
                        state.PreviewExposureSeconds[otaIndex], direction: +1);
                },
                $"Exp: {LiveSessionActions.FormatExposureLabel(expSec)}", BaseFontSize * 0.85f, BodyText, enabled: true);

            // [Capture] -- disabled while polar alignment is running so a manual exposure can't interleave
            // with the PolarAlignmentSession's own captures (different frame settings, breaks the solve cadence).
            var polarActive = state.Mode == LiveSessionMode.PolarAlign;
            var captureBtnColor = polarActive
                ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                : new RGBAColor32(0x33, 0x66, 0x33, 0xff);
            var captureBtnText = polarActive ? DimText : BrightText;
            rows.Add(Layout.Builder.HStack(
                    expCtrl.Stretch(),
                    Layout.Builder.Spacer().WFixed(4f).HStar(),
                    Layout.Builder.Text("Capture", BaseFontSize * 0.85f, captureBtnText, TextAlign.Center, TextAlign.Center)
                        .WFixed(72f).HStar().Bg(captureBtnColor)
                        .Clickable(new HitResult.ButtonHit($"PreviewCapture{otaIndex}"), polarActive ? null : _ =>
                        {
                            var exp = otaIndex < state.PreviewExposureSeconds.Length
                                ? state.PreviewExposureSeconds[otaIndex] : 5.0;
                            PostSignal(new TakePreviewSignal(otaIndex, exp,
                                otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null,
                                otaIndex < state.PreviewBinning.Length ? state.PreviewBinning[otaIndex] : (short)1));
                        }))
                .RowH(BaseRowHeight));

            // Gain row: [-] value [+] (only if camera supports gain value or gain mode).
            var tel = otaIndex < state.PreviewOTATelemetry.Length
                ? state.PreviewOTATelemetry[otaIndex]
                : PreviewOTATelemetry.Unknown;
            var hasGainControl = (tel.UsesGainValue && tel.GainMax > tel.GainMin)
                || (tel.UsesGainMode && tel.GainModes.Length > 0);
            if (hasGainControl)
            {
                var gainVal = otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null;
                var gainLabel = LiveSessionActions.FormatGainLabel(gainVal, tel);
                var gainCtrl = FormRowLayout.StepperControl(PreviewStepperStyle,
                    "-", $"GainDec{otaIndex}",
                    _ =>
                    {
                        if (otaIndex >= state.PreviewGain.Length) return;
                        state.PreviewGain[otaIndex] = LiveSessionActions.StepGain(
                            state.PreviewGain[otaIndex], tel, direction: -1);
                    },
                    "+", $"GainInc{otaIndex}",
                    _ =>
                    {
                        if (otaIndex >= state.PreviewGain.Length) return;
                        state.PreviewGain[otaIndex] = LiveSessionActions.StepGain(
                            state.PreviewGain[otaIndex], tel, direction: +1);
                    },
                    gainLabel, BaseFontSize * 0.85f, gainVal.HasValue ? BodyText : DimText, enabled: true);
                rows.Add(gainCtrl.RowH(BaseRowHeight));
            }

            // [Save] and [Solve] only appear if a preview image exists for this OTA.
            var hasImage = otaIndex < state.LastCapturedImages.Length
                && state.LastCapturedImages[otaIndex] is not null;
            if (hasImage)
            {
                var solving = otaIndex < state.PreviewPlateSolving.Length
                    && state.PreviewPlateSolving[otaIndex];
                var solveLabel = solving ? "Solving\u2026" : "Solve";
                var solveBg = solving
                    ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                    : new RGBAColor32(0x22, 0x44, 0x66, 0xff);
                var solveText = solving ? DimText : BrightText;
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Text("Save", BaseFontSize * 0.85f, BrightText, TextAlign.Center, TextAlign.Center)
                            .WStar().HStar().Bg(new RGBAColor32(0x22, 0x55, 0x44, 0xff))
                            .Clickable(new HitResult.ButtonHit($"PreviewSave{otaIndex}"), _ => PostSignal(new SaveSnapshotSignal(otaIndex))),
                        Layout.Builder.Spacer().WFixed(4f).HStar(),
                        Layout.Builder.Text(solveLabel, BaseFontSize * 0.85f, solveText, TextAlign.Center, TextAlign.Center)
                            .WStar().HStar().Bg(solveBg)
                            .Clickable(new HitResult.ButtonHit($"PreviewSolve{otaIndex}"), solving ? null : _ => PostSignal(new PlateSolvePreviewSignal(otaIndex))))
                    .RowH(BaseRowHeight * 0.9f));
            }

            return Layout.Builder.VStack([.. rows]).WStar();
        }

        /// <summary>
        /// Builds the bottom-pinned mount status block (dot + name, RA, Dec, status/pier/HA) as a VStack,
        /// prefixed by a full-width hairline divider (a keyed Fill 1px line). Docked full-width at the panel
        /// bottom by <see cref="RenderPreviewOTAPanels"/>.
        /// </summary>
        private Layout.Node BuildPreviewMountSection(LiveSessionState state)
        {
            var ms = state.MountState;
            var dotColor = ms.IsSlewing ? StatusSlewing : ms.IsTracking ? StatusTracking : DimText;
            var statusText = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
            var pierLabel = ms.PierSide switch
            {
                TianWen.Lib.Devices.PointingState.Normal => "Normal",
                TianWen.Lib.Devices.PointingState.ThroughThePole => "Through Pole",
                _ => "?"
            };
            statusText += $"  Pier: {pierLabel}  HA: {ms.HourAngle:F2}h";

            var content = Layout.Builder.VStack(
                    Layout.Builder.HStack(
                            Layout.Builder.Text("\u25cf", BaseFontSize * 0.7f, dotColor, TextAlign.Center, TextAlign.Center).WFixed(BaseRowHeight * 0.6f).HStar(),
                            Layout.Builder.Text(state.MountDisplayName ?? "Mount", BaseFontSize * 0.85f, HeaderText).WStar().HStar())
                        .RowH(BaseRowHeight),
                    Layout.Builder.Text($"RA {ms.RightAscension:F4}h", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text($"Dec {ms.Declination:F3}\u00b0", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text(statusText, BaseFontSize * 0.85f, ms.IsSlewing ? StatusSlewing : DimText).RowH(BaseRowHeight))
                .Pad(BasePadding);

            // Full-width hairline divider above the block (a coloured Box node, not a Fill painter).
            return Layout.Builder.VStack(
                Layout.Builder.Spacer().RowH(1f).Bg(SeparatorColor),
                content);
        }

        // -----------------------------------------------------------------------
        // Right panel: exposure log
        // -----------------------------------------------------------------------
    }
}

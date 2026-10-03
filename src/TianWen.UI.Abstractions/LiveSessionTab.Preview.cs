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
        /// The preview host's part of a toolbar press (<see cref="ImageRendererBase{TSurface}.ToolbarPressPolicy"/>):
        /// the solve, which the node runs on the frame on show, for that frame's OTA. Every other press is the
        /// viewer's own.
        /// </summary>
        private bool PressPreviewToolbar(ViewerState viewerState, ToolbarAction action, MouseButton button)
        {
            if (action is not ToolbarAction.PlateSolve)
            {
                return false;
            }
            // The node solves the still it keeps, never a live view's frame, which it keeps nowhere.
            if (!_showingLive && State is { } state && _displayedOta is { } ota && !IsPreviewSolving(state, ota))
            {
                PostSignal(new PlateSolvePreviewSignal(ota));
            }
            return true;
        }

        /// <summary>
        /// Whether the solve can run (<see cref="ImageRendererBase{TSurface}.HostCanRun"/>): a frame on show, not
        /// placed yet, and no solve of it going on. Null for every other action, which the viewer judges itself.
        /// </summary>
        private bool? PreviewCanRun(ToolbarAction action) => action is ToolbarAction.PlateSolve
            ? !_showingLive && State is { } state && _displayedOta is { } ota && !IsPreviewSolving(state, ota) && !_previewSource.Findings.IsPlateSolved
            : null;

        private static bool IsPreviewSolving(LiveSessionState state, int ota)
            => ota < state.PreviewPlateSolving.Length && state.PreviewPlateSolving[ota];

        /// <summary>
        /// With more than one OTA, whose frame the preview shows: a button each, the one shown lit, and a second
        /// press on it back to Auto (the first OTA with a frame). The viewer's own controls are on its toolbar; this
        /// row is what is left of the toolbar the tab drew for it while it was chromeless.
        /// </summary>
        private void RenderOtaPicker(ViewerState vs, RectF32 rect, float fontSize)
        {
            RenderLayout(Layout.Builder.Spacer().Bg(HeaderBg), rect);

            var dpiScale = DpiScale;
            var pad = BasePadding * dpiScale;
            var btnW = 36f * dpiScale * 0.8f;
            var btnFs = fontSize * 0.8f;

            // Sizes are already device px, so the tree renders at DesignScale.One. A Star spacer takes the
            // middle, which right-aligns the buttons, where they have always been.
            var nodes = new List<Layout.Node> { Layout.Builder.Spacer().WStar() };
            var otaButtonCount = State?.OtaCount ?? 0;
            for (var oi = 0; oi < otaButtonCount; oi++)
            {
                var idx = oi; // capture
                var bg = vs.SelectedCameraIndex == idx ? GuiTheme.PrimaryButtonBg : GuiTheme.NeutralButtonBg;
                nodes.Add(Layout.Builder.Text($"#{idx + 1}", btnFs, BodyText, TextAlign.Center, TextAlign.Center)
                    .WFixed(btnW).HStar().Bg(bg).BgHover(GuiTheme.Hover(bg))
                    .Clickable(new HitResult.ButtonHit($"ViewerOTA{idx}"),
                        _ => { vs.SelectedCameraIndex = vs.SelectedCameraIndex == idx ? -1 : idx; }));
            }

            // Inset the row 2px vertically inside the already-painted HeaderBg strip.
            var inner = new RectF32(rect.X + pad, rect.Y + 2f * dpiScale, rect.Width - pad * 2f, rect.Height - 4f * dpiScale);
            RenderLayout(Layout.Builder.HStack([.. nodes]).WithGap(pad), inner, scale: DesignScale.One);
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
        /// panel clips overflow). The focuser-goto field is a <see cref="Layout.Content.TextInput"/> leaf and
        /// the capture progress bar a <see cref="Layout.Builder.Progress"/> node, so both are declarations
        /// rather than draw callbacks.
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
                    columns.Add(Layout.Builder.Box(1f, 0f, SeparatorColor).WFixed(1f).HStar());
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

            PushClip(rect.X, rect.Y, rect.Width, rect.Height);
            RenderLayout(tree, rect, drawFill: DispatchOtaPanelFill);
            PopClip();
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
            var rows = new List<Layout.Node>();

            // Camera name (dim if not connected)
            rows.Add(Layout.Builder.Text(tel.CameraDisplayName, BaseFontSize, tel.CameraConnected ? HeaderText : DimText)
                .RowH(BaseRowHeight));

            // Temperature
            if (!double.IsNaN(tel.CcdTempC))
            {
                var tempColor = CameraTempColors[i % CameraTempColors.Length];
                // An uncooled camera (an ASI462MC) reports no cooler power, which reads back as NaN: say nothing
                // rather than "NaN%", as the TUI row does.
                var tempText = double.IsNaN(tel.CoolerPowerPct)
                    ? $"{tel.CcdTempC:F0}\u00b0C"
                    : $"{tel.CcdTempC:F0}\u00b0C  {tel.CoolerPowerPct:F0}%";
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
                var jogBg = GuiTheme.NeutralButtonBg;

                // Jog buttons row: [<<] [<] "10 | 100" [>] [>>] as one HStack.
                Layout.Node JogBtn(string glyph, string action, int delta) =>
                    FormRowLayout.StepMark(glyph, BaseFontSize * 0.85f, BodyText)
                        .WFixed(32f).HStar().Bg(jogBg).BgHover(GuiTheme.Hover(jogBg))
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

                    rows.Add(Layout.Builder.HStack(
                            // One field per OTA, created as the rig reports its focusers.
                            Layout.Builder.TextInput(input, BaseFontSize * 0.85f).Stretch(),
                            Layout.Builder.Text("Go", BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                                .WFixed(32f).HStar().Bg(jogBg).BgHover(GuiTheme.Hover(jogBg))
                                .Clickable(new HitResult.ButtonHit($"FocGoto{capturedI}"), _ =>
                                {
                                    if (int.TryParse(input.Text, out var pos))
                                    {
                                        PostSignal(new GotoFocuserSignal(capturedI, pos));
                                    }
                                }))
                        .WithGap(4f).RowH(BaseRowHeight));
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
                // The job is longer than the exposure (release, download over USB, decode, transfer), so past it say what
                // it is doing instead of counting on beyond the exposure, and keep a sub-second exposure readable.
                var label = elapsed < dur
                    ? $"Exposing {elapsed.TotalSeconds:0.#}/{dur.TotalSeconds:0.###}s"
                    : "Reading out the frame\u2026";
                rows.Add(Layout.Builder.Text(label, BaseFontSize * 0.85f, HeaderText).RowH(BaseRowHeight));

                rows.Add(Layout.Builder.Progress(fraction, ProgressBg, ProgressFill).RowH(BaseProgressBarH));

                // A body that sends no picture leaves this bar up for as long as the node's job lasts: a way out that is not
                // the window's quit.
                rows.Add(Layout.Builder.Text("Stop", BaseFontSize * 0.85f, BrightText, TextAlign.Center, TextAlign.Center)
                    .HStar().Bg(GuiTheme.DangerButtonBg).BgHover(GuiTheme.Hover(GuiTheme.DangerButtonBg))
                    .Clickable(new HitResult.ButtonHit($"PreviewStop{otaIndex}"), _ => PostSignal(new StopPreviewSignal(otaIndex)))
                    .RowH(BaseRowHeight));
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
                    PushLiveExposure(state, otaIndex);
                },
                "+", $"ExpInc{otaIndex}",
                _ =>
                {
                    if (otaIndex >= state.PreviewExposureSeconds.Length) return;
                    state.PreviewExposureSeconds[otaIndex] = LiveSessionActions.StepExposure(
                        state.PreviewExposureSeconds[otaIndex], direction: +1);
                    PushLiveExposure(state, otaIndex);
                },
                $"Exp: {LiveSessionActions.FormatExposureLabel(expSec)}", BaseFontSize * 0.85f, BodyText, enabled: true);

            // [Capture] -- disabled while polar alignment is running so a manual exposure can't interleave
            // with the PolarAlignmentSession's own captures (different frame settings, breaks the solve cadence).
            var polarActive = state.Mode == LiveSessionMode.PolarAlign;
            var captureBtnColor = polarActive
                ? GuiTheme.NeutralButtonBg
                : GuiTheme.GoButtonBg;
            var captureBtnText = polarActive ? DimText : BrightText;
            var captureBtn = Layout.Builder.Text("Capture", BaseFontSize * 0.85f, captureBtnText, TextAlign.Center, TextAlign.Center)
                .WFixed(72f).HStar().Bg(captureBtnColor)
                .Clickable(new HitResult.ButtonHit($"PreviewCapture{otaIndex}"), polarActive ? null : _ =>
                {
                    var exp = otaIndex < state.PreviewExposureSeconds.Length
                        ? state.PreviewExposureSeconds[otaIndex] : 5.0;
                    PostSignal(new TakePreviewSignal(otaIndex, exp,
                        otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null,
                        otaIndex < state.PreviewBinning.Length ? state.PreviewBinning[otaIndex] : (short)1));
                });
            if (!polarActive) captureBtn = captureBtn.BgHover(GuiTheme.Hover(captureBtnColor));
            rows.Add(Layout.Builder.HStack(expCtrl.Stretch(), captureBtn)
                .WithGap(4f).RowH(BaseRowHeight));

            // [Live]: a live view of this camera in the pane (#1111), lit while it runs, with the camera's rate beside it.
            // Pressed again it stops; [Capture] ends it and takes the still.
            rows.Add(BuildLiveViewRow(state, otaIndex, polarActive));

            // The camera's own lens drive while it is live (#681): only then does a Canon move its lens.
            if (LiveView is { } live && live.LiveOta == otaIndex && live.CanDriveLens)
            {
                rows.Add(BuildLensRow(live));
            }

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
                        PushLiveGain(state, otaIndex);
                    },
                    "+", $"GainInc{otaIndex}",
                    _ =>
                    {
                        if (otaIndex >= state.PreviewGain.Length) return;
                        state.PreviewGain[otaIndex] = LiveSessionActions.StepGain(
                            state.PreviewGain[otaIndex], tel, direction: +1);
                        PushLiveGain(state, otaIndex);
                    },
                    gainLabel, BaseFontSize * 0.85f, gainVal.HasValue ? BodyText : DimText, enabled: true);
                rows.Add(gainCtrl.RowH(BaseRowHeight));
            }

            // [Save] only appears if a preview image exists for this OTA. The solve is the viewer's toolbar
            // button now, for the frame on show (PressPreviewToolbar), so a column no longer carries one.
            // Not while this camera is live: the pane shows the live view, and Save writes the still the node keeps.
            var hasImage = otaIndex < state.LastCapturedImages.Length
                && state.LastCapturedImages[otaIndex] is not null
                && LiveView?.LiveOta != otaIndex;
            if (hasImage)
            {
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Text("Save", BaseFontSize * 0.85f, BrightText, TextAlign.Center, TextAlign.Center)
                            .WStar().HStar().Bg(GuiTheme.GoButtonBg).BgHover(GuiTheme.Hover(GuiTheme.GoButtonBg))
                            .Clickable(new HitResult.ButtonHit($"PreviewSave{otaIndex}"), _ => PostSignal(new SaveSnapshotSignal(otaIndex))))
                    .RowH(BaseRowHeight * 0.9f));
            }

            return Layout.Builder.VStack([.. rows]).WStar();
        }

        /// <summary>
        /// The live view's row (#1111): a [Live] toggle, lit while this OTA's camera streams, and what the node last said of
        /// it (its rate and frame size), or why it stopped. Another OTA's live view leaves this one's button as a start,
        /// which the node refuses in the run's name.
        /// </summary>
        private Layout.Node BuildLiveViewRow(LiveSessionState state, int otaIndex, bool polarActive)
        {
            var live = LiveView;
            var isLive = live?.LiveOta == otaIndex;
            var enabled = live is not null && !polarActive;
            var bg = isLive ? GuiTheme.PrimaryButtonBg : GuiTheme.NeutralButtonBg;
            var button = Layout.Builder.Text("Live", BaseFontSize * 0.85f, enabled ? BrightText : DimText, TextAlign.Center, TextAlign.Center)
                .WFixed(72f).HStar().Bg(bg)
                .Clickable(new HitResult.ButtonHit($"PreviewLive{otaIndex}"), !enabled ? null : _ =>
                {
                    if (isLive)
                    {
                        PostSignal(new StopLiveViewSignal());
                        return;
                    }
                    var exp = otaIndex < state.PreviewExposureSeconds.Length ? state.PreviewExposureSeconds[otaIndex] : 0.1;
                    PostSignal(new StartLiveViewSignal(otaIndex, exp,
                        otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null,
                        otaIndex < state.PreviewBinning.Length ? state.PreviewBinning[otaIndex] : (short)1));
                });
            if (enabled)
            {
                button = button.BgHover(GuiTheme.Hover(bg));
            }

            var status = live?.State is not { } s || s.OtaIndex != otaIndex ? ""
                : isLive ? s.FramesPerSecond is { } fps && s.Width > 0
                    ? $"{fps:0.#} fps, {live.ShownFps:0} shown  {s.Width}x{s.Height}"
                    : "Starting…"
                : s.FailureReason ?? "";
            return Layout.Builder.HStack(button, Layout.Builder.Text(status, BaseFontSize * 0.8f, DimText).WStar().HStar())
                .WithGap(4f).RowH(BaseRowHeight);
        }

        /// <summary>
        /// The lens row while a camera that drives its own lens is live (#681, P12 of docs/plans/live-session-preview.md): Near
        /// in the body's large, medium and small step, then Far in the small, medium and large, each button showing its size as
        /// one to three carets. Steps only, as the body has no position to show. A click is one step; a button HELD repeats it
        /// (<see cref="LiveViewController.BeginLensHold"/>) and is lit while it does, its press owning the gesture until the
        /// button comes up.
        /// </summary>
        private Layout.Node BuildLensRow(LiveViewController live)
        {
            var iconSize = BaseFontSize * 0.85f * Layout.Content.Icon.TextSizeRatio;
            var held = live.HeldLensStep;

            Layout.Node Step(LensFocusStep step)
            {
                var bg = held == step ? GuiTheme.PrimaryButtonBg : GuiTheme.NeutralButtonBg;
                var size = Math.Abs((int)step);
                var kind = (sbyte)step < 0 ? Layout.IconKind.CaretLeft : Layout.IconKind.CaretRight;
                var marks = new List<Layout.Node> { Layout.Builder.Spacer().WStar() };
                for (var m = 0; m < size; m++)
                {
                    marks.Add(Layout.Builder.Icon(kind, iconSize, BodyText).HStar());
                }
                marks.Add(Layout.Builder.Spacer().WStar());
                return Layout.Builder.HStack([.. marks])
                    .WFixed(28f).HStar().Bg(bg).BgHover(GuiTheme.Hover(bg))
                    .Pressable(new HitResult.ButtonHit($"Lens{step}"), _ =>
                    {
                        live.BeginLensHold(step);
                        return new DragCapture(static _ => { }, _ => live.EndLensHold());
                    });
            }

            return Layout.Builder.HStack(
                    Step(LensFocusStep.NearLarge), Step(LensFocusStep.NearMedium), Step(LensFocusStep.NearSmall),
                    Layout.Builder.Text("Lens", BaseFontSize * 0.85f * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                    Step(LensFocusStep.FarSmall), Step(LensFocusStep.FarMedium), Step(LensFocusStep.FarLarge))
                .WithGap(2f).RowH(BaseRowHeight);
        }

        // A stepper's new value, sent to this OTA's live view as it changes; nothing while it is not live.
        private void PushLiveExposure(LiveSessionState state, int otaIndex)
        {
            if (LiveView is { } live && live.LiveOta == otaIndex && otaIndex < state.PreviewExposureSeconds.Length)
            {
                live.SetExposure(TimeSpan.FromSeconds(state.PreviewExposureSeconds[otaIndex]));
            }
        }

        private void PushLiveGain(LiveSessionState state, int otaIndex)
        {
            if (LiveView is { } live && live.LiveOta == otaIndex && otaIndex < state.PreviewGain.Length && state.PreviewGain[otaIndex] is { } gain)
            {
                live.SetGain((short)Math.Clamp(gain, 0, short.MaxValue));
            }
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
            // An unknown pointing (no mount connected, or none read yet) says so rather than printing NaN.
            statusText += double.IsNaN(ms.HourAngle) ? $"  Pier: {pierLabel}  HA: --" : $"  Pier: {pierLabel}  HA: {ms.HourAngle:F2}h";

            var content = Layout.Builder.VStack(
                    Layout.Builder.HStack(
                            Layout.Builder.Text("\u25cf", BaseFontSize * 0.7f, dotColor, TextAlign.Center, TextAlign.Center).WFixed(BaseRowHeight * 0.6f).HStar(),
                            Layout.Builder.Text(state.MountDisplayName ?? "Mount", BaseFontSize * 0.85f, HeaderText).WStar().HStar())
                        .RowH(BaseRowHeight),
                    Layout.Builder.Text(double.IsNaN(ms.RightAscension) ? "RA --" : $"RA {ms.RightAscension:F4}h", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text(double.IsNaN(ms.Declination) ? "Dec --" : $"Dec {ms.Declination:F3}\u00b0", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text(statusText, BaseFontSize * 0.85f, ms.IsSlewing ? StatusSlewing : DimText).RowH(BaseRowHeight))
                .Pad(BasePadding);

            // Full-width hairline divider above the block (a coloured Box node, not a Fill painter).
            return Layout.Builder.VStack(
                Layout.Builder.Box(0f, 1f, SeparatorColor).RowH(1f),
                content);
        }

        // -----------------------------------------------------------------------
        // Right panel: exposure log
        // -----------------------------------------------------------------------
    }
}

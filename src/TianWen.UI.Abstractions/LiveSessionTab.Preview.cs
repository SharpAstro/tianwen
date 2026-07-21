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

        private void RenderMiniViewerToolbar(ViewerState vs, RectF32 rect, string fontPath, float fontSize, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var btnW = 36f * dpiScale;
            var btnH = rect.Height - 4 * dpiScale;
            var btnY = rect.Y + 2 * dpiScale;
            var btnFs = fontSize * 0.8f;
            var x = rect.X + pad;

            var activeBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);

            // [Fit] \u2014 zoom to fit
            RenderButton("Fit", x, btnY, btnW, btnH, fontPath, btnFs,
                vs.ZoomToFit ? activeBg : inactiveBg, BodyText, "ViewerFit",
                _ => { vs.ZoomToFit = true; });
            x += btnW + pad;

            // [1:1] \u2014 actual pixels
            RenderButton("1:1", x, btnY, btnW, btnH, fontPath, btnFs,
                !vs.ZoomToFit && MathF.Abs(vs.Zoom - 1f) < 0.01f ? activeBg : inactiveBg, BodyText, "Viewer1to1",
                _ => { vs.ZoomToFit = false; vs.Zoom = 1f; vs.PanOffset = (0, 0); });
            x += btnW + pad;

            // [T] \u2014 cycle stretch mode
            var stretchLabel = vs.StretchMode switch
            {
                StretchMode.None => "Raw",
                StretchMode.Linked => "Lnk",
                StretchMode.Unlinked => "Unl",
                StretchMode.Luma => "Lum",
                _ => "T"
            };
            RenderButton(stretchLabel, x, btnY, btnW, btnH, fontPath, btnFs,
                vs.StretchMode is not StretchMode.None ? activeBg : inactiveBg, BodyText, "ViewerStretch",
                _ => { CyclePreviewStretch(vs); });
            x += btnW + pad;

            // [S] \u2014 cycle stretch preset
            var presetLabel = $"{vs.StretchParameters}";
            RenderButton("S", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                inactiveBg, BodyText, "ViewerPreset",
                _ => { ViewerActions.CycleStretchPreset(vs); });
            x += btnW * 0.8f + pad;

            // [B] \u2014 cycle boost
            var boostActive = vs.CurvesBoost > 0;
            RenderButton("B", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                boostActive ? activeBg : inactiveBg, BodyText, "ViewerBoost",
                _ => { ViewerActions.CycleCurvesBoost(vs); });
            x += btnW * 0.8f + pad;

            // [G] -- WCS coordinate grid overlay. Enabled only once the preview frame
            // has been plate-solved (we need a WCS to project RA/Dec lines). Lit when
            // active. Polar-alignment mode switching now lives on the top-strip mode
            // pill dropdown -- the toolbar stays focused on viewer chrome.
            if (State is { } liveState)
            {
                var hasWcs = liveState.PreviewPlateSolveResult?.Solution is not null;
                var gridActive = vs.ShowGrid;
                var gridBg = gridActive ? activeBg : inactiveBg;
                var gridFg = hasWcs || gridActive ? BodyText : DimText;
                RenderButton("G", x, btnY, btnW * 0.6f, btnH, fontPath, btnFs,
                    gridBg, gridFg, "ViewerGrid",
                    _ =>
                    {
                        if (hasWcs)
                        {
                            vs.ShowGrid = !vs.ShowGrid;
                            liveState.NeedsRedraw = true;
                        }
                    });
                x += btnW * 0.6f + pad;
            }

            // OTA selector buttons (right-aligned) \u2014 works in both session and preview mode
            var otaButtonCount = State?.OtaCount ?? 0;
            if (otaButtonCount > 1)
            {
                var otaBtnX = rect.X + rect.Width - (btnW * 0.8f + pad) * otaButtonCount - pad;
                for (var oi = 0; oi < otaButtonCount; oi++)
                {
                    var idx = oi; // capture
                    var isSelected = vs.SelectedCameraIndex == idx;
                    RenderButton($"#{idx + 1}", otaBtnX, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                        isSelected ? activeBg : inactiveBg, BodyText, $"ViewerOTA{idx}",
                        _ => { vs.SelectedCameraIndex = vs.SelectedCameraIndex == idx ? -1 : idx; });
                    otaBtnX += btnW * 0.8f + pad;
                }
            }

            // Status text: stretch info
            var infoText = $"{vs.StretchMode} {vs.StretchParameters}";
            if (vs.CurvesBoost > 0)
            {
                infoText += $" Boost:{vs.CurvesBoost:F2}";
            }
            var infoW = rect.X + rect.Width - x - pad;
            if (otaButtonCount > 1)
            {
                infoW -= (btnW * 0.8f + pad) * otaButtonCount;
            }
            DrawText(infoText, fontPath,
                x, rect.Y, infoW, rect.Height,
                fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center);
        }

        /// <summary>
        /// Preview mode timeline: twilight bands + now needle.
        /// Shows civil/nautical/astronomical twilight zones so the user knows when dark arrives.
        /// </summary>
        private void RenderPreviewTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            if (state.AstroDark == default)
            {
                DrawText("Twilight data loading\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var pad = BasePadding * dpiScale;
            var barH = 24f * dpiScale;
            var now = timeProvider.GetUtcNow();

            // Time range: 15 min before civil set \u2192 15 min after civil rise
            var tStart = (state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1)) - TimeSpan.FromMinutes(15);
            var tEnd = (state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1)) + TimeSpan.FromMinutes(15);
            var totalSeconds = Math.Max((tEnd - tStart).TotalSeconds, 600);
            var barY = rect.Y + pad;

            float TimeToX(DateTimeOffset t) =>
                rect.X + pad + (float)((t - tStart).TotalSeconds / totalSeconds) * (rect.Width - pad * 2);

            // Twilight zone colors
            var civilColor = new RGBAColor32(0x44, 0x44, 0x22, 0x88);
            var nautColor = new RGBAColor32(0x22, 0x33, 0x55, 0x88);
            var astroColor = new RGBAColor32(0x11, 0x22, 0x44, 0x88);
            var nightColor = new RGBAColor32(0x00, 0x00, 0x22, 0xcc);

            // Fill the twilight bands
            if (state.CivilSet is { } cs)
            {
                FillRect(TimeToX(tStart), barY, TimeToX(cs) - TimeToX(tStart), barH, civilColor);
            }
            if (state.NauticalSet is { } ns)
            {
                var nsX = TimeToX(ns);
                var fromX = state.CivilSet is { } cs2 ? TimeToX(cs2) : TimeToX(tStart);
                FillRect(fromX, barY, nsX - fromX, barH, nautColor);
            }
            {
                var astroStartX = state.NauticalSet is { } ns2 ? TimeToX(ns2) : (state.CivilSet is { } cs3 ? TimeToX(cs3) : TimeToX(tStart));
                var darkX = TimeToX(state.AstroDark);
                FillRect(astroStartX, barY, darkX - astroStartX, barH, astroColor);
            }
            // Night (dark)
            {
                var darkX = TimeToX(state.AstroDark);
                var dawnX = TimeToX(state.AstroTwilight);
                FillRect(darkX, barY, dawnX - darkX, barH, nightColor);
            }
            // Dawn side: astro \u2192 nautical \u2192 civil (mirror)
            {
                var dawnX = TimeToX(state.AstroTwilight);
                var astroEndX = state.NauticalRise is { } nr ? TimeToX(nr) : (state.CivilRise is { } cr ? TimeToX(cr) : TimeToX(tEnd));
                FillRect(dawnX, barY, astroEndX - dawnX, barH, astroColor);
            }
            if (state.NauticalRise is { } nRise)
            {
                var nrX = TimeToX(nRise);
                var toX = state.CivilRise is { } cr2 ? TimeToX(cr2) : TimeToX(tEnd);
                FillRect(nrX, barY, toX - nrX, barH, nautColor);
            }
            if (state.CivilRise is { } cRise)
            {
                FillRect(TimeToX(cRise), barY, TimeToX(tEnd) - TimeToX(cRise), barH, civilColor);
            }

            // Now needle
            if (now >= tStart && now <= tEnd)
            {
                var nowX = TimeToX(now);
                FillRect(nowX, barY - 2, 2 * dpiScale, barH + 4, NowNeedleColor);
            }

            // Time axis ticks
            var axisY = barY + barH + 2;
            var axisH = rect.Height - barH - pad * 2 - 2;
            if (axisH > 4)
            {
                var rangeMins = totalSeconds / 60.0;
                var tickMins = rangeMins < 120 ? 10 : 30;
                var tickStart = new DateTimeOffset(tStart.Year, tStart.Month, tStart.Day,
                    tStart.Hour, (int)(tStart.Minute / tickMins) * (int)tickMins, 0, tStart.Offset);
                for (var t = tickStart; t <= tEnd; t = t.AddMinutes(tickMins))
                {
                    if (t < tStart) continue;
                    var tx = TimeToX(t);
                    if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                    FillRect(tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                    DrawText(t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
                        tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                        fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
                }
            }
        }

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
        private void RenderPreviewOTAPanels(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH, ITimeProvider timeProvider)
        {
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
                columns.Add(BuildPreviewOtaColumn(state, i, fontSize, dpiScale, fontPath, timeProvider).WStar());
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
            RenderLayout(tree, rect, fontPath, dpiScale, drawFill: DispatchOtaPanelFill);
            Renderer.PopClip();
        }

        /// <summary>
        /// Builds one per-OTA preview column: camera name, temperature, focuser readout + jog + goto,
        /// filter, and capture controls, as a padded VStack. Focuser jog / goto and capture buttons carry
        /// their own click hits; the goto text-input is a keyed Fill.
        /// </summary>
        private Layout.Node BuildPreviewOtaColumn(LiveSessionState state, int i,
            float fontSize, float dpiScale, string fontPath, ITimeProvider timeProvider)
        {
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
                    _otaPanelFills[gotoKey] = r => RenderTextInput(input, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, smallFs);
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
            rows.Add(BuildPreviewCaptureControls(state, i, fontSize, dpiScale, fontPath, timeProvider));

            return Layout.Builder.VStack([.. rows]).Pad(BasePadding);
        }

        /// <summary>
        /// Builds the per-OTA capture-controls block: either a "Capturing x/ys" line + progress bar (while a
        /// preview exposure runs), or an exposure stepper + [Capture], an optional gain stepper, and optional
        /// [Save]/[Solve] once a preview image exists. Returned as a VStack node (the progress bar is a keyed
        /// Fill leaf; everything else is declarative).
        /// </summary>
        private Layout.Node BuildPreviewCaptureControls(LiveSessionState state, int otaIndex,
            float fontSize, float dpiScale, string fontPath, ITimeProvider timeProvider)
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

using System;
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

            // [Fit] — zoom to fit
            RenderButton("Fit", x, btnY, btnW, btnH, fontPath, btnFs,
                vs.ZoomToFit ? activeBg : inactiveBg, BodyText, "ViewerFit",
                _ => { vs.ZoomToFit = true; });
            x += btnW + pad;

            // [1:1] — actual pixels
            RenderButton("1:1", x, btnY, btnW, btnH, fontPath, btnFs,
                !vs.ZoomToFit && MathF.Abs(vs.Zoom - 1f) < 0.01f ? activeBg : inactiveBg, BodyText, "Viewer1to1",
                _ => { vs.ZoomToFit = false; vs.Zoom = 1f; vs.PanOffset = (0, 0); });
            x += btnW + pad;

            // [T] — cycle stretch mode
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

            // [S] — cycle stretch preset
            var presetLabel = $"{vs.StretchParameters}";
            RenderButton("S", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                inactiveBg, BodyText, "ViewerPreset",
                _ => { ViewerActions.CycleStretchPreset(vs); });
            x += btnW * 0.8f + pad;

            // [B] — cycle boost
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

            // OTA selector buttons (right-aligned) — works in both session and preview mode
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

            // Time range: 15 min before civil set → 15 min after civil rise
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
            // Dawn side: astro → nautical → civil (mirror)
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
        // Bottom strip: compact guide graph + RMS + ABORT
        // -----------------------------------------------------------------------

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

            var panelW = rect.Width / otaCount;
            var progressH = BaseProgressBarH * dpiScale;
            var smallFs = fontSize * 0.85f;

            for (var i = 0; i < otaCount; i++)
            {
                var tel = preview[i];
                var px = rect.X + i * panelW;
                if (i > 0)
                {
                    FillRect(px, rect.Y, 1, rect.Height, SeparatorColor);
                }

                var y = rect.Y + pad;
                var textW = panelW - pad * 2;
                // Reserve space for mount section at the bottom
                var maxY = rect.Y + rect.Height - rowH * 5;

                // Camera name (dim if not connected)
                var nameColor = tel.CameraConnected ? HeaderText : DimText;
                DrawText(tel.CameraDisplayName, fontPath,
                    px + pad, y, textW, rowH,
                    fontSize, nameColor, TextAlign.Near, TextAlign.Center);
                y += rowH;

                // Temperature from PreviewOTATelemetry
                if (!double.IsNaN(tel.CcdTempC))
                {
                    var tempColor = CameraTempColors[i % CameraTempColors.Length];
                    var tempText = $"{tel.CcdTempC:F0}\u00B0C  {tel.CoolerPowerPct:F0}%";
                    if (!double.IsNaN(tel.SetpointC))
                    {
                        tempText += $"  \u2192 {tel.SetpointC:F0}\u00B0C";
                    }
                    DrawText(tempText, fontPath,
                        px + pad, y, textW, rowH, smallFs, tempColor, TextAlign.Near, TextAlign.Center);
                    y += rowH + pad;
                }
                else
                {
                    y += pad;
                }

                // Focuser readout + jog controls (fine ±10, coarse ±100)
                if (y < maxY)
                {
                    if (tel.FocuserConnected)
                    {
                        var focLabel = $"Foc: {tel.FocusPosition}";
                        if (!double.IsNaN(tel.FocuserTempC))
                        {
                            focLabel += $"  {tel.FocuserTempC:F1}\u00B0C";
                        }
                        if (tel.FocuserIsMoving)
                        {
                            focLabel += "  \u21C4";
                        }
                        DrawText(focLabel, fontPath,
                            px + pad, y, textW, rowH,
                            fontSize, tel.FocuserIsMoving ? StatusSlewing : BodyText, TextAlign.Near, TextAlign.Center);
                        y += rowH;

                        // Capture index once for both the jog button closures
                        // (5 buttons) and the goto-position row's OnCommit /
                        // Go button below.
                        var capturedI = i;

                        // Jog buttons row: [<<] [<] "10 | 100" [>] [>>] as one HStack (was five
                        // hand-positioned RenderButton/DrawText calls advancing a jogX cursor).
                        if (y < maxY)
                        {
                            var jogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
                            Layout.Node JogBtn(string glyph, string action, int delta) =>
                                Layout.Builder.Text(glyph, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                                    .WFixed(32f).HStar().Bg(jogBg)
                                    .Clickable(new HitResult.ButtonHit(action), _ => PostSignal(new JogFocuserSignal(capturedI, delta)));

                            var jogRow = Layout.Builder.HStack(
                                    JogBtn("\u00AB", $"FocCoarseIn{capturedI}", -100),
                                    JogBtn("\u2039", $"FocFineIn{capturedI}", -10),
                                    Layout.Builder.Text("10 | 100", BaseFontSize * 0.85f * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                                    JogBtn("\u203A", $"FocFineOut{capturedI}", 10),
                                    JogBtn("\u00BB", $"FocCoarseOut{capturedI}", 100))
                                .WithGap(2f).RowH(BaseRowHeight);
                            RenderLayout(jogRow, new RectF32(px + pad, y, textW, rowH), fontPath, dpiScale);
                            y += rowH;
                        }

                        // Goto-position row: numeric input pre-filled with the
                        // current focuser step + a "Go" button. Posts
                        // GotoFocuserSignal which routes to focuser.BeginMoveAsync.
                        // The input is parsed as int on commit; non-numeric
                        // values are ignored silently.
                        if (y < maxY && capturedI < state.FocuserGotoInputs.Length)
                        {
                            var input = state.FocuserGotoInputs[capturedI];
                            // Refresh placeholder/text to current position when
                            // not actively being edited so the user always sees
                            // a sensible starting value to tweak.
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

                            var jogBg = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
                            var rowBtnH = rowH * 0.85f;
                            var rowBtnY = y + (rowH - rowBtnH) / 2;
                            var goBtnW = 32f * dpiScale;
                            var inputW = textW - goBtnW - 4f;
                            RenderTextInput(input, (int)(px + pad), (int)rowBtnY,
                                (int)inputW, (int)rowBtnH, fontPath, smallFs);
                            RenderButton("Go", px + pad + inputW + 4f, rowBtnY, goBtnW, rowBtnH,
                                fontPath, smallFs, jogBg, BodyText, $"FocGoto{capturedI}",
                                _ =>
                                {
                                    if (int.TryParse(input.Text, out var pos))
                                    {
                                        PostSignal(new GotoFocuserSignal(capturedI, pos));
                                    }
                                });
                            y += rowH;
                        }
                    }
                    else
                    {
                        DrawText("Foc: \u2014", fontPath,
                            px + pad, y, textW, rowH,
                            fontSize, DimText, TextAlign.Near, TextAlign.Center);
                        y += rowH;
                    }
                }

                // Filter
                if (y < maxY)
                {
                    if (tel.FilterWheelConnected)
                    {
                        DrawText($"FW: {tel.FilterName}", fontPath,
                            px + pad, y, textW, rowH, smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    }
                    else
                    {
                        DrawText("FW: \u2014", fontPath,
                            px + pad, y, textW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
                    }
                    y += rowH;
                }

                // Capture controls
                y += pad;
                if (y < maxY)
                {
                    RenderPreviewCaptureControls(state, i, px + pad, y, textW, progressH, rowH,
                        fontPath, fontSize, smallFs, dpiScale, timeProvider);
                }
            }

            // Mount section (pinned to bottom, full width)
            RenderPreviewMountSection(state, rect, fontPath, fontSize, dpiScale, pad, rowH);
        }

        private void RenderPreviewCaptureControls(LiveSessionState state, int otaIndex,
            float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, ITimeProvider timeProvider)
        {
            var isCapturing = otaIndex < state.PreviewCapturing.Length && state.PreviewCapturing[otaIndex];
            var capBtnW = 72f * dpiScale;

            if (isCapturing)
            {
                // Show progress bar + elapsed/total
                var start = state.PreviewCaptureStart[otaIndex];
                var dur = state.PreviewExposureDuration[otaIndex];
                var elapsed = timeProvider.GetUtcNow() - start;
                var fraction = dur.TotalSeconds > 0
                    ? (float)Math.Min(elapsed.TotalSeconds / dur.TotalSeconds, 1.0)
                    : 0f;
                DrawText($"Capturing {elapsed.TotalSeconds:F0}/{dur.TotalSeconds:F0}s",
                    fontPath, x, y, w, rowH, smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;
                FillRect(x, y, w, progressH, ProgressBg);
                FillRect(x, y, w * fraction, progressH, ProgressFill);
                return;
            }

            // Exposure row: [-] value [+]   [Capture]. The stepper fills the row minus the
            // right-anchored [Capture] button; the [-] value [+] control is one declarative node
            // (each cell its own draw==hit leaf) instead of two RenderButton calls bracketing a
            // hand-positioned value DrawText.
            var expSec = otaIndex < state.PreviewExposureSeconds.Length
                ? state.PreviewExposureSeconds[otaIndex] : 5.0;

            var expStepW = w - capBtnW - 4 * dpiScale;
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
            RenderLayout(expCtrl, new RectF32(x, y, expStepW, rowH), fontPath, dpiScale);

            // [Capture] button -- disabled while polar alignment is running so the
            // user can't fire a manual exposure that would interleave with the
            // PolarAlignmentSession's own captures (different frame settings, breaks
            // the two-frame solve / refine cadence).
            var polarActive = state.Mode == LiveSessionMode.PolarAlign;
            var captureBtnColor = polarActive
                ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                : new RGBAColor32(0x33, 0x66, 0x33, 0xff);
            var captureBtnText = polarActive ? DimText : BrightText;
            RenderButton("Capture", x + w - capBtnW, y, capBtnW, rowH * 0.9f,
                fontPath, smallFs, captureBtnColor, captureBtnText,
                $"PreviewCapture{otaIndex}",
                polarActive ? null : _ =>
                {
                    var exp = otaIndex < state.PreviewExposureSeconds.Length
                        ? state.PreviewExposureSeconds[otaIndex] : 5.0;
                    PostSignal(new TakePreviewSignal(otaIndex, exp,
                        otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null,
                        otaIndex < state.PreviewBinning.Length ? state.PreviewBinning[otaIndex] : (short)1));
                });
            y += rowH;

            // Gain row: [-] value [+]  (only if camera supports gain value or gain mode)
            var tel = otaIndex < state.PreviewOTATelemetry.Length
                ? state.PreviewOTATelemetry[otaIndex]
                : PreviewOTATelemetry.Unknown;

            // Gain row — numeric (ZWO/ASCOM) and mode (DSLR ISO) share the same layout;
            // LiveSessionActions picks the right step semantics based on the telemetry.
            // Bracketed label = camera default; plain = user override (rendered brighter).
            var hasGainControl = (tel.UsesGainValue && tel.GainMax > tel.GainMin)
                || (tel.UsesGainMode && tel.GainModes.Length > 0);
            if (hasGainControl)
            {
                var gainVal = otaIndex < state.PreviewGain.Length ? state.PreviewGain[otaIndex] : null;
                var gainLabel = LiveSessionActions.FormatGainLabel(gainVal, tel);

                // [-] gain-value [+] as one declarative stepper (each cell draw==hit) replacing the
                // two RenderButton calls + hand-positioned value DrawText.
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
                RenderLayout(gainCtrl, new RectF32(x, y, w, rowH), fontPath, dpiScale);
                y += rowH;
            }

            // [Save] and [Solve] only appear if a preview image exists for this OTA
            var hasImage = otaIndex < state.LastCapturedImages.Length
                && state.LastCapturedImages[otaIndex] is not null;
            if (hasImage)
            {
                var halfW = (w - 4 * dpiScale) / 2;
                var saveBtnColor = new RGBAColor32(0x22, 0x55, 0x44, 0xff);
                var solveBtnColor = new RGBAColor32(0x22, 0x44, 0x66, 0xff);
                RenderButton("Save", x, y, halfW, rowH * 0.9f,
                    fontPath, smallFs, saveBtnColor, BrightText,
                    $"PreviewSave{otaIndex}",
                    _ => PostSignal(new SaveSnapshotSignal(otaIndex)));
                var solving = otaIndex < state.PreviewPlateSolving.Length
                    && state.PreviewPlateSolving[otaIndex];
                var solveLabel = solving ? "Solving\u2026" : "Solve";
                var solveBg = solving
                    ? new RGBAColor32(0x33, 0x33, 0x33, 0xff)
                    : solveBtnColor;
                var solveText = solving ? DimText : BrightText;
                RenderButton(solveLabel, x + halfW + 4 * dpiScale, y, halfW, rowH * 0.9f,
                    fontPath, smallFs, solveBg, solveText,
                    $"PreviewSolve{otaIndex}",
                    solving ? null : _ => PostSignal(new PlateSolvePreviewSignal(otaIndex)));
            }
        }

        private void RenderPreviewMountSection(LiveSessionState state, RectF32 rect,
            string fontPath, float fontSize, float dpiScale, float pad, float rowH)
        {
            var mountY = rect.Y + rect.Height - rowH * 4 - pad;
            if (mountY <= rect.Y + rect.Height * 0.35f)
            {
                return;
            }

            FillRect(rect.X, mountY, rect.Width, 1, SeparatorColor); // hairline divider stays a 1px line

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

            // Name row (dot + name), RA, Dec, status as one padded VStack (was a mountY cursor).
            var mountTree = Layout.Builder.VStack(
                    Layout.Builder.HStack(
                            Layout.Builder.Text("\u25CF", BaseFontSize * 0.7f, dotColor, TextAlign.Center, TextAlign.Center).WFixed(BaseRowHeight * 0.6f).HStar(),
                            Layout.Builder.Text(state.MountDisplayName ?? "Mount", BaseFontSize * 0.85f, HeaderText).WStar().HStar())
                        .RowH(BaseRowHeight),
                    Layout.Builder.Text($"RA {ms.RightAscension:F4}h", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text($"Dec {ms.Declination:F3}\u00B0", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    Layout.Builder.Text(statusText, BaseFontSize * 0.85f, ms.IsSlewing ? StatusSlewing : DimText).RowH(BaseRowHeight))
                .Pad(BasePadding);
            RenderLayout(mountTree, new RectF32(rect.X, mountY, rect.Width, rect.Y + rect.Height - mountY), fontPath, dpiScale);
        }

        // -----------------------------------------------------------------------
        // Right panel: exposure log
        // -----------------------------------------------------------------------
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic Session configuration tab. Left: scrollable SessionConfiguration form.
    /// Right: per-OTA camera settings + observation list.
    /// </summary>
    public class SessionTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Layout constants (at 1x scale)
        private const float BaseFontSize      = 14f;
        private const float BaseItemHeight    = 26f;
        private const float BaseHeaderHeight  = 28f;
        private const float BasePadding       = 8f;
        private const float BaseStepperBtnW   = 28f;
        private const float BaseValueW        = 80f;
        private const float BaseLabelW        = 160f;
        private const float BaseObsRowHeight  = 26f;
        private const float BaseSeparatorW    = 1f;
        private const float BaseObsPanelWidth = 440f;

        // Colors
        private static readonly RGBAColor32 ContentBg      = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg        = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg       = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText     = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText       = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText        = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 SeparatorColor = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 StepperBg      = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
        private static readonly RGBAColor32 ToggleOnBg     = new RGBAColor32(0x30, 0x60, 0x40, 0xff);
        private static readonly RGBAColor32 ToggleOffBg    = new RGBAColor32(0x40, 0x30, 0x30, 0xff);
        private static readonly RGBAColor32 CycleBg        = new RGBAColor32(0x30, 0x50, 0x80, 0xff);
        private static readonly RGBAColor32 RowAltBg       = new RGBAColor32(0x1a, 0x1a, 0x24, 0xff);
        private static readonly RGBAColor32 AccentColor    = new RGBAColor32(0x66, 0xbb, 0xff, 0xff);
        private static readonly RGBAColor32 WarningColor   = new RGBAColor32(0xff, 0xd7, 0x00, 0xff);
        private static readonly RGBAColor32 HintText       = new RGBAColor32(0x55, 0x55, 0x66, 0xff);
        private static readonly RGBAColor32 OtaHeaderBg    = new RGBAColor32(0x24, 0x24, 0x32, 0xff);
        private static readonly RGBAColor32 FrameCountText = new RGBAColor32(0x88, 0xdd, 0x88, 0xff);
        private static readonly RGBAColor32 SelectedRowBg  = new RGBAColor32(0x20, 0x30, 0x50, 0xff);

        private float _totalConfigHeight;

        /// <summary>Y positions (relative to scroll origin) of each config field, for scroll-to-visible.</summary>
        private readonly List<float> _fieldYPositions = [];

        /// <summary>Per-observation exposure value hit regions for double-click-to-edit.</summary>
        private readonly List<(RectF32 Rect, int ProposalIndex)> _exposureValueRegions = [];

        /// <summary>Cached reference to planner state for HandleInput access.</summary>
        private PlannerState? _plannerState;

        /// <summary>Cached reference to time provider for observation list rendering.</summary>
        private TimeProvider? _timeProvider;

        /// <summary>Tab state (configuration values, per-OTA camera settings, scroll offset).</summary>
        public SessionTabState State { get; } = new SessionTabState();

        /// <summary>Bounding rect of the scrollable config panel (set during render).</summary>
        public RectF32 ConfigPanelRect { get; private set; }

        /// <summary>Scroll line height in pixels (for mouse wheel step size).</summary>
        public float ScrollLineHeight { get; private set; }


        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        public void Render(
            GuiAppState appState,
            PlannerState plannerState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            TimeProvider? timeProvider = null)
        {
            BeginFrame();
            _plannerState = plannerState;
            _timeProvider = timeProvider;
            _exposureValueRegions.Clear();
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            ScrollLineHeight = BaseItemHeight * dpiScale;

            // Reinitialize per-OTA settings when the profile changes
            if (State.NeedsReinitialization(appState.ActiveProfile))
            {
                State.InitializeFromProfile(appState.ActiveProfile, appState.DeviceUriRegistry);
            }

            var layout = new PixelLayout(contentRect);

            // Right panel: camera settings + observation list
            var obsPanelW = BaseObsPanelWidth * dpiScale;
            var obsRect = layout.Dock(PixelDockStyle.Right, obsPanelW);

            // Vertical separator
            var sepRect = layout.Dock(PixelDockStyle.Right, BaseSeparatorW * dpiScale);
            FillRect(sepRect.X, sepRect.Y, sepRect.Width, sepRect.Height, SeparatorColor);

            // Left panel: config form (fills remaining)
            var configRect = layout.Fill();
            ConfigPanelRect = configRect;

            RenderConfigForm(configRect, dpiScale, fontPath);
            RenderRightPanel(plannerState, obsRect, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt) => evt switch
        {
            InputEvent.Scroll(var scrollY, var mouseX, var mouseY, _)
                when ConfigPanelRect.Contains(mouseX, mouseY) => HandleConfigScroll(scrollY),
            InputEvent.MouseDown(var px, var py, _, _, var clicks) when clicks >= 2
                => HandleDoubleClick(px, py),
            InputEvent.KeyDown(var key, _) => HandleConfigKey(key),
            _ => false
        };

        private bool HandleConfigScroll(float scrollY)
        {
            State.ConfigScrollOffset = Math.Max(0,
                State.ConfigScrollOffset - (int)(scrollY * ScrollLineHeight));
            State.NeedsRedraw = true;
            return true;
        }

        private bool HandleConfigKey(InputKey key)
        {
            switch (key)
            {
                case InputKey.Up:
                    if (State.SelectedFieldIndex > 0)
                    {
                        State.SelectedFieldIndex--;
                        EnsureFieldVisible();
                        State.NeedsRedraw = true;
                    }
                    return true;

                case InputKey.Down:
                    if (State.SelectedFieldIndex < State.FieldCount - 1)
                    {
                        State.SelectedFieldIndex++;
                        EnsureFieldVisible();
                        State.NeedsRedraw = true;
                    }
                    return true;

                case InputKey.Left when !State.IsSessionRunning:
                    State.DecrementSelectedField();
                    return true;

                case InputKey.Right when !State.IsSessionRunning:
                case InputKey.Enter when !State.IsSessionRunning:
                    State.IncrementSelectedField();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Scrolls the config panel so the selected field is visible.
        /// </summary>
        private void EnsureFieldVisible()
        {
            // Approximate: each field is one row, groups add header + gap
            // Use the stored _fieldYPositions if available, otherwise estimate
            if (State.SelectedFieldIndex >= 0 && _fieldYPositions.Count > State.SelectedFieldIndex)
            {
                var fieldY = _fieldYPositions[State.SelectedFieldIndex];
                var scaledItemH = ScrollLineHeight;

                if (fieldY < State.ConfigScrollOffset)
                {
                    State.ConfigScrollOffset = (int)fieldY;
                }
                else if (fieldY + scaledItemH > State.ConfigScrollOffset + ConfigPanelRect.Height)
                {
                    State.ConfigScrollOffset = (int)(fieldY + scaledItemH - ConfigPanelRect.Height);
                }
            }
        }

        private bool HandleDoubleClick(float px, float py)
        {
            if (State.IsSessionRunning || _plannerState is null)
            {
                return false;
            }

            for (var i = 0; i < _exposureValueRegions.Count; i++)
            {
                var (rect, proposalIdx) = _exposureValueRegions[i];
                if (!rect.Contains(px, py))
                {
                    continue;
                }

                var defaultExpSec = SessionContent.DefaultExposureSeconds(State);
                var p = _plannerState.Proposals[proposalIdx];
                var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                var capturedIdx = proposalIdx;

                State.EditingExposureIndex = capturedIdx;
                State.ExposureInput.OnCommit = text =>
                {
                    if (SessionTabState.TryParseExposureInput(text, out var newExp))
                    {
                        _plannerState.Proposals[capturedIdx] = _plannerState.Proposals[capturedIdx] with { SubExposure = newExp };
                    }
                    State.EditingExposureIndex = -1;
                    PostSignal(new DeactivateTextInputSignal());
                    State.NeedsRedraw = true;
                    return Task.CompletedTask;
                };
                State.ExposureInput.OnCancel = () =>
                {
                    State.EditingExposureIndex = -1;
                    PostSignal(new DeactivateTextInputSignal());
                    State.NeedsRedraw = true;
                };
                State.ExposureInput.Activate($"{(int)cur.TotalSeconds}");
                State.ExposureInput.SelectAll();
                PostSignal(new ActivateTextInputSignal(State.ExposureInput));
                State.NeedsRedraw = true;
                return true;
            }

            return false;
        }

        // -----------------------------------------------------------------------
        // Right panel: camera settings (top) + observation list (bottom)
        // -----------------------------------------------------------------------

        // Button colors
        private static readonly RGBAColor32 StartBtnBg      = new RGBAColor32(0x22, 0x66, 0x22, 0xff);
        private static readonly RGBAColor32 StartBtnText     = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 DisabledBtnBg    = new RGBAColor32(0x33, 0x33, 0x3a, 0xff);
        private static readonly RGBAColor32 DisabledBtnText  = new RGBAColor32(0x66, 0x66, 0x77, 0xff);

        private void RenderRightPanel(
            PlannerState plannerState,
            RectF32 rect,
            float dpiScale,
            string fontPath)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            var rightLayout = new PixelLayout(rect);
            var headerH = BaseHeaderHeight * dpiScale;
            var padding = BasePadding * dpiScale;
            var itemH = BaseItemHeight * dpiScale;

            // Camera settings section height: header + per-OTA rows
            var cameraCount = State.CameraSettings.Count;
            var cameraRowsPerOta = 3; // setpoint + gain + separator
            var cameraSectionH = headerH + Math.Max(cameraCount, 1) * (cameraRowsPerOta * itemH + padding);

            var cameraRect = rightLayout.Dock(PixelDockStyle.Top, cameraSectionH);
            var sepRect = rightLayout.Dock(PixelDockStyle.Top, BaseSeparatorW * dpiScale);
            FillRect(sepRect.X, sepRect.Y, sepRect.Width, sepRect.Height, SeparatorColor);

            // Start Session button: enabled when proposals exist and date is tonight
            var hasPinned = plannerState.Proposals.Count > 0;
            var isTonight = !plannerState.PlanningDate.HasValue;

            if (hasPinned)
            {
                var btnH = 36f * dpiScale;
                var btnRect = rightLayout.Dock(PixelDockStyle.Bottom, btnH + padding * 2);
                var btnW = btnRect.Width - padding * 4;
                var btnX = btnRect.X + padding * 2;
                var btnY = btnRect.Y + padding;
                var fs = BaseFontSize * dpiScale;

                if (State.IsSessionRunning)
                {
                    FillRect(btnX, btnY, btnW, btnH, DisabledBtnBg);
                    DrawText("Session running\u2026", fontPath,
                        btnX, btnY, btnW, btnH,
                        fs, DisabledBtnText, TextAlign.Center, TextAlign.Center);
                }
                else if (isTonight)
                {
                    RenderButton("\u25B6 Start Session", btnX, btnY, btnW, btnH,
                        fontPath, fs, StartBtnBg, StartBtnText, "StartSession",
                        _ => PostSignal(new StartSessionSignal()));
                }
                else
                {
                    FillRect(btnX, btnY, btnW, btnH, DisabledBtnBg);
                    DrawText("Start (tonight only)", fontPath,
                        btnX, btnY, btnW, btnH, fs, DisabledBtnText, TextAlign.Center, TextAlign.Center);
                }
            }

            var obsRect = rightLayout.Fill();

            RenderCameraSettings(cameraRect, dpiScale, fontPath);
            RenderObservationList(plannerState, obsRect, dpiScale, fontPath);
        }

        // -----------------------------------------------------------------------
        // Per-OTA camera settings
        // -----------------------------------------------------------------------

        private void RenderCameraSettings(
            RectF32 rect,
            float dpiScale,
            string fontPath)
        {
            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var itemH = BaseItemHeight * dpiScale;
            var headerH = BaseHeaderHeight * dpiScale;
            var stepperBtnW = BaseStepperBtnW * dpiScale;
            var valueW = BaseValueW * dpiScale * 0.75f;

            var camLayout = new PixelLayout(rect);
            var headerRect = camLayout.Dock(PixelDockStyle.Top, headerH);

            FillRect(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, HeaderBg);
            DrawText("Camera Settings", fontPath,
                headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            var listRect = camLayout.Fill();

            if (State.CameraSettings.Count == 0)
            {
                DrawText("No cameras configured.", fontPath,
                    listRect.X + padding, listRect.Y, listRect.Width - padding * 2, itemH,
                    fontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var cursor = listRect.Y;

            for (var i = 0; i < State.CameraSettings.Count && cursor + itemH * 2 <= listRect.Y + listRect.Height; i++)
            {
                var cam = State.CameraSettings[i];
                var capturedI = i;

                // OTA header: name + f-ratio
                FillRect(listRect.X, cursor, listRect.Width, itemH, OtaHeaderBg);
                var fRatioStr = !double.IsNaN(cam.FRatio) ? $"  f/{cam.FRatio:0.#}" : "";
                DrawText($"{cam.OtaName}{fRatioStr}", fontPath,
                    listRect.X + padding, cursor, listRect.Width - padding * 2, itemH,
                    fontSize, BodyText, TextAlign.Near, TextAlign.Center);
                cursor += itemH;

                var labelW = 80f * dpiScale;
                var controlX = listRect.X + padding + labelW;

                // Setpoint row (hidden for uncooled cameras like DSLRs)
                if (cam.HasCooling)
                {
                    FillRect(listRect.X, cursor, listRect.Width, itemH, PanelBg);
                    DrawText("Setpoint", fontPath,
                        listRect.X + padding, cursor, labelW, itemH,
                        fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                    RenderButton("\u2212", controlX, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Dec:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Max(State.CameraSettings[capturedI].SetpointTempC - 1, -40); State.IsDirty = true; State.NeedsRedraw = true; });
                    DrawText($"{cam.SetpointTempC}°C", fontPath,
                        controlX + stepperBtnW, cursor, valueW, itemH,
                        fontSize, BodyText, TextAlign.Center, TextAlign.Center);
                    RenderButton("+", controlX + stepperBtnW + valueW, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Inc:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Min(State.CameraSettings[capturedI].SetpointTempC + 1, 30); State.IsDirty = true; State.NeedsRedraw = true; });
                    cursor += itemH;
                }

                // Gain row
                var gainValueW = cam.UsesGainMode ? valueW * 1.6f : valueW;
                FillRect(listRect.X, cursor, listRect.Width, itemH, RowAltBg);
                DrawText("Gain", fontPath,
                    listRect.X + padding, cursor, labelW, itemH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                if (cam.UsesGainMode && cam.GainModes.Count > 0)
                {
                    // Mode camera: ◀ label ▶
                    var modeName = cam.Gain >= 0 && cam.Gain < cam.GainModes.Count
                        ? cam.GainModes[cam.Gain]
                        : $"Mode {cam.Gain}";
                    RenderButton("\u25C0", controlX, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Dec:Gain:{i}",
                        _ =>
                        {
                            var c = State.CameraSettings[capturedI];
                            c.Gain = (c.Gain - 1 + c.GainModes.Count) % c.GainModes.Count;
                            State.IsDirty = true;
                            State.NeedsRedraw = true;
                        });
                    DrawText(modeName, fontPath,
                        controlX + stepperBtnW, cursor, gainValueW, itemH,
                        fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);
                    RenderButton("\u25B6", controlX + stepperBtnW + gainValueW, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Inc:Gain:{i}",
                        _ =>
                        {
                            var c = State.CameraSettings[capturedI];
                            c.Gain = (c.Gain + 1) % c.GainModes.Count;
                            State.IsDirty = true;
                            State.NeedsRedraw = true;
                        });
                }
                else
                {
                    // Numeric gain
                    RenderButton("\u2212", controlX, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Dec:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Max(State.CameraSettings[capturedI].Gain - 10, 0); State.IsDirty = true; State.NeedsRedraw = true; });
                    DrawText($"{cam.Gain}", fontPath,
                        controlX + stepperBtnW, cursor, valueW, itemH,
                        fontSize, BodyText, TextAlign.Center, TextAlign.Center);
                    RenderButton("+", controlX + stepperBtnW + valueW, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Inc:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Min(State.CameraSettings[capturedI].Gain + 10, 600); State.IsDirty = true; State.NeedsRedraw = true; });
                }
                cursor += itemH;

                // Gap between OTAs
                cursor += padding * 0.5f;
            }
        }

        // -----------------------------------------------------------------------
        // Observation list with exposure steppers and frame estimates
        // -----------------------------------------------------------------------

        private void RenderObservationList(
            PlannerState plannerState,
            RectF32 rect,
            float dpiScale,
            string fontPath)
        {
            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var rowH = BaseObsRowHeight * dpiScale;
            var headerH = BaseHeaderHeight * dpiScale;
            var stepperBtnW = BaseStepperBtnW * dpiScale * 0.85f;

            var obsLayout = new PixelLayout(rect);
            var headerRect = obsLayout.Dock(PixelDockStyle.Top, headerH);

            FillRect(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, HeaderBg);
            DrawText("Observations", fontPath,
                headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            var listRect = obsLayout.Fill();

            if (plannerState.Proposals.Count == 0)
            {
                DrawText("No targets pinned.", fontPath,
                    listRect.X + padding, listRect.Y, listRect.Width - padding * 2, rowH,
                    fontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center);
                DrawText("Use the Planner tab to add targets.", fontPath,
                    listRect.X + padding, listRect.Y + rowH, listRect.Width - padding * 2, rowH,
                    fontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Column layout
            var colNumW = 22f * dpiScale;
            var colExpW = 100f * dpiScale;
            var colFrameW = 50f * dpiScale;
            var colNumX = listRect.X + padding;
            var colTargetX = colNumX + colNumW;
            var colExpX = listRect.X + listRect.Width - colExpW - colFrameW - padding;
            var colFrameX = listRect.X + listRect.Width - colFrameW - padding;
            var colTargetW = colExpX - colTargetX - padding;

            // Column headers
            var cursor = listRect.Y;
            DrawText("#", fontPath, colNumX, cursor, colNumW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Target", fontPath, colTargetX, cursor, colTargetW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Exp", fontPath, colExpX, cursor, colExpW, rowH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
            DrawText("~N", fontPath, colFrameX, cursor, colFrameW, rowH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
            cursor += rowH;

            FillRect(listRect.X, cursor, listRect.Width, BaseSeparatorW * dpiScale, SeparatorColor);
            cursor += BaseSeparatorW * dpiScale;

            var proposals = plannerState.Proposals;
            var sliders = plannerState.HandoffSliders;
            var dark = plannerState.AstroDark;
            var twilight = plannerState.AstroTwilight;

            // Default exposure from first OTA's f-ratio
            var defaultExpSec = State.CameraSettings.Count > 0
                ? SessionTabState.DefaultExposureFromFRatio(State.CameraSettings[0].FRatio)
                : 120;

            for (var i = 0; i < proposals.Count && cursor + rowH * 2 <= listRect.Y + listRect.Height; i++)
            {
                var proposal = proposals[i];
                var capturedI = i;
                var bgColor = i % 2 == 0 ? PanelBg : RowAltBg;

                // Compute remaining window (clipped to now)
                var windowStart = i > 0 && i - 1 < sliders.Count ? sliders[i - 1] : dark;
                var windowEnd = i < sliders.Count ? sliders[i] : twilight;
                var effectiveStart = windowStart;
                var utcNow = (_timeProvider ?? TimeProvider.System).GetUtcNow();
                if (utcNow > windowStart && utcNow < windowEnd)
                {
                    effectiveStart = utcNow;
                }
                var window = effectiveStart != default && windowEnd != default
                    ? windowEnd - effectiveStart
                    : TimeSpan.Zero;

                // Current sub-exposure (per-target or default from f-ratio)
                var subExp = proposal.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);

                // Row 1: # / target / exposure stepper / frame count
                FillRect(listRect.X, cursor, listRect.Width, rowH, bgColor);

                DrawText($"{i + 1}", fontPath, colNumX, cursor, colNumW, rowH,
                    fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(proposal.Target.Name, fontPath, colTargetX, cursor, colTargetW, rowH,
                    fontSize, BodyText, TextAlign.Near, TextAlign.Center);

                // Exposure stepper
                var expStr = SessionContent.FormatExposure(subExp);
                var expBtnW = stepperBtnW;
                var expValW = colExpW - expBtnW * 2;

                RenderButton("\u2212", colExpX, cursor, expBtnW, rowH, fontPath, fontSize * 0.85f,
                    StepperBg, BodyText, $"Dec:Exp:{i}",
                    _ =>
                    {
                        var p = plannerState.Proposals[capturedI];
                        var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                        plannerState.Proposals[capturedI] = p with { SubExposure = SessionTabState.StepExposure(cur, false) };
                        State.NeedsRedraw = true;
                    });

                // Exposure value: text input when editing, plain text otherwise
                var expValX = colExpX + expBtnW;
                if (State.EditingExposureIndex == capturedI)
                {
                    RenderTextInput(State.ExposureInput, (int)expValX, (int)cursor, (int)expValW, (int)rowH,
                        fontPath, fontSize * 0.9f);
                }
                else
                {
                    DrawText(expStr, fontPath, expValX, cursor, expValW, rowH,
                        fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);
                    _exposureValueRegions.Add((new RectF32(expValX, cursor, expValW, rowH), capturedI));
                }

                RenderButton("+", colExpX + expBtnW + expValW, cursor, expBtnW, rowH, fontPath, fontSize * 0.85f,
                    StepperBg, BodyText, $"Inc:Exp:{i}",
                    _ =>
                    {
                        var p = plannerState.Proposals[capturedI];
                        var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                        plannerState.Proposals[capturedI] = p with { SubExposure = SessionTabState.StepExposure(cur, true) };
                        State.NeedsRedraw = true;
                    });

                // Frame estimate
                var frameCount = window > TimeSpan.Zero
                    ? SessionTabState.EstimateFrameCount(window, subExp)
                    : 0;
                var frameStr = frameCount > 0 ? $"~{frameCount}" : "—";
                DrawText(frameStr, fontPath, colFrameX, cursor, colFrameW, rowH,
                    fontSize * 0.9f, FrameCountText, TextAlign.Center, TextAlign.Center);

                cursor += rowH;

                // Row 2: time window + duration
                FillRect(listRect.X, cursor, listRect.Width, rowH * 0.8f, bgColor);

                if (window > TimeSpan.Zero)
                {
                    var tz = plannerState.SiteTimeZone;
                    var startStr = windowStart.ToOffset(tz).ToString("HH:mm");
                    var endStr = windowEnd.ToOffset(tz).ToString("HH:mm");
                    var durColor = window.TotalHours < 1.5 ? WarningColor : AccentColor;
                    var durStr = window.TotalHours >= 1
                        ? $"{window.TotalHours:0.0}h"
                        : $"{window.TotalMinutes:0}min";

                    DrawText($"  {startStr}\u2013{endStr}  ({durStr})", fontPath,
                        colTargetX, cursor, listRect.Width - colNumW - padding * 2, rowH * 0.8f,
                        fontSize * 0.85f, durColor, TextAlign.Near, TextAlign.Center);
                }

                cursor += rowH * 0.8f;

                // Thin separator
                FillRect(listRect.X + padding, cursor, listRect.Width - padding * 2, BaseSeparatorW * dpiScale, SeparatorColor);
                cursor += BaseSeparatorW * dpiScale;
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Configuration form panel (left side, scrollable)
        // -----------------------------------------------------------------------

        private void RenderConfigForm(
            RectF32 rect,
            float dpiScale,
            string fontPath)
        {
            var fontSize = BaseFontSize * dpiScale;
            var padding = BasePadding * dpiScale;
            var itemH = BaseItemHeight * dpiScale;
            var headerH = BaseHeaderHeight * dpiScale;
            var labelW = BaseLabelW * dpiScale;
            var stepperBtnW = BaseStepperBtnW * dpiScale;
            var valueW = BaseValueW * dpiScale;

            FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);

            _fieldYPositions.Clear();
            var cursor = rect.Y - State.ConfigScrollOffset;
            var groups = SessionConfigGroups.Groups;
            var globalFieldIdx = 0;

            for (var gi = 0; gi < groups.Length; gi++)
            {
                var group = groups[gi];

                // Section header
                if (cursor + headerH > rect.Y - headerH && cursor < rect.Y + rect.Height)
                {
                    var visibleY = Math.Max(cursor, rect.Y);
                    FillRect(rect.X, visibleY, rect.Width, headerH, HeaderBg);
                    DrawText(group.Name, fontPath,
                        rect.X + padding, visibleY, rect.Width - padding * 2, headerH,
                        fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                }
                cursor += headerH;

                for (var fi = 0; fi < group.Fields.Length; fi++)
                {
                    var field = group.Fields[fi];
                    var fieldIdx = globalFieldIdx++;

                    // Track Y position relative to scroll origin (for EnsureFieldVisible)
                    _fieldYPositions.Add(cursor - rect.Y + State.ConfigScrollOffset);

                    // Skip rows completely outside the visible area
                    if (cursor + itemH <= rect.Y || cursor >= rect.Y + rect.Height)
                    {
                        cursor += itemH;
                        continue;
                    }

                    // Row background — highlight selected
                    var isSelected = fieldIdx == State.SelectedFieldIndex;
                    var rowBg = isSelected ? SelectedRowBg
                        : fi % 2 == 0 ? ContentBg : RowAltBg;
                    FillRect(rect.X, cursor, rect.Width, itemH, rowBg);

                    // Clickable row — selects the field
                    var capturedIdx = fieldIdx;
                    RegisterClickable(rect.X, cursor, labelW + padding, itemH,
                        new HitResult.ListItemHit("ConfigField", fieldIdx),
                        _ => { State.SelectedFieldIndex = capturedIdx; State.NeedsRedraw = true; });

                    // Label
                    DrawText(field.Label, fontPath,
                        rect.X + padding, cursor, labelW, itemH,
                        fontSize, BodyText, TextAlign.Near, TextAlign.Center);

                    // Controls
                    var controlX = rect.X + padding + labelW;

                    switch (field.Kind)
                    {
                        case ConfigFieldKind.BoolToggle:
                            RenderToggleRow(field, controlX, cursor, itemH, dpiScale, fontPath, fontSize);
                            break;

                        case ConfigFieldKind.EnumCycle:
                            RenderCycleRow(field, controlX, cursor, itemH, dpiScale, fontPath, fontSize);
                            break;

                        default:
                            RenderStepperRow(field, controlX, cursor, stepperBtnW, valueW, itemH, fontPath, fontSize);
                            break;
                    }

                    cursor += itemH;
                }

                // Small gap between groups
                cursor += padding * 0.5f;
            }

            State.FieldCount = globalFieldIdx;

            _totalConfigHeight = cursor - (rect.Y - State.ConfigScrollOffset);

            // Clamp scroll offset
            var maxScroll = Math.Max(0, (int)(_totalConfigHeight - rect.Height));
            if (State.ConfigScrollOffset > maxScroll)
            {
                State.ConfigScrollOffset = maxScroll;
            }
        }

        // -----------------------------------------------------------------------
        // Stepper row: [-] value [+]
        // -----------------------------------------------------------------------

        private void RenderStepperRow(
            ConfigFieldDescriptor field,
            float x, float y,
            float btnW, float valW, float h,
            string fontPath, float fontSize)
        {
            var valueStr = field.FormatValue(State.Configuration);
            var unitStr = field.Unit.Length > 0 ? $" {field.Unit}" : "";
            var displayStr = $"{valueStr}{unitStr}";

            ConfigButton("\u2212", x, y, btnW, h, fontPath, fontSize,
                StepperBg, $"Dec:{field.Label}",
                _ => { State.Configuration = field.Decrement(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });

            DrawText(displayStr, fontPath,
                x + btnW, y, valW, h,
                fontSize, State.IsSessionRunning ? DimText : BodyText, TextAlign.Center, TextAlign.Center);

            ConfigButton("+", x + btnW + valW, y, btnW, h, fontPath, fontSize,
                StepperBg, $"Inc:{field.Label}",
                _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });
        }

        // -----------------------------------------------------------------------
        // Toggle row: [ON] or [OFF]
        // -----------------------------------------------------------------------

        private void RenderToggleRow(
            ConfigFieldDescriptor field,
            float x, float y, float h,
            float dpiScale, string fontPath, float fontSize)
        {
            var valueStr = field.FormatValue(State.Configuration);
            var isOn = valueStr == "ON";
            var btnW = 60f * dpiScale;

            ConfigButton(valueStr, x, y, btnW, h, fontPath, fontSize,
                isOn ? ToggleOnBg : ToggleOffBg, $"Toggle:{field.Label}",
                _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });
        }

        // -----------------------------------------------------------------------
        // Cycle row: [Value ▶]
        // -----------------------------------------------------------------------

        /// <summary>Renders a button that is disabled (dimmed, no click) when the session is running.</summary>
        private void ConfigButton(string label, float x, float y, float w, float h,
            string fontPath, float fontSize, RGBAColor32 bg, string hitId, Action<InputModifier>? onClick)
        {
            if (State.IsSessionRunning)
            {
                RenderButton(label, x, y, w, h, fontPath, fontSize, DisabledBtnBg, DimText, hitId, null);
            }
            else
            {
                RenderButton(label, x, y, w, h, fontPath, fontSize, bg, BodyText, hitId, onClick);
            }
        }

        private void RenderCycleRow(
            ConfigFieldDescriptor field,
            float x, float y, float h,
            float dpiScale, string fontPath, float fontSize)
        {
            var valueStr = field.FormatValue(State.Configuration);
            var btnW = 140f * dpiScale;

            ConfigButton($"{valueStr} \u25B6", x, y, btnW, h, fontPath, fontSize * 0.9f,
                CycleBg, $"Cycle:{field.Label}",
                _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
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
        private static readonly float BaseFontSize     = GuiTheme.Metrics.BaseFontSize;
        private const float BaseItemHeight    = 26f;
        private static readonly float BaseHeaderHeight = GuiTheme.Metrics.HeaderHeight;
        private static readonly float BasePadding      = GuiTheme.Metrics.Padding;
        private const float BaseStepperBtnW   = 28f;
        private const float BaseValueW        = 80f;
        private const float BaseLabelW        = 160f;
        private const float BaseObsRowHeight  = 26f;
        private const float BaseSeparatorW    = 1f;
        private const float BaseObsPanelWidth = 440f;

        // Colors
        private static readonly RGBAColor32 ContentBg      = GuiTheme.Palette.ContentBg;
        private static readonly RGBAColor32 PanelBg        = GuiTheme.Palette.PanelBg;
        private static readonly RGBAColor32 HeaderBg       = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText     = GuiTheme.Palette.HeaderText;
        private static readonly RGBAColor32 BodyText       = GuiTheme.Palette.BodyText;
        private static readonly RGBAColor32 DimText        = GuiTheme.Palette.DimText;
        private static readonly RGBAColor32 SeparatorColor = GuiTheme.Palette.Separator;
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
        private static readonly RGBAColor32 SelectedRowBg  = GuiTheme.Palette.Selection;

        private float _totalConfigHeight;

        /// <summary>Y positions (relative to scroll origin) of each config field, for scroll-to-visible.</summary>
        private readonly List<float> _fieldYPositions = [];

        /// <summary>Per-observation exposure value hit regions for double-click-to-edit.</summary>
        private readonly List<(RectF32 Rect, int ProposalIndex)> _exposureValueRegions = [];

        /// <summary>Reused scratch buffer for measuring the shared stepper value-column width (no per-frame alloc).</summary>
        private readonly List<string> _stepperValueScratch = [];

        /// <summary>Reused scratch buffer for measuring the shared per-OTA camera-settings value-column width.</summary>
        private readonly List<string> _cameraValueScratch = [];

        /// <summary>Cached reference to planner state for HandleInput access.</summary>
        private PlannerState? _plannerState;

        /// <summary>Cached reference to time provider for observation list rendering.</summary>
        private ITimeProvider? _timeProvider;

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
            ITimeProvider? timeProvider = null)
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
                State.InitializeFromProfile(appState.ActiveProfile, appState.DeviceHub);
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
            // Clamp to the actual scrollable range so the wheel is a no-op when the form fits
            // (maxScroll == 0). The upper clamp at the end of RenderConfigForm runs only AFTER the
            // content is painted, so without clamping here each wheel tick would paint one scrolled
            // frame and then snap back. _totalConfigHeight + ConfigPanelRect are from the last render
            // (both 0 before the first frame, where maxScroll is also 0, so the wheel stays inert).
            var maxScroll = Math.Max(0, (int)(_totalConfigHeight - ConfigPanelRect.Height));
            State.ConfigScrollOffset = Math.Clamp(
                State.ConfigScrollOffset - (int)(scrollY * ScrollLineHeight), 0, maxScroll);
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
                        _plannerState.Proposals = _plannerState.Proposals.SetItem(
                            capturedIdx, _plannerState.Proposals[capturedIdx] with { SubExposure = newExp });
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

        // Shared stepper button styling for this tab (design-unit [-]/[+] look), passed to
        // FormRowLayout.StepperControl so the config + camera steppers share one definition.
        // Declared after the colours above so the static field initializer reads them initialized.
        private static readonly FormRowLayout.StepperStyle SessionStepperStyle = new(
            StepperBg, BodyText, DisabledBtnBg, DimText, BaseFontSize, BaseStepperBtnW);

        // Design-unit toggle / cycle button widths (the config form's two non-stepper control kinds).
        private const float BaseToggleBtnW = 60f;
        private const float BaseCycleBtnW  = 140f;

        // Bundles every colour + design-unit metric the config form needs into one value so
        // SessionConfigLayout.Build reads them from a single place (mirrors EquipmentPanelStyle).
        // Declared after the colours + SessionStepperStyle above so the field initializers see them set.
        private static readonly SessionConfigStyle ConfigStyle = new(
            Stepper: SessionStepperStyle,
            HeaderBg: HeaderBg, HeaderText: HeaderText,
            BodyText: BodyText, DimText: DimText,
            RowBg: ContentBg, RowAltBg: RowAltBg, SelectedRowBg: SelectedRowBg,
            ToggleOnBg: ToggleOnBg, ToggleOffBg: ToggleOffBg, CycleBg: CycleBg,
            DisabledBg: DisabledBtnBg,
            FontSize: BaseFontSize, HeaderHeight: BaseHeaderHeight, ItemHeight: BaseItemHeight,
            LabelWidth: BaseLabelW, Padding: BasePadding,
            ToggleButtonWidth: BaseToggleBtnW, CycleButtonWidth: BaseCycleBtnW);

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
            var hasPinned = plannerState.Proposals.Length > 0;
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
            var labelW = 80f * dpiScale;

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

            var controlX = listRect.X + padding + labelW;

            // Measurement-driven value column shared across every OTA's setpoint/gain row so long
            // gain-mode names fit and all steppers stay aligned (see PixelWidgetBase.MeasureValueColumnWidth).
            // Mode cameras contribute all mode names (not just the current one) so the column does not
            // reflow as the user cycles modes.
            _cameraValueScratch.Clear();
            for (var i = 0; i < State.CameraSettings.Count; i++)
            {
                var cam = State.CameraSettings[i];
                if (cam.HasCooling)
                {
                    _cameraValueScratch.Add($"{cam.SetpointTempC}°C");
                }

                if (cam.UsesGainMode && cam.GainModes.Count > 0)
                {
                    for (var m = 0; m < cam.GainModes.Count; m++)
                    {
                        _cameraValueScratch.Add(cam.GainModes[m]);
                    }
                }
                else
                {
                    _cameraValueScratch.Add($"{cam.Gain}");
                }
            }

            var valueW = MeasureValueColumnWidth(
                _cameraValueScratch, fontPath, fontSize,
                minWidth: BaseValueW * dpiScale * 0.75f,
                maxWidth: listRect.Width - padding * 2f - labelW - stepperBtnW * 2f,
                horizontalPadding: padding);

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

                // Setpoint row (hidden for uncooled cameras like DSLRs)
                if (cam.HasCooling)
                {
                    FillRect(listRect.X, cursor, listRect.Width, itemH, PanelBg);
                    DrawText("Setpoint", fontPath,
                        listRect.X + padding, cursor, labelW, itemH,
                        fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                    var setCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u2212", $"Dec:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Max(State.CameraSettings[capturedI].SetpointTempC - 1, -40); State.IsDirty = true; State.NeedsRedraw = true; },
                        "+", $"Inc:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Min(State.CameraSettings[capturedI].SetpointTempC + 1, 30); State.IsDirty = true; State.NeedsRedraw = true; },
                        $"{cam.SetpointTempC}°C", BaseFontSize, BodyText, enabled: true);
                    RenderLayout(setCtrl, new RectF32(controlX, cursor, stepperBtnW + valueW + stepperBtnW, itemH), fontPath, dpiScale);
                    cursor += itemH;
                }

                // Gain row
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
                    var modeCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u25C0", $"Dec:Gain:{i}",
                        _ => { var c = State.CameraSettings[capturedI]; c.Gain = (c.Gain - 1 + c.GainModes.Count) % c.GainModes.Count; State.IsDirty = true; State.NeedsRedraw = true; },
                        "\u25B6", $"Inc:Gain:{i}",
                        _ => { var c = State.CameraSettings[capturedI]; c.Gain = (c.Gain + 1) % c.GainModes.Count; State.IsDirty = true; State.NeedsRedraw = true; },
                        modeName, BaseFontSize * 0.9f, BodyText, enabled: true);
                    RenderLayout(modeCtrl, new RectF32(controlX, cursor, stepperBtnW + valueW + stepperBtnW, itemH), fontPath, dpiScale);
                }
                else
                {
                    // Numeric gain
                    var gainCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u2212", $"Dec:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Max(State.CameraSettings[capturedI].Gain - 10, 0); State.IsDirty = true; State.NeedsRedraw = true; },
                        "+", $"Inc:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Min(State.CameraSettings[capturedI].Gain + 10, 600); State.IsDirty = true; State.NeedsRedraw = true; },
                        $"{cam.Gain}", BaseFontSize, BodyText, enabled: true);
                    RenderLayout(gainCtrl, new RectF32(controlX, cursor, stepperBtnW + valueW + stepperBtnW, itemH), fontPath, dpiScale);
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

            if (plannerState.Proposals.Length == 0)
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

            for (var i = 0; i < proposals.Length && cursor + rowH * 2 <= listRect.Y + listRect.Height; i++)
            {
                var proposal = proposals[i];
                var capturedI = i;
                var bgColor = i % 2 == 0 ? PanelBg : RowAltBg;

                // Compute remaining window (clipped to now)
                var windowStart = i > 0 && i - 1 < sliders.Length ? sliders[i - 1] : dark;
                var windowEnd = i < sliders.Length ? sliders[i] : twilight;
                var effectiveStart = windowStart;
                var utcNow = (_timeProvider ?? SystemTimeProvider.Instance).GetUtcNow();
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

                // Exposure stepper: [-] value [+]. The value cell is a Fill leaf so the engine positions it;
                // the drawFill closure renders a text input while editing, otherwise the value text plus the
                // double-click-to-edit region taken from the arranged rect (imperative pixel font sizes, as before).
                var expStr = SessionContent.FormatExposure(subExp);
                var expBtnW = stepperBtnW;
                var expValW = colExpW - expBtnW * 2;

                LayoutNode ExpButton(string glyph, string hit, Action<InputModifier> onClick) =>
                    new LayoutNode.Leaf(new LayoutContent.Text(glyph, BaseFontSize * 0.85f) { Color = BodyText, HAlign = TextAlign.Center, VAlign = TextAlign.Center })
                    {
                        Width = Sizing.Fixed(BaseStepperBtnW * 0.85f),
                        Height = Sizing.Star(),
                        Background = StepperBg,
                        Hit = new HitResult.ButtonHit(hit),
                        OnClick = onClick,
                    };

                var expRow = new LayoutNode.Stack(
                [
                    ExpButton("\u2212", $"Dec:Exp:{i}",
                        _ =>
                        {
                            var p = plannerState.Proposals[capturedI];
                            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                            plannerState.Proposals = plannerState.Proposals.SetItem(
                                capturedI, p with { SubExposure = SessionTabState.StepExposure(cur, false) });
                            State.NeedsRedraw = true;
                        }),
                    new LayoutNode.Leaf(new LayoutContent.Fill(Key: "exp")) { Width = Sizing.Star(), Height = Sizing.Star() },
                    ExpButton("+", $"Inc:Exp:{i}",
                        _ =>
                        {
                            var p = plannerState.Proposals[capturedI];
                            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                            plannerState.Proposals = plannerState.Proposals.SetItem(
                                capturedI, p with { SubExposure = SessionTabState.StepExposure(cur, true) });
                            State.NeedsRedraw = true;
                        }),
                ], LayoutAxis.Horizontal);

                RenderLayout(expRow, new RectF32(colExpX, cursor, expBtnW + expValW + expBtnW, rowH), fontPath, dpiScale,
                    drawFill: (_, r) =>
                    {
                        if (State.EditingExposureIndex == capturedI)
                        {
                            RenderTextInput(State.ExposureInput, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, fontPath, fontSize * 0.9f);
                        }
                        else
                        {
                            DrawText(expStr, fontPath, r.X, r.Y, r.Width, r.Height, fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);
                            _exposureValueRegions.Add((r, capturedI));
                        }
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
                        ? $"{(int)window.TotalHours}h {window.Minutes:D2}m"
                        : $"{(int)window.TotalMinutes}m";

                    DrawText($"  {startStr}-{endStr}  ({durStr})", fontPath,
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
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, ContentBg);

            var groups = SessionConfigGroups.Groups;

            // --- Shared stepper value column (design units) ---------------------------------------
            // Size the [-] value [+] cell to the widest current stepper value (e.g. "Auto (3min)") so
            // long strings neither clip nor collide with the buttons, while every stepper aligns in one
            // column. Measured in DESIGN units (BaseFontSize + design min/max) so it slots straight into
            // the design-unit tree below -- the single RenderLayout(dpiScale) scales the whole form at
            // once. MeasureValueColumnWidth returns the minimum when no font is available (headless tests).
            _stepperValueScratch.Clear();
            var totalFields = 0;
            for (var gi = 0; gi < groups.Length; gi++)
            {
                var fields = groups[gi].Fields;
                for (var fi = 0; fi < fields.Length; fi++)
                {
                    totalFields++;
                    if (fields[fi].Kind is ConfigFieldKind.BoolToggle or ConfigFieldKind.EnumCycle)
                    {
                        continue; // toggles / cycles size their own button, not the value cell
                    }

                    _stepperValueScratch.Add(SessionConfigLayout.FormatStepperDisplay(fields[fi], State.Configuration));
                }
            }

            State.FieldCount = totalFields;

            var availDesignW = rect.Width / dpiScale;
            var valueWidth = MeasureValueColumnWidth(
                _stepperValueScratch, fontPath, BaseFontSize,
                minWidth: BaseValueW,
                maxWidth: availDesignW - BasePadding * 3f - BaseLabelW - BaseStepperBtnW * 2f,
                horizontalPadding: BasePadding);

            // --- Field Y positions (scaled px, relative to scroll origin) + total content height -----
            // Derived arithmetically from the fixed row structure (header + N items + group gap, per
            // group) so it exactly matches the tree the engine arranges below; used by EnsureFieldVisible
            // and the scroll clamp. (Cheaper than measuring the arranged tree, and structurally identical.)
            _fieldYPositions.Clear();
            var yDesign = 0f;
            for (var gi = 0; gi < groups.Length; gi++)
            {
                yDesign += BaseHeaderHeight;
                var fields = groups[gi].Fields;
                for (var fi = 0; fi < fields.Length; fi++)
                {
                    _fieldYPositions.Add(yDesign * dpiScale);
                    yDesign += BaseItemHeight;
                }
                yDesign += BasePadding * 0.5f;
            }

            _totalConfigHeight = yDesign * dpiScale;

            // Clamp scroll to the scrollable range before painting.
            var maxScroll = Math.Max(0, (int)(_totalConfigHeight - rect.Height));
            State.ConfigScrollOffset = Math.Clamp(State.ConfigScrollOffset, 0, maxScroll);

            // --- One tree for the whole form; scroll via the root-bounds Y offset, clipped to the panel ---
            var tree = SessionConfigLayout.Build(
                groups, State.Configuration, State.SelectedFieldIndex, State.IsSessionRunning,
                valueWidth, ConfigStyle,
                onSelectField: idx => _ => { State.SelectedFieldIndex = idx; State.NeedsRedraw = true; },
                onDecrement: field => _ => { State.Configuration = field.Decrement(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; },
                onIncrement: field => _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });

            var contentTop = rect.Y - State.ConfigScrollOffset;
            var arranged = ArrangeLayout(tree, new RectF32(rect.X, contentTop, rect.Width, _totalConfigHeight), fontPath, dpiScale);

            // A single tree arranges every row at an absolute rect -- including rows scrolled off the top
            // or bottom. Painting those would draw outside the panel AND register clickable regions beyond
            // the viewport, so drop any node that does not intersect the panel before painting (this is
            // what the per-row index-virtualised path used to get for free by skipping off-screen rows).
            var top = rect.Y;
            var bottom = rect.Y + rect.Height;
            var visible = ImmutableArray.CreateBuilder<ArrangedNode<float>>(arranged.Length);
            foreach (var node in arranged)
            {
                var b = node.Bounds;
                if (b.Y < bottom && b.Y + b.Height > top)
                {
                    visible.Add(node);
                }
            }

            // Scissor partially-visible top/bottom rows to the panel edge (the proper clip the old header
            // "Math.Max(cursor, rect.Y)" hack stood in for; section headers now scroll naturally).
            Renderer.PushClip(new RectInt(
                new PointInt((int)(rect.X + rect.Width), (int)bottom),
                new PointInt((int)rect.X, (int)top)));
            PaintLayout(visible.ToImmutable(), fontPath, dpiScale);
            Renderer.PopClip();
        }
    }
}

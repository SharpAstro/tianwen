using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui
{
    /// <summary>
    /// Renders the Session configuration tab for the TianWen N.I.N.A.-style GUI.
    /// Left: scrollable SessionConfiguration form. Right: per-OTA camera settings + observation list.
    /// </summary>
    public sealed class VkSessionTab : VkTabBase
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

        private float _totalConfigHeight;

        /// <summary>Tab state (configuration values, per-OTA camera settings, scroll offset).</summary>
        public SessionTabState State { get; } = new SessionTabState();

        /// <summary>Bounding rect of the scrollable config panel (set during render).</summary>
        public RectF32 ConfigPanelRect { get; private set; }

        /// <summary>Scroll line height in pixels (for mouse wheel step size).</summary>
        public float ScrollLineHeight { get; private set; }

        public VkSessionTab(VkRenderer renderer) : base(renderer)
        {
        }

        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        public void Render(
            GuiAppState appState,
            PlannerState plannerState,
            RectF32 contentRect,
            float dpiScale,
            string fontPath)
        {
            BeginFrame();
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            ScrollLineHeight = BaseItemHeight * dpiScale;

            // Reinitialize per-OTA settings when the profile changes
            if (State.NeedsReinitialization(appState.ActiveProfile))
            {
                State.InitializeFromProfile(appState.ActiveProfile);
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
        // Mouse wheel handling
        // -----------------------------------------------------------------------

        public override bool HandleMouseWheel(float scrollY, float mouseX, float mouseY)
        {
            if (ConfigPanelRect.Contains(mouseX, mouseY))
            {
                State.ConfigScrollOffset = Math.Max(0,
                    State.ConfigScrollOffset - (int)(scrollY * ScrollLineHeight));
                State.NeedsRedraw = true;
                return true;
            }
            return false;
        }

        // -----------------------------------------------------------------------
        // Right panel: camera settings (top) + observation list (bottom)
        // -----------------------------------------------------------------------

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
            DrawText("Camera Settings".AsSpan(), fontPath,
                headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            var listRect = camLayout.Fill();

            if (State.CameraSettings.Count == 0)
            {
                DrawText("No cameras configured.".AsSpan(), fontPath,
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
                DrawText($"{cam.OtaName}{fRatioStr}".AsSpan(), fontPath,
                    listRect.X + padding, cursor, listRect.Width - padding * 2, itemH,
                    fontSize, BodyText, TextAlign.Near, TextAlign.Center);
                cursor += itemH;

                // Setpoint row
                var labelW = 80f * dpiScale;
                var controlX = listRect.X + padding + labelW;

                FillRect(listRect.X, cursor, listRect.Width, itemH, PanelBg);
                DrawText("Setpoint".AsSpan(), fontPath,
                    listRect.X + padding, cursor, labelW, itemH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                RenderButton("\u2212", controlX, cursor, stepperBtnW, itemH, fontPath, fontSize,
                    StepperBg, BodyText, $"Dec:Setpoint:{i}",
                    () => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Max(State.CameraSettings[capturedI].SetpointTempC - 1, -40); State.NeedsRedraw = true; });
                DrawText($"{cam.SetpointTempC}°C".AsSpan(), fontPath,
                    controlX + stepperBtnW, cursor, valueW, itemH,
                    fontSize, BodyText, TextAlign.Center, TextAlign.Center);
                RenderButton("+", controlX + stepperBtnW + valueW, cursor, stepperBtnW, itemH, fontPath, fontSize,
                    StepperBg, BodyText, $"Inc:Setpoint:{i}",
                    () => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Min(State.CameraSettings[capturedI].SetpointTempC + 1, 30); State.NeedsRedraw = true; });
                cursor += itemH;

                // Gain row
                FillRect(listRect.X, cursor, listRect.Width, itemH, RowAltBg);
                DrawText("Gain".AsSpan(), fontPath,
                    listRect.X + padding, cursor, labelW, itemH,
                    fontSize * 0.9f, DimText, TextAlign.Near, TextAlign.Center);

                if (cam.UsesGainMode && cam.GainModes.Count > 0)
                {
                    // Mode camera: cycle through named modes
                    var modeName = cam.Gain >= 0 && cam.Gain < cam.GainModes.Count
                        ? cam.GainModes[cam.Gain]
                        : $"Mode {cam.Gain}";
                    var cycleBtnW = 120f * dpiScale;
                    RenderButton($"{modeName} \u25B6", controlX, cursor, cycleBtnW, itemH, fontPath, fontSize * 0.9f,
                        CycleBg, BodyText, $"Cycle:Gain:{i}",
                        () =>
                        {
                            var c = State.CameraSettings[capturedI];
                            c.Gain = (c.Gain + 1) % c.GainModes.Count;
                            State.NeedsRedraw = true;
                        });
                }
                else
                {
                    // Numeric gain
                    RenderButton("\u2212", controlX, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Dec:Gain:{i}",
                        () => { State.CameraSettings[capturedI].Gain = Math.Max(State.CameraSettings[capturedI].Gain - 10, 0); State.NeedsRedraw = true; });
                    DrawText($"{cam.Gain}".AsSpan(), fontPath,
                        controlX + stepperBtnW, cursor, valueW, itemH,
                        fontSize, BodyText, TextAlign.Center, TextAlign.Center);
                    RenderButton("+", controlX + stepperBtnW + valueW, cursor, stepperBtnW, itemH, fontPath, fontSize,
                        StepperBg, BodyText, $"Inc:Gain:{i}",
                        () => { State.CameraSettings[capturedI].Gain = Math.Min(State.CameraSettings[capturedI].Gain + 10, 600); State.NeedsRedraw = true; });
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
            DrawText("Observations".AsSpan(), fontPath,
                headerRect.X + padding, headerRect.Y, headerRect.Width - padding * 2, headerRect.Height,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            var listRect = obsLayout.Fill();

            if (plannerState.Proposals.Count == 0)
            {
                DrawText("No targets pinned.".AsSpan(), fontPath,
                    listRect.X + padding, listRect.Y, listRect.Width - padding * 2, rowH,
                    fontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center);
                DrawText("Use the Planner tab to add targets.".AsSpan(), fontPath,
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
            DrawText("#".AsSpan(), fontPath, colNumX, cursor, colNumW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Target".AsSpan(), fontPath, colTargetX, cursor, colTargetW, rowH, fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Exp".AsSpan(), fontPath, colExpX, cursor, colExpW, rowH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
            DrawText("~N".AsSpan(), fontPath, colFrameX, cursor, colFrameW, rowH, fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
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

                // Compute window
                var windowStart = i > 0 && i - 1 < sliders.Count ? sliders[i - 1] : dark;
                var windowEnd = i < sliders.Count ? sliders[i] : twilight;
                var window = windowStart != default && windowEnd != default
                    ? windowEnd - windowStart
                    : TimeSpan.Zero;

                // Current sub-exposure (per-target or default from f-ratio)
                var subExp = proposal.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);

                // Row 1: # / target / exposure stepper / frame count
                FillRect(listRect.X, cursor, listRect.Width, rowH, bgColor);

                DrawText($"{i + 1}".AsSpan(), fontPath, colNumX, cursor, colNumW, rowH,
                    fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(proposal.Target.Name.AsSpan(), fontPath, colTargetX, cursor, colTargetW, rowH,
                    fontSize, BodyText, TextAlign.Near, TextAlign.Center);

                // Exposure stepper
                var expStr = FormatExposure(subExp);
                var expBtnW = stepperBtnW;
                var expValW = colExpW - expBtnW * 2;

                RenderButton("\u2212", colExpX, cursor, expBtnW, rowH, fontPath, fontSize * 0.85f,
                    StepperBg, BodyText, $"Dec:Exp:{i}",
                    () =>
                    {
                        var p = plannerState.Proposals[capturedI];
                        var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                        plannerState.Proposals[capturedI] = p with { SubExposure = SessionTabState.StepExposure(cur, false) };
                        State.NeedsRedraw = true;
                    });
                DrawText(expStr.AsSpan(), fontPath, colExpX + expBtnW, cursor, expValW, rowH,
                    fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);
                RenderButton("+", colExpX + expBtnW + expValW, cursor, expBtnW, rowH, fontPath, fontSize * 0.85f,
                    StepperBg, BodyText, $"Inc:Exp:{i}",
                    () =>
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
                DrawText(frameStr.AsSpan(), fontPath, colFrameX, cursor, colFrameW, rowH,
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

                    DrawText($"  {startStr}\u2013{endStr}  ({durStr})".AsSpan(), fontPath,
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

        private static string FormatExposure(TimeSpan ts)
        {
            if (ts.TotalMinutes >= 1 && ts.TotalSeconds % 60 == 0)
            {
                return $"{(int)ts.TotalMinutes}min";
            }
            return $"{(int)ts.TotalSeconds}s";
        }

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

            var cursor = rect.Y - State.ConfigScrollOffset;
            var groups = SessionConfigGroups.Groups;

            for (var gi = 0; gi < groups.Length; gi++)
            {
                var group = groups[gi];

                // Section header
                if (cursor + headerH > rect.Y - headerH && cursor < rect.Y + rect.Height)
                {
                    var visibleY = Math.Max(cursor, rect.Y);
                    FillRect(rect.X, visibleY, rect.Width, headerH, HeaderBg);
                    DrawText(group.Name.AsSpan(), fontPath,
                        rect.X + padding, visibleY, rect.Width - padding * 2, headerH,
                        fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                }
                cursor += headerH;

                for (var fi = 0; fi < group.Fields.Length; fi++)
                {
                    var field = group.Fields[fi];

                    // Skip rows completely outside the visible area
                    if (cursor + itemH <= rect.Y || cursor >= rect.Y + rect.Height)
                    {
                        cursor += itemH;
                        continue;
                    }

                    // Row background
                    var rowBg = fi % 2 == 0 ? ContentBg : RowAltBg;
                    FillRect(rect.X, cursor, rect.Width, itemH, rowBg);

                    // Label
                    DrawText(field.Label.AsSpan(), fontPath,
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

            RenderButton("\u2212", x, y, btnW, h, fontPath, fontSize,
                StepperBg, BodyText, $"Dec:{field.Label}",
                () => { State.Configuration = field.Decrement(State.Configuration); State.NeedsRedraw = true; });

            DrawText(displayStr.AsSpan(), fontPath,
                x + btnW, y, valW, h,
                fontSize, BodyText, TextAlign.Center, TextAlign.Center);

            RenderButton("+", x + btnW + valW, y, btnW, h, fontPath, fontSize,
                StepperBg, BodyText, $"Inc:{field.Label}",
                () => { State.Configuration = field.Increment(State.Configuration); State.NeedsRedraw = true; });
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

            RenderButton(valueStr, x, y, btnW, h, fontPath, fontSize,
                isOn ? ToggleOnBg : ToggleOffBg, BodyText, $"Toggle:{field.Label}",
                () => { State.Configuration = field.Increment(State.Configuration); State.NeedsRedraw = true; });
        }

        // -----------------------------------------------------------------------
        // Cycle row: [Value ▶]
        // -----------------------------------------------------------------------

        private void RenderCycleRow(
            ConfigFieldDescriptor field,
            float x, float y, float h,
            float dpiScale, string fontPath, float fontSize)
        {
            var valueStr = field.FormatValue(State.Configuration);
            var btnW = 140f * dpiScale;

            RenderButton($"{valueStr} \u25B6", x, y, btnW, h, fontPath, fontSize * 0.9f,
                CycleBg, BodyText, $"Cycle:{field.Label}",
                () => { State.Configuration = field.Increment(State.Configuration); State.NeedsRedraw = true; });
        }
    }
}

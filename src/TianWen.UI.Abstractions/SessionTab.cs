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

        /// <summary>Per-frame Fill-leaf painter dispatch for the observation list's single RenderLayout
        /// (the per-row exposure value cell). Render-thread-only, so a plain Dictionary is safe.</summary>
        private readonly Dictionary<string, Action<RectF32>> _obsFills = new();

        /// <summary>Reused scratch buffer for measuring the shared stepper value-column width (no per-frame alloc).</summary>
        private readonly List<string> _stepperValueScratch = [];

        /// <summary>Cached reference to planner state for HandleInput access.</summary>
        private PlannerState? _plannerState;

        /// <summary>Cached reference to time provider for observation list rendering.</summary>
        private ITimeProvider? _timeProvider;

        /// <summary>Tab state (configuration values, per-OTA camera settings, scroll offset).</summary>
        public SessionTabState State { get; } = new SessionTabState();

        /// <summary>Scroll line height in pixels \u2014 the config panel's atom extent (one BaseItemHeight line).</summary>
        public float ScrollLineHeight { get; private set; }

        /// <summary>
        /// Config-panel scroll (DIR.Lib atom model): one atom = one <see cref="ScrollLineHeight"/> line,
        /// <see cref="ScrollBarMode.None"/> keeps the historical clip-only look (no scrollbar). It owns
        /// the continuous fractional offset (the trackpad wheel carry) and the clamp;
        /// <see cref="SessionTabState.ConfigScrollOffset"/> is the canonical-atom mirror shared with the
        /// TUI (whose Console.Lib ScrollableList rows are the same unit).
        /// </summary>
        private readonly ListScrollController _configScroll =
            new ListScrollController { Mode = ScrollBarMode.None };


        // -----------------------------------------------------------------------
        // Public entry point
        // -----------------------------------------------------------------------

        public void Render(
            GuiAppState appState,
            PlannerState plannerState,
            RectF32 contentRect,
            ITimeProvider? timeProvider = null)
        {
            BeginFrame();
            // DPI comes from the inherited DpiScale (host-set); local alias keeps the px math unchanged.
            var dpiScale = DpiScale;
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

            RenderConfigForm(configRect);
            RenderRightPanel(plannerState, obsRect);
        }

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt)
        {
            switch (evt)
            {
                case InputEvent.MouseDown(var px, var py, _, _, var clicks) when clicks >= 2:
                    return HandleDoubleClick(px, py);

                case InputEvent.KeyDown(var key, _):
                    return HandleConfigKey(key);

                default:
                    // Wheel + touch/mouse drag over the config panel via the controller (its viewport is
                    // the config rect from the last render, so right-panel input falls through untouched;
                    // before the first frame the extent is empty and every event is ignored). Field rows +
                    // stepper buttons are registered clickables the host dispatches BEFORE HandleInput, so
                    // only unclaimed presses land here \u2014 an empty-space tap selects nothing, hence the
                    // pending tap is drained and dropped.
                    if (_configScroll.HandleInput(evt))
                    {
                        _configScroll.TakeAtomTap();
                        State.NeedsRedraw = true;
                        return true;
                    }
                    return false;
            }
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
            // Fields are not atom-aligned (group headers + gaps shift them off the BaseItemHeight grid),
            // so ensure both atoms the field's pixel span touches \u2014 they are adjacent, so the second call
            // moves at most one more line and the whole field lands inside the viewport. _fieldYPositions
            // is from the last render, same as the controller's geometry.
            if (State.SelectedFieldIndex >= 0 && _fieldYPositions.Count > State.SelectedFieldIndex)
            {
                var fieldY = _fieldYPositions[State.SelectedFieldIndex];
                var ext = ScrollLineHeight;
                _configScroll.EnsureVisible((int)MathF.Floor(fieldY / ext));
                _configScroll.EnsureVisible((int)MathF.Floor((fieldY + ext - 0.001f) / ext));
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
            RectF32 rect)
        {
            var dpiScale = DpiScale;
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
                var btnRect = rightLayout.Dock(PixelDockStyle.Bottom, 36f * dpiScale + padding * 2);

                // Start button as a declarative node (was a RenderButton + two disabled FillRect/DrawText
                // branches): running -> disabled label, tonight -> clickable Start, other date -> disabled hint.
                var btnNode = State.IsSessionRunning
                    ? Layout.Builder.Text("Session running\u2026", BaseFontSize, DisabledBtnText, TextAlign.Center, TextAlign.Center).Bg(DisabledBtnBg)
                    : isTonight
                        ? Layout.Builder.Text("\u25B6 Start Session", BaseFontSize, StartBtnText, TextAlign.Center, TextAlign.Center)
                            .Bg(StartBtnBg)
                            .Clickable(new HitResult.ButtonHit("StartSession"), _ => PostSignal(new StartSessionSignal()))
                        : Layout.Builder.Text("Start (tonight only)", BaseFontSize, DisabledBtnText, TextAlign.Center, TextAlign.Center).Bg(DisabledBtnBg);

                RenderLayout(Layout.Builder.VStack(btnNode.Stretch()).Pad(BasePadding), btnRect);
            }

            var obsRect = rightLayout.Fill();

            RenderCameraSettings(cameraRect);
            RenderObservationList(plannerState, obsRect);
        }

        // -----------------------------------------------------------------------
        // Per-OTA camera settings
        // -----------------------------------------------------------------------

        private void RenderCameraSettings(
            RectF32 rect)
        {
            // ONE arranged tree: "Camera Settings" header + per-OTA (name header, optional setpoint row,
            // gain row). The stepper value cells are Star, so every row is the same width and the value
            // columns align with no measured-width pass. Was a cursor walk with a MeasureValueColumnWidth
            // pre-pass. Label column fixed; the stepper fills the rest.
            const float labelW = 80f;
            var rows = new List<Layout.Node>
            {
                Layout.Builder.Text("Camera Settings", BaseFontSize, HeaderText).RowH(BaseHeaderHeight).Bg(HeaderBg),
            };

            if (State.CameraSettings.Count == 0)
            {
                rows.Add(Layout.Builder.Text("No cameras configured.", BaseFontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center)
                    .RowH(BaseItemHeight));
                RenderLayout(Layout.Builder.VStack([.. rows]), rect);
                return;
            }

            for (var i = 0; i < State.CameraSettings.Count; i++)
            {
                var cam = State.CameraSettings[i];
                var capturedI = i;

                // OTA header: name + f-ratio
                var fRatioStr = !double.IsNaN(cam.FRatio) ? $"  f/{cam.FRatio:0.#}" : "";
                rows.Add(Layout.Builder.Text($"{cam.OtaName}{fRatioStr}", BaseFontSize, BodyText).RowH(BaseItemHeight).Bg(OtaHeaderBg));

                // Setpoint row (hidden for uncooled cameras like DSLRs)
                if (cam.HasCooling)
                {
                    var setCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u2212", $"Dec:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Max(State.CameraSettings[capturedI].SetpointTempC - 1, -40); State.IsDirty = true; State.NeedsRedraw = true; },
                        "+", $"Inc:Setpoint:{i}",
                        _ => { State.CameraSettings[capturedI].SetpointTempC = (sbyte)Math.Min(State.CameraSettings[capturedI].SetpointTempC + 1, 30); State.IsDirty = true; State.NeedsRedraw = true; },
                        $"{cam.SetpointTempC}\u00b0C", BaseFontSize, BodyText, enabled: true);
                    rows.Add(Layout.Builder.HStack(
                            Layout.Builder.Text("Setpoint", BaseFontSize * 0.9f, DimText).WFixed(labelW).HStar(),
                            setCtrl.Stretch())
                        .RowH(BaseItemHeight).Bg(PanelBg));
                }

                // Gain row: mode camera (< label >) or numeric (- value +)
                Layout.Node gainCtrl;
                if (cam.UsesGainMode && cam.GainModes.Count > 0)
                {
                    var modeName = cam.Gain >= 0 && cam.Gain < cam.GainModes.Count
                        ? cam.GainModes[cam.Gain]
                        : $"Mode {cam.Gain}";
                    gainCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u25c0", $"Dec:Gain:{i}",
                        _ => { var c = State.CameraSettings[capturedI]; c.Gain = (c.Gain - 1 + c.GainModes.Count) % c.GainModes.Count; State.IsDirty = true; State.NeedsRedraw = true; },
                        "\u25b6", $"Inc:Gain:{i}",
                        _ => { var c = State.CameraSettings[capturedI]; c.Gain = (c.Gain + 1) % c.GainModes.Count; State.IsDirty = true; State.NeedsRedraw = true; },
                        modeName, BaseFontSize * 0.9f, BodyText, enabled: true);
                }
                else
                {
                    gainCtrl = FormRowLayout.StepperControl(SessionStepperStyle,
                        "\u2212", $"Dec:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Max(State.CameraSettings[capturedI].Gain - 10, 0); State.IsDirty = true; State.NeedsRedraw = true; },
                        "+", $"Inc:Gain:{i}",
                        _ => { State.CameraSettings[capturedI].Gain = Math.Min(State.CameraSettings[capturedI].Gain + 10, 600); State.IsDirty = true; State.NeedsRedraw = true; },
                        $"{cam.Gain}", BaseFontSize, BodyText, enabled: true);
                }
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Text("Gain", BaseFontSize * 0.9f, DimText).WFixed(labelW).HStar(),
                        gainCtrl.Stretch())
                    .RowH(BaseItemHeight).Bg(RowAltBg));

                // Gap between OTAs
                rows.Add(Layout.Builder.Spacer().RowH(BasePadding * 0.5f));
            }

            RenderLayout(Layout.Builder.VStack([.. rows]), rect);
        }

        // -----------------------------------------------------------------------
        // Observation list with exposure steppers and frame estimates
        // -----------------------------------------------------------------------

        private void RenderObservationList(
            PlannerState plannerState,
            RectF32 rect)
        {
            var dpiScale = DpiScale;
            // Font from the inherited FontPath (host-set); captured by the exposure-cell painter lambda below.
            var fontPath = FontPath;
            _obsFills.Clear();

            const float colNumW = 22f;
            const float colExpW = 100f;
            const float colFrameW = 50f;
            var expBtnW = BaseStepperBtnW * 0.85f;

            // "Observations" header + column headers, then per-proposal (row1: # / target / exposure stepper /
            // frames, row2: time window) as ONE arranged tree. The exposure value cell stays a keyed Fill --
            // its painter shows the value text (+ registers the double-click-to-edit region) or the inline
            // text input while editing -- dispatched through _obsFills. Was a y += rowH cursor walk.
            var rows = new List<Layout.Node>
            {
                Layout.Builder.Text("Observations", BaseFontSize, HeaderText).RowH(BaseHeaderHeight).Bg(HeaderBg),
            };

            if (plannerState.Proposals.Length == 0)
            {
                rows.Add(Layout.Builder.Text("No targets pinned.", BaseFontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center).RowH(BaseObsRowHeight));
                rows.Add(Layout.Builder.Text("Use the Planner tab to add targets.", BaseFontSize * 0.9f, HintText, TextAlign.Center, TextAlign.Center).RowH(BaseObsRowHeight));
                RenderLayout(Layout.Builder.VStack([.. rows]), rect);
                return;
            }

            // Column header row
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text("#", BaseFontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center).WFixed(colNumW).HStar(),
                    Layout.Builder.Text("Target", BaseFontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center).WStar(),
                    Layout.Builder.Text("Exp", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WFixed(colExpW).HStar(),
                    Layout.Builder.Text("~N", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center).WFixed(colFrameW).HStar())
                .RowH(BaseObsRowHeight));
            rows.Add(Layout.Builder.Spacer().RowH(BaseSeparatorW).Bg(SeparatorColor));

            var proposals = plannerState.Proposals;
            var sliders = plannerState.HandoffSliders;
            var dark = plannerState.AstroDark;
            var twilight = plannerState.AstroTwilight;
            var defaultExpSec = State.CameraSettings.Count > 0
                ? SessionTabState.DefaultExposureFromFRatio(State.CameraSettings[0].FRatio)
                : 120;

            // Build only the rows that fit the panel (mirrors the old cursor viewport guard); clip the rest.
            var availDesign = rect.Height / dpiScale;
            var usedDesign = BaseHeaderHeight + BaseObsRowHeight + BaseSeparatorW;
            var perProposalDesign = BaseObsRowHeight + BaseObsRowHeight * 0.8f + BaseSeparatorW;

            for (var i = 0; i < proposals.Length; i++)
            {
                if (usedDesign + perProposalDesign > availDesign)
                {
                    break;
                }
                usedDesign += perProposalDesign;

                var proposal = proposals[i];
                var capturedI = i;
                var bgColor = i % 2 == 0 ? PanelBg : RowAltBg;

                // Remaining window (clipped to now)
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

                var subExp = proposal.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                var expStr = SessionContent.FormatExposure(subExp);

                // Exposure value cell: keyed Fill -> text + double-click region, or the inline editor.
                var expKey = $"exp:{i}";
                _obsFills[expKey] = r =>
                {
                    if (State.EditingExposureIndex == capturedI)
                    {
                        RenderTextInput(State.ExposureInput, r, fontPath, BaseFontSize * 0.9f * dpiScale);
                    }
                    else
                    {
                        DrawText(expStr, fontPath, r.X, r.Y, r.Width, r.Height, BaseFontSize * 0.9f * dpiScale, BodyText, TextAlign.Center, TextAlign.Center);
                        _exposureValueRegions.Add((r, capturedI));
                    }
                };

                Layout.Node ExpButton(string glyph, string hit, Action<InputModifier> onClick) =>
                    Layout.Builder.Text(glyph, BaseFontSize * 0.85f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(expBtnW).HStar().Bg(StepperBg)
                        .Clickable(new HitResult.ButtonHit(hit), onClick);

                var expStepper = Layout.Builder.HStack(
                    ExpButton("\u2212", $"Dec:Exp:{i}",
                        _ =>
                        {
                            var p = plannerState.Proposals[capturedI];
                            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                            plannerState.Proposals = plannerState.Proposals.SetItem(capturedI, p with { SubExposure = SessionTabState.StepExposure(cur, false) });
                            State.NeedsRedraw = true;
                        }),
                    Layout.Builder.Fill(key: expKey).Stretch(),
                    ExpButton("+", $"Inc:Exp:{i}",
                        _ =>
                        {
                            var p = plannerState.Proposals[capturedI];
                            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                            plannerState.Proposals = plannerState.Proposals.SetItem(capturedI, p with { SubExposure = SessionTabState.StepExposure(cur, true) });
                            State.NeedsRedraw = true;
                        }));

                var frameCount = window > TimeSpan.Zero ? SessionTabState.EstimateFrameCount(window, subExp) : 0;
                var frameStr = frameCount > 0 ? $"~{frameCount}" : "\u2014";

                // Row 1: # | target | exposure stepper | frames
                rows.Add(Layout.Builder.HStack(
                        Layout.Builder.Text($"{i + 1}", BaseFontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center).WFixed(colNumW).HStar(),
                        Layout.Builder.Text(proposal.Target.Name, BaseFontSize, BodyText, TextAlign.Near, TextAlign.Center).WStar(),
                        expStepper.WFixed(colExpW).HStar(),
                        Layout.Builder.Text(frameStr, BaseFontSize * 0.9f, FrameCountText, TextAlign.Center, TextAlign.Center).WFixed(colFrameW).HStar())
                    .RowH(BaseObsRowHeight).Bg(bgColor));

                // Row 2: time window + duration (indented past the # column), or an empty bg row
                if (window > TimeSpan.Zero)
                {
                    var tz = plannerState.SiteTimeZone;
                    var startStr = windowStart.ToOffset(tz).ToString("HH:mm");
                    var endStr = windowEnd.ToOffset(tz).ToString("HH:mm");
                    var durColor = window.TotalHours < 1.5 ? WarningColor : AccentColor;
                    var durStr = window.TotalHours >= 1
                        ? $"{(int)window.TotalHours}h {window.Minutes:D2}m"
                        : $"{(int)window.TotalMinutes}m";
                    rows.Add(Layout.Builder.HStack(
                            Layout.Builder.Spacer().WFixed(colNumW).HStar(),
                            Layout.Builder.Text($"{startStr}-{endStr}  ({durStr})", BaseFontSize * 0.85f, durColor, TextAlign.Near, TextAlign.Center).WStar())
                        .RowH(BaseObsRowHeight * 0.8f).Bg(bgColor));
                }
                else
                {
                    rows.Add(Layout.Builder.Spacer().RowH(BaseObsRowHeight * 0.8f).Bg(bgColor));
                }

                // Thin separator
                rows.Add(Layout.Builder.Spacer().RowH(BaseSeparatorW).Bg(SeparatorColor));
            }

            Renderer.PushClip(new RectInt(
                new PointInt((int)(rect.X + rect.Width), (int)(rect.Y + rect.Height)),
                new PointInt((int)rect.X, (int)rect.Y)));
            RenderLayout(Layout.Builder.VStack([.. rows]), rect,
                drawFill: (fill, r) => { if (fill.Key is { } k && _obsFills.TryGetValue(k, out var painter)) painter(r); });
            Renderer.PopClip();
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Configuration form panel (left side, scrollable)
        // -----------------------------------------------------------------------

        private void RenderConfigForm(
            RectF32 rect)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
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

            // Hand the controller this frame's geometry: one atom = one BaseItemHeight line, total in
            // lines rounded UP so the last partial line stays reachable (the whole-atom bound can
            // over-scroll by under two line heights past the exact pixel bottom \u2014 accepted slack of the
            // atom model). SetExtent re-clamps the offset; the state field then mirrors the snapped
            // canonical atom offset for the TUI's benefit.
            _configScroll.SetExtent(rect, ScrollLineHeight,
                (int)MathF.Ceiling(_totalConfigHeight / ScrollLineHeight), dpiScale);
            State.ConfigScrollOffset = _configScroll.AtomOffset;

            // --- One tree for the whole form; scroll via the root-bounds Y offset, clipped to the panel ---
            var tree = SessionConfigLayout.Build(
                groups, State.Configuration, State.SelectedFieldIndex, State.IsSessionRunning,
                valueWidth, ConfigStyle,
                onSelectField: idx => _ => { State.SelectedFieldIndex = idx; State.NeedsRedraw = true; },
                onDecrement: field => _ => { State.Configuration = field.Decrement(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; },
                onIncrement: field => _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; });

            var contentTop = rect.Y - _configScroll.Offset * ScrollLineHeight;
            var arranged = ArrangeLayout(tree, new RectF32(rect.X, contentTop, rect.Width, _totalConfigHeight));

            // A single tree arranges every row at an absolute rect -- including rows scrolled off the top
            // or bottom. Painting those would draw outside the panel AND register clickable regions beyond
            // the viewport, so drop any node that does not intersect the panel before painting (this is
            // what the per-row index-virtualised path used to get for free by skipping off-screen rows).
            var top = rect.Y;
            var bottom = rect.Y + rect.Height;
            var visible = ImmutableArray.CreateBuilder<Layout.ArrangedNode<float>>(arranged.Length);
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
            PaintLayout(visible.ToImmutable());
            Renderer.PopClip();
        }
    }
}

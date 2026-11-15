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

        /// <summary>The least the right panel is given beside the form; with less room than this for it, the tab stacks.</summary>
        private const float BaseObsPanelLeastWidth = 320f;

        /// <summary>
        /// The least of the form's own scroll lines it keeps when the two halves are stacked: the right panel is
        /// served its whole height first (it is what the tab is opened for, and the form scrolls), down to this.
        /// </summary>
        private const float StackedFormLeastLines = 3f;

        /// <summary>The Start Session button's height, inside the panel's padding.</summary>
        private const float BaseStartButtonHeight = 36f;

        // Colors. The palette-derived ones are PROPERTIES, not static readonly fields: a field
        // initialiser snapshots the palette at type-init, so this tab went on painting the startup
        // scheme through every theme switch. RGBAColor32 is a struct, so reading through allocates
        // nothing. What literals remain are CATEGORICAL series (index N, no severity to express)
        // or the two-trace exemption, each noted at its declaration.
        private static RGBAColor32 ContentBg      => GuiTheme.Palette.ContentBg;
        private static RGBAColor32 PanelBg        => GuiTheme.Palette.PanelBg;
        private static RGBAColor32 HeaderBg       => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 HeaderText     => GuiTheme.Palette.HeaderText;
        private static RGBAColor32 BodyText       => GuiTheme.Palette.BodyText;
        private static RGBAColor32 DimText        => GuiTheme.Palette.DimText;
        private static RGBAColor32 SeparatorColor => GuiTheme.Palette.Separator;
        private static RGBAColor32 StepperBg      => GuiTheme.Palette.Separator;
        // A toggle's two states are "this is on" and "this is off", not two arbitrary tints, so they
        // sit on Success and Error muted back into the panel: saturated enough to read at a glance,
        // dark enough to carry the label.
        private static RGBAColor32 ToggleOnBg     => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Success, 0.45f);
        private static RGBAColor32 ToggleOffBg    => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Error, 0.35f);
        private static RGBAColor32 CycleBg        => GuiTheme.Mix(GuiTheme.Palette.PanelBg, GuiTheme.Palette.Accent, 0.55f);
        private static RGBAColor32 RowAltBg       => GuiTheme.AltRowBg;
        private static RGBAColor32 AccentColor    => GuiTheme.Palette.Accent;
        private static RGBAColor32 WarningColor   => GuiTheme.Palette.Warn;
        private static RGBAColor32 HintText       => GuiTheme.Palette.DimText;
        private static RGBAColor32 OtaHeaderBg    => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 FrameCountText => GuiTheme.Palette.Success;
        private static RGBAColor32 SelectedRowBg           => GuiTheme.Palette.Selection;

        /// <summary>Per-frame Fill-leaf painter dispatch for the observation list's single RenderLayout
        /// (the per-row exposure value cell). Render-thread-only, so a plain Dictionary is safe.</summary>
        private readonly Dictionary<string, Action<RectF32>> _obsFills = new();

        /// <summary>
        /// The observation list's scroll, its viewport the rect the engine arranges the list to (it is declared
        /// <see cref="Layout.Node.WithScroll"/>, so the router brings it the wheel). The list used to build only
        /// the rows that fit and drop the rest without saying so.
        /// </summary>
        private readonly ListScrollController _obsScroll = new ListScrollController { Mode = ScrollBarMode.Decorative };

        /// <summary>The observation list's scroll, for tests.</summary>
        internal ListScrollController ObservationScroll => _obsScroll;

        /// <summary>Reused scratch buffer for measuring the shared stepper value-column width (no per-frame alloc).</summary>
        private readonly List<string> _stepperValueScratch = [];

        /// <summary>Cached reference to time provider for observation list rendering.</summary>
        private ITimeProvider? _timeProvider;

        /// <summary>Tab state (configuration values, per-OTA camera settings, scroll offset).</summary>
        public SessionTabState State { get; } = new SessionTabState();

        /// <summary>
        /// The config form's scroll. The form is declared <see cref="Layout.Node.WithScroll"/>, so the engine states
        /// its extent and viewport; one atom is one form line (<see cref="ListScrollController.AtomDesignUnits"/>),
        /// which keeps <see cref="SessionTabState.ConfigScrollOffset"/>, the mirror the terminal host reads, in lines
        /// as its rows are. It owns the fractional offset (the trackpad wheel carry) and the clamp. The bar is drawn
        /// as the observation list's is: the form had none, so nothing said it scrolled.
        /// </summary>
        private readonly ListScrollController _configScroll =
            new ListScrollController { Mode = ScrollBarMode.Decorative, AtomDesignUnits = BaseItemHeight };


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
            _timeProvider = timeProvider;
            RenderLayout(Layout.Builder.Spacer().Bg(ContentBg), contentRect);

            // Reinitialize per-OTA settings when the profile changes
            if (State.NeedsReinitialization(appState.ActiveProfile))
            {
                State.InitializeFromProfile(appState.ActiveProfile, appState.CameraCapabilitiesOf);
            }
            // The config form's caveats compare a preference against the profile (the flip deadline against
            // the mount safety limit); the form is built deeper in, where the profile is not in scope.
            State.ActiveProfileData = appState.ActiveProfile?.Data;

            // The config form on the left, a rule, and the right panel (camera settings + observation list). The
            // panel used to take a fixed width and the form the rest, so in a window narrower than the two together
            // the form's steppers ran off its edge and its labels were cut (1000 px at 150% was enough; found by
            // WireframeSweepTests). The form is now given at least what the engine measures its widest row to need,
            // the panel what is left up to its full width, and where even the panel's least does not fit beside the
            // form the two are stacked, the form above. The whole tab is ONE tree in design units, the form and the
            // panel declared in it alike, and each of the two scrolls in the engine (WithScroll).
            // A caveat beside a value is a star, which adds nothing to the measure, so it coming and going does not
            // move the split.
            CollectStepperValues();
            // The tab's width in design units, the unit every node below is declared in.
            var tabW = contentRect.Width / dpiScale;
            // The value column is bounded by the whole tab, so even stacked (where the form has the tab's width) a
            // long value cannot push a row's steppers past its edge.
            _stepperValueW = MeasureValueColumnWidth(
                _stepperValueScratch, FontPath, BaseFontSize,
                minWidth: BaseValueW,
                maxWidth: tabW - BasePadding * 3f - BaseLabelW - BaseStepperBtnW * 2f,
                horizontalPadding: BasePadding);
            // The engine measures in device px; the tree below is in design units.
            var formTree = BuildConfigTree();
            var formNeed = MeasureLayout(
                formTree, new Layout.Size<float>(float.MaxValue, float.MaxValue)).Width / dpiScale;
            // The form scrolls in the engine, one atom a form line: the engine measures it, slides it and clips it,
            // and the row an arrow key lands on is brought into view by the widget base (RevealListRow). It used to
            // sum its own rows into a height and a Y per field, to feed a hand-fed controller and scroll a field
            // into view, beside a panel the engine already scrolled.
            _configScroll.TrackColor = SeparatorColor;
            _configScroll.ThumbColor = GuiTheme.Palette.SeparatorStrong;
            var form = formTree.WithScroll(_configScroll).Bg(ContentBg);
            var panel = BuildRightPanel(plannerState);
            var rule = Layout.Builder.Spacer().Bg(SeparatorColor);
            // Each floor is stated once: the side-by-side tree declares them and the fit test below reads the same
            // values (a star's floor is not part of the engine's measure of the row, so the test cannot ask it).
            var fitsSideBySide = formNeed + BaseSeparatorW + BaseObsPanelLeastWidth <= tabW;
            var tab = fitsSideBySide
                ? Layout.Builder.HStack(
                    form.WStar(1f, min: formNeed).HStar(),
                    rule.WFixed(BaseSeparatorW).HStar(),
                    // Equal shares between the form's floor and the panel's ceiling. The two bounds cannot both bind
                    // (the ceiling binds in a wide tab, the floor in a narrow one), which matters: the engine freezes a
                    // capped star and a floored one in the same pass, and a panel weighted to fill first overflowed
                    // the tab.
                    panel.WStar(1f, min: BaseObsPanelLeastWidth, max: BaseObsPanelWidth).HStar())
                // Stacked, the panel is served first, up to its own measured height, out of what the form's floor
                // leaves, and the form (which scrolls) takes the rest. A fixed share of the height used to split
                // them, which in a short tab left the observations room for their header alone, and in a tall one
                // a band of empty panel under them.
                : Layout.Builder.VStack(
                    form.WStar().HStar(1f, min: BaseItemHeight * StackedFormLeastLines),
                    rule.HFixed(BaseSeparatorW).WStar(),
                    panel.WStar().HStarUpToContent());
            RenderLayout(tab, contentRect,
                drawFill: (fill, r) => { if (fill.Key is { } k && _obsFills.TryGetValue(k, out var painter)) painter(r); });

            // The terminal host reads the form's scroll in lines, which is what an atom is here.
            State.ConfigScrollOffset = _configScroll.AtomOffset;
        }

        // -----------------------------------------------------------------------
        // Input handling
        // -----------------------------------------------------------------------

        public override bool HandleInput(InputEvent evt)
        {
            switch (evt)
            {
                case InputEvent.KeyDown(var key, _):
                    return HandleConfigKey(key);

                default:
                    // Wheel + touch/mouse drag over the config form via the controller (its viewport is the
                    // rect the engine last arranged the form to, so right-panel input falls through untouched;
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

        // Wired on first use: the cursor belongs to the widget base, and there is no constructor of this
        // class to bind it in without giving up the primary one.
        private bool _fieldCursorWired;

        private bool HandleConfigKey(InputKey key)
        {
            switch (key)
            {
                case InputKey.Up:
                case InputKey.Down:
                    SyncFieldCursor();
                    MoveListCursor(key == InputKey.Down ? 1 : -1);
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
        /// Places the keyboard's cursor on the selected field before a step, and hangs the scroll on it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The selection lives in <see cref="SessionTabState.SelectedFieldIndex"/> because the terminal
        /// host reads it too, so the cursor is placed FROM it rather than owning it: a click writes the
        /// index straight, and this brings the cursor to wherever the index is before the arrows move it.
        /// </para>
        /// <para>
        /// Opened with the row COUNT, which is what lets it walk a form taller than its panel. The form is a
        /// scroll node, so an off-screen row registers nothing, and a cursor with no count stops dead at the
        /// bottom of the window -- the reason this list could not take a cursor before DIR.Lib 9.2.
        /// </para>
        /// </remarks>
        private void SyncFieldCursor()
        {
            if (!_fieldCursorWired)
            {
                _fieldCursorWired = true;
                // The step brings its row into view itself (MoveListCursor reveals it in the form's scroll).
                ListCursor.Moved += index =>
                {
                    State.SelectedFieldIndex = index;
                    State.NeedsRedraw = true;
                };
            }

            ListCursor.Open(SessionConfigLayout.FieldListId, State.SelectedFieldIndex, State.FieldCount);
        }

        /// <summary>
        /// Opens the exposure field on proposal <paramref name="proposalIdx"/>, seeded with its current
        /// sub-exposure. Reached by a double-click DECLARED on the exposure cell
        /// (<see cref="Layout.Node.DoubleClickable"/>), which replaced a host arm that caught the second press
        /// and then searched the arranged tree for an "exp:{index}" key under the pointer to learn which cell
        /// it was: the cell that was pressed already knew.
        /// </summary>
        private void BeginExposureEdit(PlannerState plannerState, int proposalIdx)
        {
            var defaultExpSec = SessionContent.DefaultExposureSeconds(State);
            var p = plannerState.Proposals[proposalIdx];
            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);

            State.EditingExposureIndex = proposalIdx;
            State.ExposureInput.OnCommit = text =>
            {
                if (SessionTabState.TryParseExposureInput(text, out var newExp))
                {
                    plannerState.Proposals = plannerState.Proposals.SetItem(
                        proposalIdx, plannerState.Proposals[proposalIdx] with { SubExposure = newExp });
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
            // Through the focus owner, seeded and selected in one call. This was three spellings of
            // one act -- Activate, SelectAll, and a DEFERRED ActivateTextInputSignal that re-focused
            // the field a frame later -- from before the tab had a route to the owner; it has one
            // now, Ui.Focus being the window's own.
            Ui.Focus.Focus(State.ExposureInput, $"{(int)cur.TotalSeconds}");
            State.NeedsRedraw = true;
        }

        // -----------------------------------------------------------------------
        // Right panel: camera settings (top) + observation list (bottom)
        // -----------------------------------------------------------------------

        // Button colors
        private static RGBAColor32 StartBtnBg      => GuiTheme.GoButtonBg;
        private static RGBAColor32 StartBtnText    => GuiTheme.GoButtonInk;
        private static RGBAColor32 DisabledBtnBg   => GuiTheme.Palette.Separator;
        private static RGBAColor32 DisabledBtnText => GuiTheme.Palette.DimText;

        // Shared stepper button styling for this tab (design-unit [-]/[+] look), passed to
        // FormRowLayout.StepperControl so the config + camera steppers share one definition.
        // Computed, never a static readonly field: a field initialised FROM the palette keeps the palette of the moment
        // the class loaded, so F12 turned the window red and left this tab's whole form in Dark (found 2026-11-14).
        private static FormRowLayout.StepperStyle SessionStepperStyle => new(
            StepperBg, BodyText, DisabledBtnBg, DimText, BaseFontSize, BaseStepperBtnW);

        // Design-unit toggle / cycle button widths (the config form's two non-stepper control kinds).
        private const float BaseToggleBtnW = 60f;
        private const float BaseCycleBtnW  = 140f;

        // Bundles every colour + design-unit metric the config form needs into one value so
        // SessionConfigLayout.Build reads them from a single place (mirrors EquipmentPanelStyle).
        // Computed for the reason SessionStepperStyle is.
        private static SessionConfigStyle ConfigStyle => new(
            Stepper: SessionStepperStyle,
            HeaderBg: HeaderBg, HeaderText: HeaderText,
            BodyText: BodyText, DimText: DimText,
            RowBg: ContentBg, RowAltBg: RowAltBg, SelectedRowBg: SelectedRowBg,
            ToggleOnBg: ToggleOnBg, ToggleOffBg: ToggleOffBg, CycleBg: CycleBg,
            DisabledBg: DisabledBtnBg,
            WarnText: GuiTheme.Palette.Warn,
            FontSize: BaseFontSize, HeaderHeight: BaseHeaderHeight, ItemHeight: BaseItemHeight,
            LabelWidth: BaseLabelW, Padding: BasePadding,
            ToggleButtonWidth: BaseToggleBtnW, CycleButtonWidth: BaseCycleBtnW);

        /// <summary>
        /// The right panel as ONE tree in design units: the camera settings at their own height, a rule, the
        /// observations (their list scrolling once it is given less than it holds), and the Start button at the
        /// foot. Its height is the engine's measure of these, which the stacked tab serves first; it used to be
        /// carved by a dock from a camera band summed by hand and a list that counted its rows into what was left.
        /// </summary>
        private Layout.Node BuildRightPanel(PlannerState plannerState)
        {
            var bands = new List<Layout.Node>
            {
                // Each band declares its width: a child of a stack is Auto across it, so rows whose columns are
                // stars would sit at their natural width (each band was the root of its own arrange before, and a
                // root fills its rect).
                BuildCameraSettings().WStar(),
                Layout.Builder.Spacer().RowH(BaseSeparatorW).Bg(SeparatorColor),
                BuildObservationList(plannerState).WStar().HStarUpToContent(),
            };

            // Start button: offered when proposals exist; startable only tonight and with no session running.
            if (plannerState.Proposals.Length > 0)
            {
                var isTonight = !plannerState.PlanningDate.HasValue;
                var btnNode = State.IsSessionRunning
                    ? Layout.Builder.Text("Session running\u2026", BaseFontSize, DisabledBtnText, TextAlign.Center, TextAlign.Center).Bg(DisabledBtnBg)
                    : isTonight
                        ? Layout.Builder.Text("\u25B6 Start Session", BaseFontSize, StartBtnText, TextAlign.Center, TextAlign.Center)
                            .Bg(StartBtnBg).BgHover(GuiTheme.Hover(StartBtnBg))
                            .Clickable(new HitResult.ButtonHit("StartSession"), _ => PostSignal(new StartSessionSignal()))
                        : Layout.Builder.Text("Start (tonight only)", BaseFontSize, DisabledBtnText, TextAlign.Center, TextAlign.Center).Bg(DisabledBtnBg);

                // A plain star above the button takes what the list leaves, so the button rests at the foot of a
                // tall panel as it did when it was docked there.
                bands.Add(Layout.Builder.Spacer().HStar());
                bands.Add(Layout.Builder.VStack(btnNode.RowH(BaseStartButtonHeight)).Pad(BasePadding).WStar());
            }

            return Layout.Builder.VStack([.. bands]).Bg(PanelBg);
        }

        // -----------------------------------------------------------------------
        // Per-OTA camera settings
        // -----------------------------------------------------------------------

        private Layout.Node BuildCameraSettings()
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
                return Layout.Builder.VStack([.. rows]);
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

            return Layout.Builder.VStack([.. rows]);
        }

        // -----------------------------------------------------------------------
        // Observation list with exposure steppers and frame estimates
        // -----------------------------------------------------------------------

        private Layout.Node BuildObservationList(PlannerState plannerState)
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
                return Layout.Builder.VStack([.. rows]);
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

            // Every proposal is a row of the scrolling list below the headers, which the engine clips to the room
            // the panel gives it; it used to build only the rows that fit and drop the rest without a word.
            var items = new List<Layout.Node>(proposals.Length * 3);
            for (var i = 0; i < proposals.Length; i++)
            {
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

                // Exposure value cell. The two states are two different NODES rather than one Fill that
                // branches inside its painter: editing is a TextInput leaf, display stays a Fill because it
                // is genuinely bespoke. The display cell DECLARES its double-click-to-edit, so the press
                // reaches this cell's own proposal index and no host arm has to find the cell again.
                var expKey = $"exp:{i}";
                _obsFills[expKey] = r =>
                    DrawText(expStr, fontPath, r.X, r.Y, r.Width, r.Height, BaseFontSize * 0.9f * dpiScale, BodyText, TextAlign.Center, TextAlign.Center);
                var expCell = State.EditingExposureIndex == capturedI
                    ? Layout.Builder.TextInput(State.ExposureInput, BaseFontSize * 0.9f)
                    : State.IsSessionRunning
                        ? Layout.Builder.Fill(key: expKey)
                        : Layout.Builder.Fill(key: expKey)
                            .Clickable(new HitResult.ButtonHit($"EditExp:{i}"))
                            .DoubleClickable(_ => BeginExposureEdit(plannerState, capturedI))
                            .WithTooltip("Double-click to type an exposure");

                Layout.Node ExpButton(Layout.IconKind icon, string hit, Action<InputModifier> onClick) =>
                    Layout.Builder.Icon(icon, BaseFontSize * 0.85f * Layout.Content.Icon.TextSizeRatio, BodyText)
                        .WFixed(expBtnW).HStar().Bg(StepperBg).BgHover(GuiTheme.Hover(StepperBg))
                        .Clickable(new HitResult.ButtonHit(hit), onClick);

                var expStepper = Layout.Builder.HStack(
                    ExpButton(Layout.IconKind.Minus, $"Dec:Exp:{i}",
                        _ =>
                        {
                            var p = plannerState.Proposals[capturedI];
                            var cur = p.SubExposure ?? TimeSpan.FromSeconds(defaultExpSec);
                            plannerState.Proposals = plannerState.Proposals.SetItem(capturedI, p with { SubExposure = SessionTabState.StepExposure(cur, false) });
                            State.NeedsRedraw = true;
                        }),
                    expCell.Stretch(),
                    ExpButton(Layout.IconKind.Plus, $"Inc:Exp:{i}",
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
                items.Add(Layout.Builder.HStack(
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
                    items.Add(Layout.Builder.HStack(
                            Layout.Builder.Spacer().WFixed(colNumW).HStar(),
                            Layout.Builder.Text($"{startStr}-{endStr}  ({durStr})", BaseFontSize * 0.85f, durColor, TextAlign.Near, TextAlign.Center).WStar())
                        .RowH(BaseObsRowHeight * 0.8f).Bg(bgColor));
                }
                else
                {
                    items.Add(Layout.Builder.Spacer().RowH(BaseObsRowHeight * 0.8f).Bg(bgColor));
                }

                // Thin separator
                items.Add(Layout.Builder.Spacer().RowH(BaseSeparatorW).Bg(SeparatorColor));
            }

            _obsScroll.TrackColor = SeparatorColor;
            _obsScroll.ThumbColor = GuiTheme.Palette.SeparatorStrong;
            rows.Add(Layout.Builder.VStack([.. items]).WithScroll(_obsScroll).WStar().HStarUpToContent());
            return Layout.Builder.VStack([.. rows]);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Configuration form panel (left side, scrollable)
        // -----------------------------------------------------------------------

        // --- Shared stepper value column (design units) ---------------------------------------
        // Size the [-] value [+] cell to the widest current stepper value (e.g. "Auto (3min)") so
        // long strings neither clip nor collide with the buttons, while every stepper aligns in one
        // column. Measured in DESIGN units (BaseFontSize + design min/max) so it slots straight into
        // the design-unit tree below -- the single RenderLayout(dpiScale) scales the whole form at
        // once. MeasureValueColumnWidth returns the minimum when no font is available (headless tests).
        private void CollectStepperValues()
        {
            var groups = SessionConfigGroups.Groups;
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
        }

        /// <summary>
        /// The stepper value column's width in design units: the widest collected value, measured once a frame by
        /// <see cref="Render"/>, so the form is arranged with the column it was measured with.
        /// </summary>
        private float _stepperValueW = BaseValueW;

        private Layout.Node BuildConfigTree() => SessionConfigLayout.Build(
            SessionConfigGroups.Groups, State.Configuration, State.SelectedFieldIndex, State.IsSessionRunning,
            _stepperValueW, ConfigStyle,
            onSelectField: idx => _ => { State.SelectedFieldIndex = idx; State.NeedsRedraw = true; },
            onDecrement: field => _ => { State.Configuration = field.Decrement(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; },
            onIncrement: field => _ => { State.Configuration = field.Increment(State.Configuration); State.IsDirty = true; State.NeedsRedraw = true; },
            profile: State.ActiveProfileData);
    }
}

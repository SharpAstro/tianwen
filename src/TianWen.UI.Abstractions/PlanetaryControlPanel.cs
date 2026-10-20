using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.DAL;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The Planetary mode's capture panel (#1612): its settings and the rows the GUI's planetary view paints down its left side,
/// CAPTURE, RECORD (<see cref="PlanetaryRecordControl"/>), REGION (ROI), RECENTER and FOCUSER. A declaration, so the view and
/// a test arrange the same tree. The view keeps what only it can draw: the picture-in-picture of the sensor (the
/// <see cref="PipKey"/> fill), its drag, and the ROI outlined on the stream.
/// <para>
/// A capture setting (exposure, gain, depth, high speed, the ROI's size and pan) is staged on the capture's
/// <see cref="NodePlanetaryCapture"/> while a capture runs, to take effect on its next frame, and is carried by the next Start
/// otherwise. Start and Stop, the recording and the focuser and mount jogs are signals; the recenter is handed to the capture
/// every frame (<see cref="NodePlanetaryCapture.ConfigureRecenter"/> sends it only when it changed). Render thread only: every
/// field is written by a press the router dispatches on that thread.
/// </para>
/// <para>
/// <b>It fits the left half of a 1080-line screen at 100 %</b>, about 935 design units under the GUI's status bar and the Live
/// Session top strip, and <c>PlanetaryControlPanelFitTests</c> holds every section to it. While a capture runs, every row a
/// user acts on is on screen without scrolling: the rows that are setup only fold away, which today is the picture-in-picture
/// (its drag is idle-only, and it shows the selection, which the recenter's jogs of the running window do not move). Where
/// folding is not enough (idle with the last recording shown, a smaller screen, a larger scale) the panel scrolls, by the wheel.
/// </para>
/// </summary>
public sealed class PlanetaryControlPanel
{
    /// <summary>The panel's width in design units; the view gives it at most half its content.</summary>
    public const float BaseWidth = 280f;

    /// <summary>The key of the fill the view draws the sensor's picture-in-picture into.</summary>
    public const string PipKey = "RoiPip";

    // Panel text follows the GUI's standard metrics (design units; the paint scales them) so it matches the other tabs' chrome.
    private static readonly float PanelFontSize = GuiTheme.Metrics.BaseFontSize;
    private const float BaseRowHeight = 26f;
    private const float BasePanelPad = 8f;
    private const float BaseGap = 5f;

    // Planetary captures a single OTA; its focuser jog targets OTA index 0 (mirrors the single-OTA focus path).
    private const int PlanetaryOtaIndex = 0;

    private static RGBAColor32 ContentBg => GuiTheme.Palette.ContentBg;
    private static RGBAColor32 PanelBg => GuiTheme.Palette.HeaderBg;
    private static RGBAColor32 HeaderText => GuiTheme.Palette.HeaderText;
    private static RGBAColor32 DimText => GuiTheme.Palette.DimText;
    private static RGBAColor32 StartBg => GuiTheme.GoButtonBg;
    private static RGBAColor32 StopBg => GuiTheme.DangerButtonBg;
    private static RGBAColor32 ButtonText => GuiTheme.InkOn(StartBg);
    private static RGBAColor32 SectionText => GuiTheme.Palette.DimText;
    private static RGBAColor32 Divider => GuiTheme.Palette.Separator;
    private static RGBAColor32 MovingText => GuiTheme.Palette.Warn;
    private static RGBAColor32 StepBtnBg => GuiTheme.NeutralButtonBg;
    private static RGBAColor32 JogBg => GuiTheme.NeutralButtonBg;
    private static RGBAColor32 CheckOnBg => GuiTheme.GoButtonBg;

    // Capture settings, edited by the steppers and posted on Start.
    private static readonly double[] ExposurePresetsMs = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
    private int _exposureIdx = 4;                 // 10 ms

    private const int GainStep = 10;
    private const int GainMax = 1000;
    private int _gain = 100;

    // ROI is a FREE rect within the camera's hardware constraints, not a fixed list: these presets are just snapped-to-constraint
    // convenience sizes the size stepper cycles. The chosen rect (_roi) is snapped to the connected camera's RoiConstraints
    // (step, alignment, sensor bounds) every frame.
    private static readonly (int W, int H)[] RoiSizePresets =
        [(320, 240), (512, 512), (640, 360), (640, 480), (800, 600), (1024, 768), (1280, 960)];
    private int _roiSizeIdx = 2;                  // 640x360

    // Fallback sensor when no camera is connected yet, so the PiP still renders a plausible frame. A typical small planetary
    // CMOS (e.g. IMX585-class); replaced by the real sensor dims the moment a camera reports.
    private const int FallbackSensorW = 1936;
    private const int FallbackSensorH = 1096;

    // The current ROI selection (origin + snapped size), the sensor it was snapped against, and whether the ROI rectangle is
    // overlaid on the live stream.
    private RoiRect _roi;
    private RoiConstraints _roiConstraints = RoiConstraints.ForSensor(FallbackSensorW, FallbackSensorH);
    private int _sensorW = FallbackSensorW;
    private int _sensorH = FallbackSensorH;
    private bool _roiInitialised;

    // The stream's depth and high-speed readout, offered only where the camera has a choice (its telemetry's VideoBitDepths and
    // CanFastReadout). A null depth is the camera's own, shown as its deepest.
    private BitDepth? _bitDepth;
    private bool _highSpeed = true;

    // --- RECENTER (Phase C): COM auto-recenter via ROI jog + opt-in coarse mount jog, handed to the capture once a frame. Both
    // are OPT-IN (the user, 2026-09-28): the operator turns the recenter on once the rig is ready for it, and it costs the
    // capture loop about 20 ms a frame at full frame. Mount jog moves the scope, and its sign is uncalibrated. ---
    private PlanetaryRecenterMode _recenter;
    private static readonly int[] DeadbandPresetsPx = [2, 4, 8, 16, 32];
    private int _deadbandIdx = 1;                 // 4 px
    private static readonly int[] RecenterGainPresetsPct = [10, 25, 50, 75, 100];
    private int _gainIdx = 2;                      // 50%
    // The held move's speed, an index into MountJog.SiderealMultiples: 1x, the handset's speed 1.
    private int _moveSpeedIdx = 1;

    // The panel's scroll, for where its rows outgrow the room it is given. The engine states what it scrolls over each frame,
    // and a wheel over the panel moves it (the router asks the region the scrolled stack registers); a bar shows that it does.
    private readonly ListScrollController _scroll = new ListScrollController { Mode = ScrollBarMode.Decorative };

    /// <summary>The panel's scroll, as the last <see cref="Build"/> left it: for a test to read its bar's colours.</summary>
    internal ListScrollController Scroll => _scroll;

    /// <summary>The Record control (#1344): its duration field and its rows.</summary>
    public PlanetaryRecordControl Record { get; } = new PlanetaryRecordControl();

    /// <summary>The ROI selection: where on the sensor the readout window sits, snapped to the camera's rule.</summary>
    public RoiRect Roi => _roi;

    /// <summary>The sensor the selection was snapped against: the connected camera's, else a plausible fallback.</summary>
    public int SensorWidth => _sensorW;

    /// <inheritdoc cref="SensorWidth"/>
    public int SensorHeight => _sensorH;

    /// <summary>Whether the ROI is outlined on the live stream ("Show ROI on image").</summary>

    /// <summary>
    /// Takes the sensor and its ROI rule from <paramref name="telemetry"/> (populated off-thread from the connected camera), so
    /// the picker reflects the REAL camera's constraints before a capture without touching the driver on the render thread; a
    /// plausible sensor stands in before a camera reports. The ROI is (re)centred on first use or a sensor change, and
    /// otherwise re-snapped so it stays legal if the constraints refresh.
    /// </summary>
    public void FollowCamera(PreviewOTATelemetry telemetry)
    {
        int sw, sh;
        RoiConstraints c;
        if (telemetry.CameraConnected && telemetry.SensorWidth > 0 && telemetry.SensorHeight > 0)
        {
            sw = telemetry.SensorWidth;
            sh = telemetry.SensorHeight;
            // A connected camera always reports valid constraints (the DIM default is a free rect over the sensor); guard
            // against an all-zero default just in case the snapshot predates the camera read.
            c = telemetry.RoiConstraints.MaxWidth > 0 ? telemetry.RoiConstraints : RoiConstraints.ForSensor(sw, sh);
        }
        else
        {
            sw = FallbackSensorW;
            sh = FallbackSensorH;
            c = RoiConstraints.ForSensor(sw, sh);
        }

        _roiConstraints = c;
        var sensorChanged = sw != _sensorW || sh != _sensorH;
        _sensorW = sw;
        _sensorH = sh;

        if (!_roiInitialised || sensorChanged)
        {
            var p = RoiSizePresets[_roiSizeIdx];
            _roi = c.Snap(RoiRect.Centered(sw, sh, p.W, p.H));
            _roiInitialised = true;
        }
        else
        {
            _roi = c.Snap(_roi); // keep it legal against the (possibly refreshed) constraints
        }
    }

    /// <summary>Centres the ROI selection on a point of the sensor (the picture-in-picture's drag), snapped and clamped.</summary>
    public void CentreRoiOn(float sensorX, float sensorY)
    {
        var x = (int)MathF.Round(sensorX - _roi.Width / 2f);
        var y = (int)MathF.Round(sensorY - _roi.Height / 2f);
        _roi = _roiConstraints.Snap(new RoiRect(x, y, _roi.Width, _roi.Height));
    }

    /// <summary>
    /// The panel as one tree for a frame: a scrolled column of fixed-height rows, its background the panel's, sized in design
    /// units. Each call is a frame of the panel, so it first hands <paramref name="capture"/> the recenter the panel shows.
    /// <paramref name="telemetry"/> is the OTA's (the focuser's readout and jog, and which depths and readouts the camera
    /// offers), <paramref name="recordingTimeLeft"/> the recording's by this computer's clock
    /// (<see cref="PlanetaryCaptureController.RecordingTimeLeft"/>). <paramref name="pipPress"/> is what a press on the
    /// picture-in-picture does, the view's, since only the view knows where it drew the sensor inside the fill: a press the
    /// scrolled panel's own region would otherwise swallow.
    /// </summary>
    /// <param name="reframe">The reframe going on (#1033), which turns the region section into its confirm card; null for none.</param>
    /// <param name="utcNow">The instant a sequence's rotation light reads the planet at, the app's clock (<c>TIANWEN_NOW</c> included).</param>
    public Layout.Node Build(NodePlanetaryCapture capture, PreviewOTATelemetry telemetry, TimeSpan recordingTimeLeft, SignalBus? bus,
        Func<PointerPress, DragCapture?>? pipPress = null, PlanetaryReframe? reframe = null, DateTimeOffset utcNow = default)
    {
        var capturing = capture.IsCapturing;

        // Keep the capture's recenter config in lock-step with the toggles and steppers (staged only when it changed; one sync
        // point picks up the initial default and any change, no per-click plumbing).
        PushRecenter(capture);

        // The bar in the palette's own colours, read each frame so a theme switch reaches it: DIR.Lib's default is a
        // blue-grey, which Night, holding blue at zero, cannot show.
        _scroll.TrackColor = GuiTheme.Palette.Separator;
        _scroll.ThumbColor = GuiTheme.Palette.SeparatorStrong;

        // --- Capture section ---
        var captureFill = capturing ? StopBg : StartBg;
        var startStop = Layout.Builder.Text(capturing ? "Stop" : "Start", PanelFontSize, ButtonText, TextAlign.Center, TextAlign.Center)
            .Bg(captureFill).BgHover(GuiTheme.Hover(captureFill)).RowH(BaseRowHeight)
            .Clickable(new HitResult.ButtonHit("PlanetaryCaptureToggle"), _ =>
            {
                if (capturing)
                {
                    bus?.Post(new StopVideoCaptureSignal());
                }
                else
                {
                    bus?.Post(new StartVideoCaptureSignal(
                        ExposureMs: ExposurePresetsMs[_exposureIdx],
                        Gain: (short)_gain,
                        RoiWidth: _roi.Width,
                        RoiHeight: _roi.Height,
                        BitDepth: _bitDepth,
                        HighSpeed: telemetry.CanFastReadout ? _highSpeed : null));
                }
            });

        var exposureMs = ExposurePresetsMs[_exposureIdx];

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();
        rows.Add(SectionHeader("CAPTURE"));
        rows.Add(startStop);
        // Exposure, gain, the ROI's size and its pan are all live-tunable WHILE capturing (as on a real planetary capture): a
        // change updates the local value and, when a capture is running, is staged to take effect on its next frame.
        rows.Add(Stepper("Exp", $"{exposureMs:0.##} ms", "Exposure",
            () => { _exposureIdx = Math.Max(0, _exposureIdx - 1); PushExposure(capture, capturing); },
            () => { _exposureIdx = Math.Min(ExposurePresetsMs.Length - 1, _exposureIdx + 1); PushExposure(capture, capturing); }));
        rows.Add(Stepper("Gain", _gain.ToString(), "Gain",
            () => { _gain = Math.Max(0, _gain - GainStep); if (capturing) capture.SetGain(_gain); },
            () => { _gain = Math.Min(GainMax, _gain + GainStep); if (capturing) capture.SetGain(_gain); }));
        // IsDefault first: a camera with no reading yet holds a default array, whose Length throws.
        if (telemetry.VideoBitDepths is { IsDefault: false, Length: > 1 } depths)
        {
            rows.Add(DepthRow(depths, capture, capturing));
        }
        if (telemetry.CanFastReadout)
        {
            rows.Add(CheckRow("High speed", _highSpeed, "HighSpeed", () =>
            {
                _highSpeed = !_highSpeed;
                if (capturing)
                {
                    capture.SetHighSpeed(_highSpeed);
                }
            }));
        }
        if (capturing)
        {
            var depth = capture.FrameBitDepth is { } streamed ? $"   {streamed.BitSize} bit" : "";
            // The current rate (#1619): the run's average blends every exposure the run has had.
            var readout = $"{capture.CurrentFps:F0} fps   {capture.FramesReceived} frm   {capture.DroppedFrames} drop{depth}";
            rows.Add(Layout.Builder.Text(readout, PanelFontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
        }

        // --- Record section (#1344): the node writes the SER and finishes it unwatched; this asks and shows what it says. ---
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
        rows.Add(SectionHeader("RECORD"));
        rows.AddRange(Record.Rows(capture.Recording, capturing, recordingTimeLeft, bus, PanelFontSize, BaseRowHeight, BaseGap * 0.6f));

        // --- Region of Interest section: PiP sensor thumbnail + the ROI rect, size presets (snapped to the camera's
        // RoiConstraints), pan, and the on-stream overlay toggle. All live during capture: Size rebuilds the stream at the new
        // framing on the next frame; Pan jogs the readout window (the fast, mount-free recenter actuator). While idle they
        // position the SELECTION shown in the PiP + overlay, applied on the next Start.
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
        rows.Add(SectionHeader("REGION (ROI)"));
        // Setup only, so it folds while a capture runs (see the class remarks): the fit of a 1080-line screen needs its rows.
        if (!capturing)
        {
            var pip = Layout.Builder.Fill(key: PipKey).RowH(BaseRowHeight * 3f);
            rows.Add(pipPress is null ? pip : pip.Pressable(new HitResult.ButtonHit(PipKey), pipPress));
        }
        // While a reframe is on show the size is the PROPOSAL's, which nothing applies until it is committed.
        void Resize(int index)
        {
            SetRoiSize(index);
            if (reframe is not null)
            {
                reframe.ResizeProposal(_roi.Width, _roi.Height);
            }
            else if (capturing)
            {
                capture.SetRoiSize(_roi.Width, _roi.Height);
            }
        }
        rows.Add(Stepper("Size", reframe is { } sized ? $"{sized.Proposal.Width}x{sized.Proposal.Height}" : $"{_roi.Width}x{_roi.Height}",
            "RoiSize", () => Resize(_roiSizeIdx - 1), () => Resize(_roiSizeIdx + 1)));
        if (reframe is { } proposing)
        {
            // The confirm card (#1033): the proposal is dragged on the full frame; it moves the window, or is let go.
            var p = proposing.Proposal;
            rows.Add(Layout.Builder.Text($"Drag the window on the frame: {p.Width}x{p.Height} at ({p.X}, {p.Y})", PanelFontSize * 0.8f,
                DimText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
            rows.Add(Layout.Builder.HStack(
                    PanelButton("Move window", "ReframeCommit", StartBg, () => bus?.Post(new CommitReframeSignal())).WStar().HStar(),
                    PanelButton("Cancel", "ReframeCancel", StepBtnBg, () => bus?.Post(new CancelReframeSignal())).WStar().HStar())
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
        }
        else
        {
            rows.Add(Layout.Builder.HStack(
                    RowLabel("Pan"),
                    RoiPanButton("◀", "RoiPanLeft", -1, 0, capture, capturing),
                    RoiPanButton("▶", "RoiPanRight", 1, 0, capture, capturing),
                    RoiPanButton("▲", "RoiPanUp", 0, -1, capture, capturing),
                    RoiPanButton("▼", "RoiPanDown", 0, 1, capture, capturing))
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
            // The whole sensor while a capture runs, to reframe on (#1033): it replaced "Show ROI on image", whose outline on a
            // stream that IS the window was the whole image and said nothing.
            if (capturing)
            {
                rows.Add(PanelButton("Full frame", "TakeFullFrame", StepBtnBg, () => bus?.Post(new TakeFullFrameSignal())).RowH(BaseRowHeight));
            }
        }

        // --- Recenter section (Phase C): keep the planet centred. Auto-recenter jogs the readout window (fast, mount-free) to
        // null the disk's COM drift; the opt-in Mount jog is the coarse fallback when the ROI reaches the sensor edge.
        // Deadband/Gain tune the loop; the N/S/E/W buttons are a manual nudge. ---
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
        rows.Add(SectionHeader("RECENTER"));
        var recenterStyle = new Layout.ButtonGroupStyle(CheckOnBg, StepBtnBg, HeaderText, DimText, GuiTheme.Hover(StepBtnBg));
        ReadOnlySpan<Layout.ButtonGroupOption<PlanetaryRecenterMode>> recenterModes =
        [
            new(PlanetaryRecenterMode.Off, "Off") { Hit = new HitResult.ButtonHit("RecenterOff") },
            new(PlanetaryRecenterMode.Roi, "ROI") { Hit = new HitResult.ButtonHit("RecenterRoi") },
            new(PlanetaryRecenterMode.Mount, "Mount") { Hit = new HitResult.ButtonHit("RecenterMount") },
            new(PlanetaryRecenterMode.Both, "Both") { Hit = new HitResult.ButtonHit("RecenterBoth") },
        ];
        rows.Add(Layout.Builder.ButtonGroup(recenterModes, _recenter, chosen => _recenter = chosen, recenterStyle, PanelFontSize * 0.9f)
            .RowH(BaseRowHeight));
        rows.Add(Stepper("Deadband", $"{DeadbandPresetsPx[_deadbandIdx]} px", "RecenterDeadband",
            () => _deadbandIdx = Math.Max(0, _deadbandIdx - 1),
            () => _deadbandIdx = Math.Min(DeadbandPresetsPx.Length - 1, _deadbandIdx + 1)));
        rows.Add(Stepper("Gain", $"{RecenterGainPresetsPct[_gainIdx]}%", "RecenterGain",
            () => _gainIdx = Math.Max(0, _gainIdx - 1),
            () => _gainIdx = Math.Min(RecenterGainPresetsPct.Length - 1, _gainIdx + 1)));
        if (capturing && _recenter is not PlanetaryRecenterMode.Off)
        {
            var (ox, oy) = capture.LastComOffset;
            rows.Add(Layout.Builder.Text(
                    $"off {ox:+0.0;-0.0},{oy:+0.0;-0.0} px   {capture.LastRecenterActuator}",
                    PanelFontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center)
                .RowH(BaseRowHeight * 0.8f));
        }
        // The mount moved while a button is held (the owner, 2026-10-20), at a speed in multiples of sidereal: the slow ones ride
        // on tracking as a guide pulse does, so a held button never leaves the mount untracked.
        var speedStyle = new Layout.ButtonGroupStyle(CheckOnBg, StepBtnBg, HeaderText, DimText, GuiTheme.Hover(StepBtnBg));
        var speeds = new Layout.ButtonGroupOption<int>[MountJog.SiderealMultiples.Length];
        for (var i = 0; i < speeds.Length; i++)
        {
            speeds[i] = new Layout.ButtonGroupOption<int>(i, SpeedLabel(MountJog.SiderealMultiples[i])) { Hit = new HitResult.ButtonHit($"MoveSpeed{i}") };
        }
        rows.Add(Layout.Builder.ButtonGroup<int>(speeds, _moveSpeedIdx, chosen => _moveSpeedIdx = chosen, speedStyle, PanelFontSize * 0.9f)
            .RowH(BaseRowHeight));
        var speed = MountJog.SiderealMultiples[_moveSpeedIdx];
        rows.Add(Layout.Builder.HStack(
                RowLabel("Move"),
                MountMoveButton("N", "MountMoveN", GuideDirection.North, speed, bus),
                MountMoveButton("S", "MountMoveS", GuideDirection.South, speed, bus),
                MountMoveButton("E", "MountMoveE", GuideDirection.East, speed, bus),
                MountMoveButton("W", "MountMoveW", GuideDirection.West, speed, bus))
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));

        // --- Find section (#1684): the planet the capture is for, and where the mount really points, from a full-sensor still the
        // node solves, blind where nothing local can. A focal length off the profile's is offered, never written unasked; the sync
        // and the goto wait for a press, and a planet already in the field is left where it is. Shown with the capture's mount
        // connected, or with a wheel for the sequence's planet, as FILTER is shown only with a wheel, so a colour camera on a rig
        // with neither keeps its panel. ---
        if (capture.MountConnected || telemetry.FilterWheelConnected)
        {
            rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
            rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
            rows.Add(SectionHeader("FIND"));
            AddFindRows(rows, capture, capturing, bus);
        }

        // --- Filter section (#1618): the filter in the beam, and the wheel's filters to turn to. The node changes it, through its
        // one filter change and the focus offset, stopping a running stream for it and starting it again (#1639); a recording
        // going on refuses it in words. Shown only with a wheel connected, so a colour camera's panel does not grow. ---
        if (telemetry.FilterWheelConnected)
        {
            rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
            rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
            rows.Add(SectionHeader("FILTER"));
            AddFilterRows(rows, telemetry, bus);

            // --- Sequence section (#1340): steps of a filter, the stream's settings and a length, taken as sets, one file a step;
            // the node runs it on the capture, and each step's light says how far the planet turns over it. ---
            rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
            rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
            rows.Add(SectionHeader("SEQUENCE"));
            AddSequenceRows(rows, capture, telemetry, capturing, bus, utcNow);
        }

        // --- Focuser section (reuses JogFocuserSignal: the same path the Live Session OTA panel posts) ---
        rows.Add(Layout.Builder.Spacer().RowH(BaseGap));
        rows.Add(Layout.Builder.Box(0f, 1f, Divider).RowH(1f));
        rows.Add(SectionHeader("FOCUSER"));
        if (telemetry.FocuserConnected)
        {
            var focLabel = $"Pos: {telemetry.FocusPosition}";
            if (!double.IsNaN(telemetry.FocuserTempC))
            {
                focLabel += $"   {telemetry.FocuserTempC:F1}°C";
            }
            var position = Layout.Builder.Text(focLabel, PanelFontSize, telemetry.FocuserIsMoving ? MovingText : HeaderText,
                TextAlign.Near, TextAlign.Center);
            // A moving focuser is marked by a pair of drawn carets, the jog row's own marks, never a symbol in the text run.
            rows.Add(telemetry.FocuserIsMoving
                ? Layout.Builder.HStack(
                        position.WStar().HStar(),
                        FormRowLayout.StepMark("‹", PanelFontSize, MovingText).HStar(),
                        FormRowLayout.StepMark("›", PanelFontSize, MovingText).HStar())
                    .RowH(BaseRowHeight)
                : position.RowH(BaseRowHeight));

            // Jog row: [<<] [<] [>] [>>], coarse +/-100, fine +/-10 (matches the Live Session OTA panel).
            rows.Add(Layout.Builder.HStack(
                    JogButton("«", "FocCoarseIn", -100, bus),
                    JogButton("‹", "FocFineIn", -10, bus),
                    JogButton("›", "FocFineOut", 10, bus),
                    JogButton("»", "FocCoarseOut", 100, bus))
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
            rows.Add(Layout.Builder.Text("fine 10  /  coarse 100", PanelFontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center)
                .RowH(BaseRowHeight * 0.7f));
        }
        else
        {
            rows.Add(Layout.Builder.Text("No focuser connected", PanelFontSize, DimText, TextAlign.Near, TextAlign.Center)
                .RowH(BaseRowHeight));
        }

        return Layout.Builder.VStack(rows.ToArray()).Pad(BasePanelPad).WithGap(BaseGap).Bg(PanelBg).WithScroll(_scroll);
    }

    /// <summary>The wheel's filters a row at a time, as a choice each row declares, the one in the beam lit.</summary>
    internal const int FiltersPerRow = 4;

    private static void AddFilterRows(ImmutableArray<Layout.Node>.Builder rows, PreviewOTATelemetry telemetry, SignalBus? bus)
    {
        var filters = telemetry.FilterSlots.IsDefault ? [] : telemetry.FilterSlots;
        if (filters.Length == 0)
        {
            rows.Add(Layout.Builder.Text("The wheel names no filters", PanelFontSize * 0.8f, DimText,
                TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
            return;
        }

        var inBeam = telemetry.FilterPosition;
        rows.Add(Layout.Builder.Text(inBeam >= 0 && inBeam < filters.Length ? $"In the beam: {filters[inBeam]}" : "The wheel is turning",
            PanelFontSize, HeaderText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
        var style = new Layout.ButtonGroupStyle(CheckOnBg, StepBtnBg, HeaderText, DimText, GuiTheme.Hover(StepBtnBg));
        for (var start = 0; start < filters.Length; start += FiltersPerRow)
        {
            var count = Math.Min(FiltersPerRow, filters.Length - start);
            var options = new Layout.ButtonGroupOption<int>[count];
            for (var i = 0; i < count; i++)
            {
                var slot = start + i;
                options[i] = new Layout.ButtonGroupOption<int>(slot, ShortLabel(filters[slot])) { Hit = new HitResult.ButtonHit($"Filter{slot}") };
            }
            rows.Add(Layout.Builder.ButtonGroup<int>(options, inBeam, slot =>
                {
                    if (slot != inBeam)
                    {
                        bus?.Post(new ChangeFilterSignal(PlanetaryOtaIndex, slot));
                    }
                }, style, PanelFontSize * 0.9f)
                .RowH(BaseRowHeight));
        }
    }

    /// <summary>The sequence the SEQUENCE section edits (#1340), kept with the panel between frames.</summary>
    internal PlanetarySequenceDraft Sequence { get; } = new PlanetarySequenceDraft();

    /// <summary>The planets a sequence's light can speak for, as the planet choice offers them.</summary>
    private static readonly CatalogIndex[] TurningPlanets = [CatalogIndex.Mars, CatalogIndex.Jupiter, CatalogIndex.Saturn];

    private void AddSequenceRows(ImmutableArray<Layout.Node>.Builder rows, NodePlanetaryCapture capture, PreviewOTATelemetry telemetry,
        bool capturing, SignalBus? bus, DateTimeOffset now)
    {
        var draft = Sequence;
        var slots = telemetry.FilterSlots.IsDefault ? [] : telemetry.FilterSlots;
        var running = capture.Sequence is { } progress && progress.Phase is not (PlanetarySequencePhase.Done or PlanetarySequencePhase.Stopped
            or PlanetarySequencePhase.Failed);

        // The planet the light speaks for: the one the FIND section names, else the one the node found the mount pointing at.
        var planet = draft.Planet ?? capture.Planet;

        for (var i = 0; i < draft.Steps.Length; i++)
        {
            var index = i;
            var step = draft.Steps[i];
            var filter = step.FilterSlot < slots.Length ? ShortLabel(slots[step.FilterSlot]) : $"#{step.FilterSlot + 1}";
            var duration = PlanetarySequenceDraft.DurationOf(step, capture.CurrentFps);
            var turn = planet is { } turning && duration is { } span ? PlanetaryTurn.TurnPx(turning, now, span, telemetry.PixelScaleArcsec) : double.NaN;
            var light = PlanetaryTurn.LightFor(turn);
            var turnText = double.IsFinite(turn) ? $"{turn:0.0} px" : "turn ?";
            rows.Add(Layout.Builder.HStack(
                    PanelButton($"{index + 1}. {filter}", $"SeqFilter{index}", StepBtnBg, () => draft.NextFilter(index, slots.Length)).WFixed(64f).HStar(),
                    Layout.Builder.Text($"{step.ExposureMs:0.##} ms g{step.Gain}", PanelFontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center).WStar().HStar(),
                    Layout.Builder.Text(turnText, PanelFontSize * 0.8f, LightColour(light), TextAlign.Far, TextAlign.Center).WFixed(52f).HStar(),
                    PanelButton("Drop", $"SeqDrop{index}", StepBtnBg, () => draft.Remove(index)).WFixed(40f).HStar())
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
            rows.Add(Layout.Builder.HStack(
                    Stepper("  Len", step.ByFrames ? $"{step.Frames} fr" : $"{step.Seconds} s", $"SeqLen{index}",
                        () => draft.StepLength(index, -1), () => draft.StepLength(index, 1)).WStar().HStar(),
                    PanelButton(step.ByFrames ? "frames" : "time", $"SeqUnit{index}", StepBtnBg, () => draft.ToggleLength(index)).WFixed(52f).HStar())
                .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));
        }

        rows.Add(Layout.Builder.HStack(
                PanelButton("Add step", "SeqAdd", StepBtnBg, () => draft.Add(slots.Length, ExposurePresetsMs[_exposureIdx], (short)_gain)).WStar().HStar(),
                Stepper("Sets", draft.Sets.ToString(System.Globalization.CultureInfo.InvariantCulture), "SeqSets",
                    () => draft.StepSets(-1), () => draft.StepSets(1), alignedLabel: false).WStar().HStar())
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));

        if (running)
        {
            rows.Add(PanelButton("Stop sequence", "SeqStop", StopBg, () => bus?.Post(new StopPlanetarySequenceSignal())).RowH(BaseRowHeight));
        }
        else if (capturing && !draft.Steps.IsEmpty)
        {
            rows.Add(PanelButton("Start sequence", "SeqStart", StartBg, () => bus?.Post(new StartPlanetarySequenceSignal(draft.ToRequest())))
                .RowH(BaseRowHeight));
        }
        if (capture.Sequence is { } shown)
        {
            rows.Add(Layout.Builder.Text(SequenceInWords(shown), PanelFontSize * 0.8f, shown.FailureReason is null ? DimText : GuiTheme.Palette.Error,
                TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight));
        }
    }

    /// <summary>A find's exposure choices, in seconds: a star field through a planetary focal length wants seconds, not milliseconds.</summary>
    internal static readonly double[] FindExposurePresetsS = [1, 2, 5, 10, 20, 30];

    private int _findExposureIdx = 2;

    private void AddFindRows(ImmutableArray<Layout.Node>.Builder rows, NodePlanetaryCapture capture, bool capturing, SignalBus? bus)
    {
        var draft = Sequence;
        var planet = draft.Planet ?? capture.Planet;
        var planetStyle = new Layout.ButtonGroupStyle(CheckOnBg, StepBtnBg, HeaderText, DimText, GuiTheme.Hover(StepBtnBg));
        ReadOnlySpan<Layout.ButtonGroupOption<CatalogIndex>> planets =
        [
            new(TurningPlanets[0], "Mars") { Hit = new HitResult.ButtonHit("PlanetMars") },
            new(TurningPlanets[1], "Jupiter") { Hit = new HitResult.ButtonHit("PlanetJupiter") },
            new(TurningPlanets[2], "Saturn") { Hit = new HitResult.ButtonHit("PlanetSaturn") },
        ];
        rows.Add(Layout.Builder.ButtonGroup(planets, planet ?? default, chosen => draft.Planet = chosen, planetStyle, PanelFontSize * 0.9f)
            .RowH(BaseRowHeight));
        if (!capturing || !capture.MountConnected)
        {
            return;
        }

        var exposure = FindExposurePresetsS[_findExposureIdx];
        rows.Add(Layout.Builder.HStack(
                Stepper("Exp", $"{exposure:0.#} s", "FindExposure",
                    () => _findExposureIdx = Math.Max(0, _findExposureIdx - 1),
                    () => _findExposureIdx = Math.Min(FindExposurePresetsS.Length - 1, _findExposureIdx + 1)).WStar().HStar(),
                PanelButton("Find", "FindPlanet", StepBtnBg, () => bus?.Post(new FindPlanetSignal(exposure, planet))).WFixed(52f).HStar())
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f));

        if (capture.Find is not { } found)
        {
            return;
        }
        foreach (var line in FindInLines(found))
        {
            rows.Add(Layout.Builder.Text(line, PanelFontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight * 0.8f));
        }
        if (found.FocalLengthToOffer is { } offer)
        {
            rows.Add(PanelButton($"Use {offer} mm in the profile", "FindUseFocalLength", StepBtnBg,
                () => bus?.Post(new UseMeasuredFocalLengthSignal(PlanetaryOtaIndex, offer))).RowH(BaseRowHeight));
        }
        var target = planet ?? found.Planet;
        rows.Add(found.PlanetInField || target is null
            ? PanelButton("Sync the mount here", "FindSync", StartBg, () => bus?.Post(new FindPlanetGoSignal(target, SyncOnly: true))).RowH(BaseRowHeight)
            : PanelButton($"Sync, go to {target}", "FindGo", StartBg, () => bus?.Post(new FindPlanetGoSignal(target, SyncOnly: false))).RowH(BaseRowHeight));
    }

    /// <summary>A find's result as the panel's short lines: where it solved, how far the mount was off, where the planet is.</summary>
    internal static string[] FindInLines(PlanetaryFindDto found)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new System.Collections.Generic.List<string>(3)
        {
            string.Create(culture, $"RA {found.RaJ2000:0.000} h  Dec {found.DecJ2000:+0.00;-0.00}  {found.ScaleArcsec:0.000}\"/px"),
        };
        if (found.MountOffsetDeg is { } off)
        {
            lines.Add(string.Create(culture, $"The mount was {off:0.00} deg off"));
        }
        if (found.Planet is { } body && found.PlanetSeparationDeg is { } sep)
        {
            lines.Add(found.PlanetInField
                ? $"{body} is in the field"
                : string.Create(culture, $"{body} is {sep:0.00} deg away"));
        }
        return [.. lines];
    }

    /// <summary>Where the node's sequence is, in a line: "Set 2/10, step 1/3 (Red), 4 files", or how it ended.</summary>
    internal static string SequenceInWords(PlanetarySequenceDto sequence) => sequence.Phase switch
    {
        PlanetarySequencePhase.Done => $"Done: {sequence.Files.Length} files",
        PlanetarySequencePhase.Stopped => $"Stopped: {sequence.Files.Length} files kept",
        PlanetarySequencePhase.Failed => $"Failed: {sequence.FailureReason}",
        PlanetarySequencePhase.Pausing => $"Set {sequence.Set}/{sequence.Sets} done, pausing; {sequence.Files.Length} files",
        _ => $"Set {sequence.Set}/{sequence.Sets}, step {sequence.Step}/{sequence.Steps}{(sequence.Filter is { } f ? $" ({f})" : "")}, {sequence.Files.Length} files",
    };

    // The light's colour: green under a pixel, yellow and orange while the de-rotation carries it, red past the limit.
    private static RGBAColor32 LightColour(PlanetaryTurnLight light) => light switch
    {
        PlanetaryTurnLight.Green => GuiTheme.Palette.Success,
        PlanetaryTurnLight.Yellow => GuiTheme.Palette.Warn,
        PlanetaryTurnLight.Orange => GuiTheme.Mix(GuiTheme.Palette.Warn, GuiTheme.Palette.Error, 0.5f),
        PlanetaryTurnLight.Red => GuiTheme.Palette.Error,
        _ => DimText,
    };

    // What fits a quarter of the panel: a known filter's short name (L, R, Ha), else the name it was given.
    internal static string ShortLabel(string name) => new InstalledFilter(name) switch
    {
        { CustomName: { } custom } => custom,
        { Filter.ShortName: { Length: > 0 } shortName } => shortName,
        var filter => filter.DisplayName,
    };

    // A labelled button of the panel's: its fill lit under the pointer, a press runs the action.
    private static Layout.Node PanelButton(string label, string id, RGBAColor32 fill, Action onClick)
        => Layout.Builder.Text(label, PanelFontSize, ButtonText, TextAlign.Center, TextAlign.Center)
            .Bg(fill).BgHover(GuiTheme.Hover(fill))
            .Clickable(new HitResult.ButtonHit(id), _ => onClick());

    /// <summary>The widest label a row of the panel carries, which sizes every row's label column alike.</summary>
    internal const string LabelWidthSample = "Deadband";

    // A row's label, its column measured by the engine from the widest label, so every row's label lines up and none clips
    // (a fixed 52 cut "Deadband" and "Nudge" to "Dead..." and "Nu...").
    private static Layout.Node RowLabel(string label, bool aligned = true)
        => Layout.Builder.Text(label, PanelFontSize, DimText, TextAlign.Near, TextAlign.Center, widthSample: aligned ? LabelWidthSample : null)
            .HStar();

    private static Layout.Node SectionHeader(string text)
        => Layout.Builder.Text(text, PanelFontSize, SectionText, TextAlign.Near, TextAlign.Center).RowH(BaseRowHeight);

    // "label [-] value [+]" as one fixed-height row; a press runs the action, and the new value shows next frame.
    // A stepper sharing its row with another control (Sets, beside Add step) sizes its label to itself, not to the column.
    private static Layout.Node Stepper(string label, string value, string idPrefix, Action onDec, Action onInc, bool alignedLabel = true)
    {
        // Its marks resolve where every step, jog and pan mark does (CLAUDE.md, Layout DSL).
        var dec = FormRowLayout.StepMark("-", PanelFontSize, ButtonText)
            .WFixed(BaseRowHeight).HStar().Bg(StepBtnBg).BgHover(GuiTheme.Hover(StepBtnBg))
            .Clickable(new HitResult.ButtonHit(idPrefix + "Dec"), _ => onDec());
        var inc = FormRowLayout.StepMark("+", PanelFontSize, ButtonText)
            .WFixed(BaseRowHeight).HStar().Bg(StepBtnBg).BgHover(GuiTheme.Hover(StepBtnBg))
            .Clickable(new HitResult.ButtonHit(idPrefix + "Inc"), _ => onInc());
        return Layout.Builder.HStack(
                RowLabel(label, alignedLabel),
                dec,
                Layout.Builder.Text(value, PanelFontSize, HeaderText, TextAlign.Center, TextAlign.Center).WStar().HStar(),
                inc)
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f);
    }

    // The stream's depth, one segment per depth the camera streams in; a change while capturing restarts its stream in it.
    private Layout.Node DepthRow(ImmutableArray<BitDepth> depths, NodePlanetaryCapture capture, bool capturing)
    {
        var options = new Layout.ButtonGroupOption<BitDepth>[depths.Length];
        for (var i = 0; i < depths.Length; i++)
        {
            options[i] = new Layout.ButtonGroupOption<BitDepth>(depths[i], $"{depths[i].BitSize} bit") { Hit = new HitResult.ButtonHit($"Depth{depths[i].BitSize}") };
        }
        var selected = _bitDepth is { } chosen && depths.Contains(chosen) ? chosen : depths[^1];
        var style = new Layout.ButtonGroupStyle(CheckOnBg, StepBtnBg, HeaderText, DimText, GuiTheme.Hover(StepBtnBg));
        return Layout.Builder.HStack(
                RowLabel("Depth"),
                Layout.Builder.ButtonGroup<BitDepth>(options, selected, depth =>
                {
                    _bitDepth = depth;
                    if (capturing)
                    {
                        capture.SetBitDepth(depth);
                    }
                }, style, PanelFontSize * 0.9f).Stretch())
            .RowH(BaseRowHeight).WithGap(BaseGap * 0.6f);
    }

    private static Layout.Node JogButton(string glyph, string id, int steps, SignalBus? bus)
        => FormRowLayout.StepMark(glyph, PanelFontSize, ButtonText)
            .WStar().HStar().Bg(JogBg).BgHover(GuiTheme.Hover(JogBg))
            .Clickable(new HitResult.ButtonHit(id), _ => bus?.Post(new JogFocuserSignal(PlanetaryOtaIndex, steps)));

    // Cycle the size preset, snapping to the current constraints; keep the current origin (Snap re-clamps so the new size still
    // fits the sensor).
    private void SetRoiSize(int idx)
    {
        _roiSizeIdx = Math.Clamp(idx, 0, RoiSizePresets.Length - 1);
        var p = RoiSizePresets[_roiSizeIdx];
        _roi = _roiConstraints.Snap(new RoiRect(_roi.X, _roi.Y, p.W, p.H));
    }

    // One pan step in sensor pixels: ~5% of the sensor per click (at least one origin step).
    private (int Dx, int Dy) RoiPanDelta(int dirX, int dirY)
        => (dirX * Math.Max(_roiConstraints.OriginStepX, _sensorW / 20),
            dirY * Math.Max(_roiConstraints.OriginStepY, _sensorH / 20));

    // Pan the ROI selection origin (the PiP / overlay), then snap to alignment + bounds.
    private void PanRoi(int dirX, int dirY)
    {
        var (dx, dy) = RoiPanDelta(dirX, dirY);
        _roi = _roiConstraints.Snap(new RoiRect(_roi.X + dx, _roi.Y + dy, _roi.Width, _roi.Height));
    }

    private Layout.Node RoiPanButton(string glyph, string id, int dirX, int dirY, NodePlanetaryCapture capture, bool capturing)
    {
        // Move the SELECTION (PiP / overlay); while capturing also jog the live readout window by the same pixel delta: the
        // fast, mount-free recenter actuator.
        void Pan()
        {
            if (capturing)
            {
                var (dx, dy) = RoiPanDelta(dirX, dirY);
                capture.JogRoi(dx, dy);
            }
            PanRoi(dirX, dirY);
        }

        return FormRowLayout.StepMark(glyph, PanelFontSize, ButtonText)
            .WStar().HStar().Bg(JogBg).BgHover(GuiTheme.Hover(JogBg))
            .Clickable(new HitResult.ButtonHit(id), _ => Pan());
    }

    // Stages the selected exposure preset on a running capture (nothing while idle; the next Start carries it).
    private void PushExposure(NodePlanetaryCapture capture, bool capturing)
    {
        if (capturing)
        {
            capture.SetExposure(TimeSpan.FromMilliseconds(ExposurePresetsMs[_exposureIdx]));
        }
    }

    // A checkbox row: green bg when on, neutral when off, and a DRAWN tick in a well (DIR.Lib's declared Checkbox), the whole
    // row toggling.
    private static Layout.Node CheckRow(string label, bool on, string id, Action onClick)
    {
        var style = new Layout.CheckboxStyle(ContentBg, CheckOnBg, DimText, GuiTheme.Hover(on ? CheckOnBg : StepBtnBg))
        {
            CheckedLabelColor = HeaderText,
            RowFill = StepBtnBg,
            CheckedRowFill = CheckOnBg,
            BoxSize = PanelFontSize * 0.9f,
        };
        return Layout.Builder.Checkbox(label, on, _ => onClick(), style, PanelFontSize * 0.9f, new HitResult.ButtonHit(id))
            .PadX(4f)
            .RowH(BaseRowHeight);
    }

    // A mount move button: the press starts the move and holds it, the release (wherever the pointer is let go) stops it.
    private static Layout.Node MountMoveButton(string glyph, string id, GuideDirection direction, double speed, SignalBus? bus)
        => Layout.Builder.Text(glyph, PanelFontSize, ButtonText, TextAlign.Center, TextAlign.Center)
            .WStar().HStar().Bg(JogBg).BgHover(GuiTheme.Hover(JogBg))
            .Pressable(new HitResult.ButtonHit(id), _ =>
            {
                bus?.Post(new StartMountMoveSignal(direction, speed));
                return new DragCapture(static _ => { }, _ => bus?.Post(new StopMountMoveSignal()));
            });

    // "0.5x", "1x", "32x": a speed as the choice shows it.
    internal static string SpeedLabel(double multiple)
        => multiple.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x";

    // The recenter the panel shows, handed to the capture (staged only when it changed).
    private void PushRecenter(NodePlanetaryCapture capture)
        => capture.ConfigureRecenter(_recenter is not PlanetaryRecenterMode.Off, _recenter is PlanetaryRecenterMode.Mount or PlanetaryRecenterMode.Both,
            DeadbandPresetsPx[_deadbandIdx], RecenterGainPresetsPct[_gainIdx] / 100.0,
            roiJog: _recenter is not PlanetaryRecenterMode.Mount);
}

/// <summary>
/// How the capture panel recentres the planet (the owner, 2026-10-20: one choice in place of two checkboxes): not at all, by
/// panning the window, by the mount (the window left where it is), or both (the window first, the mount once the window reaches
/// the sensor's edge). The mount's sign is uncalibrated, so its moves are capped.
/// </summary>
public enum PlanetaryRecenterMode
{
    Off,
    Roi,
    Mount,
    Both,
}

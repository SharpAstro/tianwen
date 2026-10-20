using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.DAL;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Planetary mode's capture panel (#1612) as the GUI's planetary view paints it: what each control posts or stages on the
/// capture, pressed through the router where it was painted, as each did when the panel was built inside the view. Its height
/// in the tallest state is <c>PlanetaryControlPanelFitTests</c>'; the Record control's own rows are
/// <see cref="PlanetaryRecordControlTests"/>'.
/// </summary>
public class PlanetaryControlPanelTests
{
    /// <summary>A camera with a choice of depth and a high-speed readout, a focuser, and the fallback sensor's size.</summary>
    /// <summary>The owner's Saturn night (2026-10-07), the instant every panel here is painted at.</summary>
    internal static readonly DateTimeOffset Night = new DateTimeOffset(2026, 10, 7, 11, 30, 0, TimeSpan.Zero);

    internal static readonly PreviewOTATelemetry Rig = PreviewOTATelemetry.Unknown with
    {
        CameraConnected = true,
        FocuserConnected = true,
        FocusPosition = 12345,
        FocuserTempC = 8.5,
        SensorWidth = 1936,
        SensorHeight = 1096,
        RoiConstraints = RoiConstraints.ForSensor(1936, 1096),
        VideoBitDepths = [BitDepth.Int8, BitDepth.Int16],
        CanFastReadout = true,
    };

    /// <summary>The smallest planetary view: the panel's tree, painted as <c>VkPlanetaryTab</c> paints it, in its own rect.</summary>
    internal sealed class Host(RgbaImageRenderer renderer) : PixelWidgetBase<RgbaImage>(renderer)
    {
        public void Paint(PlanetaryControlPanel panel, NodePlanetaryCapture capture, PreviewOTATelemetry telemetry, SignalBus? bus,
            RectF32 rect, TimeSpan timeLeft = default, Func<PointerPress, DragCapture?>? pipPress = null, PlanetaryReframe? reframe = null)
        {
            BeginFrame();
            panel.FollowCamera(telemetry);
            RenderLayout(panel.Build(capture, telemetry, timeLeft, bus, pipPress, reframe, Night), rect);
        }

        /// <summary>Every text leaf the last paint arranged, for a test that reads what the panel says.</summary>
        public IReadOnlyList<string> Texts()
            => [.. GetCapturedLayout().Select(static arranged => arranged.Node).OfType<Layout.Node.Leaf>()
                .Select(static leaf => leaf.Content).OfType<Layout.Content.Text>().Select(static text => text.Value)];

        /// <summary>The widths the last paint arranged for every text leaf reading <paramref name="text"/>.</summary>
        public IReadOnlyList<float> WidthsOf(string text)
            => [.. GetCapturedLayout().Where(arranged => arranged.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var value } } && value == text)
                .Select(static arranged => arranged.Bounds.Width)];

        /// <summary>
        /// What the paint drew for every text leaf reading <paramref name="text"/>: its run fitted to the box the engine arranged
        /// for it, by the painter's own fit (<see cref="TextFit.ForWidth"/>, under the run's own trim), so a cut label reads
        /// "Dead..." here as it did on screen.
        /// </summary>
        public IReadOnlyList<string> PaintedAs(string text)
        {
            var context = MeasureContext();
            return [.. GetCapturedLayout()
                .Where(arranged => arranged.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var value } } && value == text)
                .Select(arranged => ((Layout.Content.Text)((Layout.Node.Leaf)arranged.Node).Content, arranged.Bounds.Width))
                .Select(run => TextFit.ForWidth(Renderer, run.Item1.Value, context.FontPath, context.Fallback,
                    run.Item1.FontSize * context.FontScale, run.Width, run.Item1.Trim).Text)];
        }

        /// <summary>Presses the control <paramref name="action"/> names the way a click arrives, through the router, where it was painted.</summary>
        public void Press(string action)
        {
            var region = GetRegisteredRegions().Where(r => r.Result is HitResult.ButtonHit hit && hit.Action == action)
                .Select(static r => (ClickableRegion?)r).SingleOrDefault().ShouldNotBeNull($"the panel offers {action}");
            var (x, y) = (region.X + region.Width / 2f, region.Y + region.Height / 2f);
            var router = new InputRouter(Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [this] };
            router.Handle(new InputEvent.MouseDown(x, y));
            router.Handle(new InputEvent.MouseUp(x, y));
        }
    }

    /// <summary>A panel, its capture and a bus that keeps every signal the panel posts, painted once at <paramref name="scale"/>.</summary>
    private sealed class Rigged : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly SignalBus _bus = new SignalBus();
        private readonly float _scale;
        private readonly float _height;

        public Rigged(float scale, bool capturing, PreviewOTATelemetry? telemetry = null, float height = 1100)
        {
            _scale = scale;
            _height = height;
            Telemetry = telemetry ?? Rig;
            _renderer = new RgbaImageRenderer((uint)(PlanetaryControlPanel.BaseWidth * scale), (uint)(height * scale));
            Host = new Host(_renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            _bus.Subscribe<StartVideoCaptureSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopVideoCaptureSignal>(s => Posted.Add(s));
            _bus.Subscribe<JogFocuserSignal>(s => Posted.Add(s));
            _bus.Subscribe<JogMountSignal>(s => Posted.Add(s));
            _bus.Subscribe<StartPlanetaryRecordingSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopPlanetaryRecordingSignal>(s => Posted.Add(s));
            _bus.Subscribe<ChangeFilterSignal>(s => Posted.Add(s));
            _bus.Subscribe<TakeFullFrameSignal>(s => Posted.Add(s));
            _bus.Subscribe<CommitReframeSignal>(s => Posted.Add(s));
            _bus.Subscribe<CancelReframeSignal>(s => Posted.Add(s));
            _bus.Subscribe<StartPlanetarySequenceSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopPlanetarySequenceSignal>(s => Posted.Add(s));
            _bus.Subscribe<FindPlanetSignal>(s => Posted.Add(s));
            _bus.Subscribe<StartMountMoveSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopMountMoveSignal>(s => Posted.Add(s));
            _bus.Subscribe<FindPlanetGoSignal>(s => Posted.Add(s));
            _bus.Subscribe<UseMeasuredFocalLengthSignal>(s => Posted.Add(s));
            if (capturing)
            {
                Capture.Began();
                Capture.UpdateFromAnswer(new PlanetaryStateDto { Camera = "ZWO ASI462MC", Running = true, RoiWidth = 640, RoiHeight = 360 });
            }
            Paint();
            // The first frame hands the capture the recenter the panel shows; what a press stages comes after it.
            Capture.TakeChanges();
        }

        public PlanetaryControlPanel Panel { get; } = new PlanetaryControlPanel();

        public NodePlanetaryCapture Capture { get; } = new NodePlanetaryCapture();

        public PreviewOTATelemetry Telemetry { get; }

        /// <summary>The reframe on show (#1033), which the next paint builds the panel with; null for none.</summary>
        public PlanetaryReframe? Reframe { get; set; }

        public Host Host { get; }

        public List<object> Posted { get; } = [];

        /// <summary>A frame of the panel, in its design width and a height it never needs.</summary>
        public void Paint() => Host.Paint(Panel, Capture, Telemetry, _bus,
            new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * _scale, _height * _scale), reframe: Reframe);

        /// <summary>Presses the control <paramref name="action"/> names through the router, then paints the next frame.</summary>
        public void Press(string action)
        {
            Host.Press(action);
            _bus.ProcessPending();
            Paint();
        }

        public void Dispose() => _renderer.Dispose();
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void StartPostsTheCaptureTheSettingsDescribeAndStagesNothing(float scale)
    {
        using var rig = new Rigged(scale, capturing: false);

        rig.Press("ExposureInc");     // 10 ms to 20 ms
        rig.Press("GainInc");         // 100 to 110
        rig.Press("Depth8");          // the camera's own (its deepest) to 8 bit
        rig.Press("HighSpeed");       // on to off
        rig.Press("RoiSizeInc");      // 640x360 to 640x480
        rig.Capture.TakeChanges().ShouldBeNull("while idle a setting is the next Start's, never the capture's");
        rig.Press("PlanetaryCaptureToggle");

        rig.Posted.ShouldBe([new StartVideoCaptureSignal(ExposureMs: 20, Gain: 110, RoiWidth: 640, RoiHeight: 480,
            BitDepth: BitDepth.Int8, HighSpeed: false)]);
    }

    /// <summary>A mono camera's wheel: the filter in the beam is said and lit, and a press on another asks the node to turn to it (#1618).</summary>
    private static readonly PreviewOTATelemetry WithWheel = Rig with
    {
        FilterWheelConnected = true,
        FilterName = "Green",
        FilterPosition = 2,
        FilterSlots = ["Luminance", "Red", "Green", "Blue", "H-Alpha", "OIII", "IR 685", "Dark"],
    };

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.5f, true)]
    public void APressOnAFilterAsksForItAndOneOnTheFilterInTheBeamAsksNothing(float scale, bool capturing)
    {
        using var rig = new Rigged(scale, capturing, WithWheel);

        rig.Press("Filter2");
        rig.Posted.ShouldBeEmpty("Green is in the beam already");
        rig.Press("Filter6");
        rig.Press("Filter0");

        rig.Posted.ShouldBe([new ChangeFilterSignal(0, 6), new ChangeFilterSignal(0, 0)]);
    }

    [Fact]
    public void EveryFilterOfTheWheelIsOfferedByItsShortName()
    {
        using var rig = new Rigged(1f, capturing: false, WithWheel);

        var filters = rig.Host.GetRegisteredRegions().Select(static r => r.Result).OfType<HitResult.ButtonHit>()
            .Where(static hit => hit.Action.StartsWith("Filter", StringComparison.Ordinal)).Select(static hit => hit.Action);
        filters.ShouldBe(["Filter0", "Filter1", "Filter2", "Filter3", "Filter4", "Filter5", "Filter6", "Filter7"]);
        PlanetaryControlPanel.ShortLabel("Luminance").ShouldBe("L");
        PlanetaryControlPanel.ShortLabel("H-Alpha").ShouldBe("Hα");
        PlanetaryControlPanel.ShortLabel("IR 685").ShouldBe("IR 685", "a filter it does not know keeps the name it was given");
    }

    /// <summary>The owner's rig of 2026-10-07: a mono camera on a wheel at 1780 mm (0.2316"/px), streaming Saturn at 63 fps.</summary>
    private static readonly PreviewOTATelemetry Saturn1780 = WithWheel with { PixelScaleArcsec = 0.2316 };

    private static void Streaming(NodePlanetaryCapture capture, double fps, PlanetarySequenceDto? sequence = null)
        => capture.UpdateFromAnswer(new PlanetaryStateDto
        {
            Camera = "G3M678M", Running = true, RoiWidth = 640, RoiHeight = 360, CurrentFramesPerSecond = fps, Sequence = sequence,
        });

    [Fact]
    public void TheSequenceSectionBuildsStepsAndStartsThemOnTheNodeAsItsRequest()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);

        rig.Press("SeqAdd");            // L at the panel's 10 ms and gain 100, 10,000 frames
        rig.Press("SeqAdd");            // the next slot, R
        rig.Press("SeqUnit1");          // R by time
        rig.Press("SeqLen1Inc");        // 60 s to 90 s
        rig.Press("SeqSetsInc");        // 2 sets
        rig.Press("SeqStart");

        var start = rig.Posted.OfType<StartPlanetarySequenceSignal>().ShouldHaveSingleItem().Request;
        start.Sets.ShouldBe(2);
        start.Steps.Select(static s => (s.FilterPosition, s.ExposureMs, s.Gain, s.Frames, s.DurationSeconds))
            .ShouldBe([(0, 10.0, (short?)100, (int?)10000, (double?)null), (1, 10.0, (short?)100, (int?)null, (double?)90)]);
    }

    [Fact]
    public void EachStepsLightSaysHowFarThePlanetTurnsOverIt()
    {
        // 10,000 frames at 63 fps is 158 s, which turns Saturn's middle 1.1 px at 0.2316"/px: just past the green.
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);
        Streaming(rig.Capture, 63.3);
        rig.Press("PlanetSaturn");
        rig.Press("SeqAdd");

        rig.Host.Texts().ShouldContain("1.1 px");
        rig.Press("SeqLen0Dec");        // 10,000 to 5,000 frames: 79 s
        rig.Host.Texts().ShouldContain("0.5 px");
    }

    [Fact]
    public void WhileTheNodeRunsTheSequenceThePanelSaysWhereItIsAndOffersAStop()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);
        rig.Press("SeqAdd");
        Streaming(rig.Capture, 63.3, new PlanetarySequenceDto
        {
            Phase = PlanetarySequencePhase.Recording, Set = 2, Sets = 10, Step = 1, Steps = 3, Filter = "Red", Files = ["a.ser", "b.ser", "c.ser", "d.ser"],
        });
        rig.Paint();

        rig.Host.Texts().ShouldContain("Set 2/10, step 1/3 (Red), 4 files");
        rig.Press("SeqStop");
        rig.Posted.ShouldBe([new StopPlanetarySequenceSignal()]);
        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "SeqStart" })
            .ShouldBeFalse("one sequence at a time");
    }

    [Fact]
    public void FindTakesAStillAtItsOwnExposureForThePlanetChosen()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);
        Found(rig, find: null);
        rig.Press("PlanetSaturn");
        rig.Press("FindExposureInc");   // 5 s to 10 s
        rig.Press("FindPlanet");

        rig.Posted.OfType<FindPlanetSignal>().ShouldHaveSingleItem().ShouldBe(new FindPlanetSignal(10, CatalogIndex.Saturn));
        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "FindGo" or "FindSync" })
            .ShouldBeFalse("nothing to go to before a find");
    }

    [Fact]
    public void AFindsResultOffersTheMeasuredFocalLengthAndTheGoToThePlanet()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);
        Found(rig, new PlanetaryFindDto
        {
            Summary = "", RaJ2000 = 22.6, DecJ2000 = -10.9, ScaleArcsec = 0.2316, MountOffsetDeg = 8.71, FocalLengthToOffer = 1780,
            Planet = CatalogIndex.Saturn, PlanetSeparationDeg = 8.7, PlanetInField = false,
        });

        rig.Host.Texts().ShouldContain("The mount was 8.71 deg off");
        rig.Host.Texts().ShouldContain("Saturn is 8.70 deg away");
        rig.Press("FindUseFocalLength");
        rig.Press("FindGo");
        rig.Posted.ShouldBe([new UseMeasuredFocalLengthSignal(0, 1780), new FindPlanetGoSignal(CatalogIndex.Saturn, SyncOnly: false)]);
    }

    [Fact]
    public void APlanetAlreadyInTheFieldIsOnlySyncedNeverSlewedTo()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);
        Found(rig, new PlanetaryFindDto
        {
            Summary = "", RaJ2000 = 22.6, DecJ2000 = -10.9, ScaleArcsec = 0.2316, Planet = CatalogIndex.Saturn,
            PlanetSeparationDeg = 0.01, PlanetInField = true,
        });

        rig.Host.Texts().ShouldContain("Saturn is in the field");
        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "FindUseFocalLength" or "FindGo" })
            .ShouldBeFalse("no focal length to offer, and no slew to a planet already there");
        rig.Press("FindSync");
        rig.Posted.ShouldBe([new FindPlanetGoSignal(CatalogIndex.Saturn, SyncOnly: true)]);
    }

    // The node's capture with its mount connected, and what its last find learned (null for none).
    private static void Found(Rigged rig, PlanetaryFindDto? find)
    {
        rig.Capture.UpdateFromAnswer(new PlanetaryStateDto
        {
            Camera = "G3M678M", Running = true, RoiWidth = 640, RoiHeight = 360, MountConnected = true, Find = find,
        });
        rig.Paint();
    }

    [Fact]
    public void ACaptureWithNoMountOffersNoFind()
    {
        using var rig = new Rigged(1f, capturing: true, Saturn1780, height: 2400);

        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "FindPlanet" })
            .ShouldBeFalse("a find's sync and goto need the mount");
        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "PlanetSaturn" })
            .ShouldBeTrue("the wheel's sequence still names its planet");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void EveryRowsLabelHasTheColumnItsWidestLabelNeedsSoNoneIsCut(float scale)
    {
        // A fixed 52 design units cut "Deadband" to "Dead..." and the Nudge row's 40 cut "Nudge" to "Nu..." (2026-10-20).
        using var rig = new Rigged(scale, capturing: true, Saturn1780, height: 2400);
        var column = rig.Host.WidthsOf(PlanetaryControlPanel.LabelWidthSample).ShouldHaveSingleItem();

        foreach (var label in new[] { "Exp", "Gain", "Depth", "Size", "Pan", "Deadband", "Move", "Sets" })
        {
            var widths = rig.Host.WidthsOf(label);
            widths.ShouldNotBeEmpty($"the panel labels a row {label}");
            rig.Host.PaintedAs(label).ShouldAllBe(painted => painted == label, $"{label} is painted whole");
            if (label != "Sets")
            {
                // Sets shares its row with Add step, so its label takes only its own width.
                widths.ShouldAllBe(width => Math.Abs(width - column) < 0.5f, $"{label}'s column lines up with the widest");
            }
        }
    }

    [Fact]
    public void ARecordingsEndIsSaidAsWhatItCameTo()
    {
        // The status line kept the start's "Recording 60 s to ..." after the file was done (2026-10-20).
        var done = new PlanetaryRecordingDto
        {
            Path = "C:/Pictures/Luminance_2026-10-20T02_07_35_OTA1.ser", Written = true, FramesWritten = 10537, FramesDropped = 0,
            EndReason = "its duration is over",
        };
        PlanetaryCaptureController.RecordingEndedInWords(done)
            .ShouldBe("Recorded 10537 frames to Luminance_2026-10-20T02_07_35_OTA1.ser, 0 dropped");

        var failed = new PlanetaryRecordingDto { Path = "C:/Pictures/a.ser", FailureReason = "the disk is full" };
        PlanetaryCaptureController.RecordingEndedInWords(failed).ShouldBe("Recording to a.ser failed: the disk is full");
    }

    [Fact]
    public void ACameraWithNoWheelHasNoFilterSection()
    {
        using var rig = new Rigged(1f, capturing: false);

        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit hit && hit.Action.StartsWith("Filter", StringComparison.Ordinal))
            .ShouldBeFalse("a colour camera's panel does not grow a section it cannot use");
    }

    [Fact]
    public void ACameraWithNoHighSpeedReadoutStartsWithTheCamerasOwn()
    {
        using var rig = new Rigged(1f, capturing: false, Rig with { CanFastReadout = false, VideoBitDepths = [] });

        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "HighSpeed" or "Depth8" or "Depth16" })
            .ShouldBeFalse("a camera with no choice is offered none");
        rig.Press("PlanetaryCaptureToggle");

        rig.Posted.ShouldBe([new StartVideoCaptureSignal(ExposureMs: 10, Gain: 100, RoiWidth: 640, RoiHeight: 360)]);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WhileCapturingStopPostsStop(float scale)
    {
        using var rig = new Rigged(scale, capturing: true);

        rig.Press("PlanetaryCaptureToggle");

        rig.Posted.ShouldBe([new StopVideoCaptureSignal()]);
    }

    [Theory]
    [InlineData("ExposureInc", 20.0, null, null)]
    [InlineData("ExposureDec", 5.0, null, null)]
    [InlineData("GainInc", null, (short)110, null)]
    [InlineData("GainDec", null, (short)90, null)]
    [InlineData("Depth8", null, null, BitDepth.Int8)]
    public void WhileCapturingAStepperIsStagedOnTheCapture(string action, double? exposureMs, short? gain, BitDepth? depth)
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press(action);

        var staged = rig.Capture.TakeChanges().ShouldNotBeNull();
        (staged.ExposureMs, staged.Gain, staged.BitDepth).ShouldBe((exposureMs, gain, depth));
        rig.Posted.ShouldBeEmpty("a live setting goes to the capture, not through a signal");
    }

    /// <summary>The camera's own depth is shown chosen, so its segment takes a press only once another depth is.</summary>
    [Fact]
    public void WhileCapturingTheDeeperDepthIsStagedOnceAnotherIsChosen()
    {
        using var rig = new Rigged(1f, capturing: true);
        rig.Press("Depth8");
        rig.Capture.TakeChanges();

        rig.Press("Depth16");

        rig.Capture.TakeChanges().ShouldNotBeNull().BitDepth.ShouldBe(BitDepth.Int16);
    }

    [Fact]
    public void WhileCapturingTheSmallerRoiSizeIsStagedOnTheCaptureAndSizesTheSelection()
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press("RoiSizeDec");      // 640x360 to 512x512

        var resized = rig.Capture.TakeChanges().ShouldNotBeNull();
        (resized.RoiWidth, resized.RoiHeight).ShouldBe((512, 512));
        (rig.Panel.Roi.Width, rig.Panel.Roi.Height).ShouldBe((512, 512));
    }

    [Fact]
    public void WhileCapturingHighSpeedIsSwitchedOnTheCapture()
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press("HighSpeed");

        rig.Capture.TakeChanges().ShouldNotBeNull().HighSpeed.ShouldBe(false);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WhileCapturingTheRoisSizeAndPanAreStagedOnTheCaptureAndMoveTheSelection(float scale)
    {
        using var rig = new Rigged(scale, capturing: true);
        var before = rig.Panel.Roi;

        rig.Press("RoiSizeInc");
        var resized = rig.Capture.TakeChanges().ShouldNotBeNull();
        (resized.RoiWidth, resized.RoiHeight).ShouldBe((640, 480));

        rig.Press("RoiPanRight");
        rig.Press("RoiPanDown");
        rig.Press("RoiPanLeft");
        rig.Press("RoiPanUp");
        rig.Press("RoiPanUp");
        // A twentieth of the sensor a step: 1936 / 20 across, 1096 / 20 down; the steps add up between two sends.
        var jogged = rig.Capture.TakeChanges().ShouldNotBeNull();
        (jogged.JogX, jogged.JogY).ShouldBe(((int?)null, (int?)-54));

        rig.Panel.Roi.ShouldBe(RoiConstraints.ForSensor(1936, 1096).Snap(new RoiRect(before.X, before.Y - 54, 640, 480)),
            "the selection moves with the window");
    }

    [Fact]
    public void WhileIdleThePanMovesOnlyTheSelection()
    {
        using var rig = new Rigged(1f, capturing: false);
        var before = rig.Panel.Roi;

        rig.Press("RoiPanRight");

        rig.Panel.Roi.ShouldBe(before with { X = before.X + 96 });
        rig.Capture.TakeChanges().ShouldBeNull();
    }

    [Fact]
    public void TheRecenterIsHandedToTheCaptureAsThePanelShowsItAndOnlyOnceChanged()
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press("RecenterBoth");
        rig.Press("RecenterDeadbandInc");   // 4 px to 8 px
        rig.Press("RecenterGainDec");       // 50 % to 25 %

        var recenter = rig.Capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull();
        (recenter.Auto, recenter.Roi, recenter.MountJog, recenter.DeadbandPixels, recenter.Gain).ShouldBe((true, true, true, 8, 0.25));

        rig.Paint();
        rig.Capture.TakeChanges().ShouldBeNull("a frame that changed nothing stages nothing");
        rig.Capture.RecenterForStart.ShouldBe(recenter, "and the next Start carries it");
    }

    [Theory]
    [InlineData("RecenterOff", false, true, false)]
    [InlineData("RecenterRoi", true, true, false)]
    [InlineData("RecenterMount", true, false, true)]
    [InlineData("RecenterBoth", true, true, true)]
    public void EachRecenterModeIsWhatTheCaptureIsTold(string mode, bool auto, bool roi, bool mount)
    {
        using var rig = new Rigged(1f, capturing: true);
        rig.Press(mode == "RecenterOff" ? "RecenterRoi" : "RecenterOff");
        rig.Capture.TakeChanges();

        rig.Press(mode);

        var recenter = rig.Capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull();
        (recenter.Auto, recenter.Roi, recenter.MountJog).ShouldBe((auto, roi, mount));
    }

    [Fact]
    public void TheRecentersDeadbandNarrowsAndItsGainRisesOnTheCapture()
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press("RecenterDeadbandDec");   // 4 px to 2 px
        rig.Press("RecenterGainInc");       // 50 % to 75 %

        var recenter = rig.Capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull();
        (recenter.Auto, recenter.MountJog, recenter.DeadbandPixels, recenter.Gain).ShouldBe((false, false, 2, 0.75));
    }

    [Theory]
    [InlineData("FocCoarseIn", -100)]
    [InlineData("FocFineIn", -10)]
    [InlineData("FocFineOut", 10)]
    [InlineData("FocCoarseOut", 100)]
    public void AFocuserJogPostsItsSteps(string action, int steps)
    {
        using var rig = new Rigged(1f, capturing: true);

        rig.Press(action);

        rig.Posted.ShouldBe([new JogFocuserSignal(0, steps)]);
    }

    [Theory]
    [InlineData("MountMoveN", GuideDirection.North)]
    [InlineData("MountMoveS", GuideDirection.South)]
    [InlineData("MountMoveE", GuideDirection.East)]
    [InlineData("MountMoveW", GuideDirection.West)]
    public void AMountMoveButtonStartsOnPressAndStopsOnRelease(string action, GuideDirection direction)
    {
        using var rig = new Rigged(1f, capturing: false);

        rig.Press(action);   // a press and its release, through the router

        rig.Posted.ShouldBe([new StartMountMoveSignal(direction, 1), new StopMountMoveSignal()], "1x, the handset's speed 1, until chosen");
    }

    [Fact]
    public void TheMoveIsAtTheSpeedChosen()
    {
        using var rig = new Rigged(1f, capturing: false);

        rig.Press("MoveSpeed2");   // 2x
        rig.Press("MountMoveW");

        rig.Posted.ShouldBe([new StartMountMoveSignal(GuideDirection.West, 2), new StopMountMoveSignal()]);
    }

    /// <summary>A moving focuser is marked by drawn carets, never a symbol character in the text run, which Night cannot tint.</summary>
    [Fact]
    public void AMovingFocuserIsMarkedByDrawnIconsNotACharacter()
    {
        using var rig = new Rigged(1f, capturing: false, Rig with { FocuserIsMoving = true });
        var layout = rig.Host.GetCapturedLayout();

        layout.Any(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.Contains('⇄'))
            .ShouldBeFalse();
        layout.Any(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Pos: 12345   8.5°C" } })
            .ShouldBeTrue("the position keeps its words");
        var marks = layout.Where(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Icon })
            .Select(static n => ((Layout.Content.Icon)((Layout.Node.Leaf)n.Node).Content).Kind).ToArray();
        marks.ShouldContain(Layout.IconKind.CaretLeft);
        marks.ShouldContain(Layout.IconKind.CaretRight);
    }

    [Fact]
    public void WithNoFocuserThePanelSaysSoAndOffersNoJog()
    {
        using var rig = new Rigged(1f, capturing: false, Rig with { FocuserConnected = false });

        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "FocCoarseIn" }).ShouldBeFalse();
        rig.Host.GetCapturedLayout().Any(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "No focuser connected" } })
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void RecordAsksForTheDurationThroughTheRecordControl(float scale)
    {
        using var rig = new Rigged(scale, capturing: true);
        rig.Panel.Record.DurationInput.Text = "90";
        rig.Paint();

        rig.Press("PlanetaryRecordToggle");

        rig.Posted.ShouldBe([new StartPlanetaryRecordingSignal(TimeSpan.FromSeconds(90))]);
    }

    /// <summary>
    /// "Show ROI on image" outlined a stream that IS the window, so the whole image (#1033): a running capture offers its full
    /// sensor instead, and an idle panel offers neither.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void ARunningCaptureOffersItsFullFrameToReframeOnAndAnIdleOneDoesNot(float scale)
    {
        using (var idle = new Rigged(scale, capturing: false))
        {
            idle.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "TakeFullFrame" or "RoiOverlayToggle" })
                .ShouldBeFalse("an idle panel has no stream to take a full frame of");
        }
        using var rig = new Rigged(scale, capturing: true);

        rig.Press("TakeFullFrame");

        rig.Posted.ShouldBe([new TakeFullFrameSignal()]);
    }

    [Fact]
    public void AReframesConfirmCardMovesTheWindowOrLetsItGoAndItsSizeIsTheProposals()
    {
        using var rig = new Rigged(1f, capturing: true);
        var reframe = new PlanetaryReframe(1936, 1096, new RoiRect(648, 368, 640, 360), RoiConstraints.ForSensor(1936, 1096));
        rig.Reframe = reframe;
        rig.Paint();
        rig.Host.GetRegisteredRegions().Any(static r => r.Result is HitResult.ButtonHit { Action: "TakeFullFrame" or "RoiPanLeft" })
            .ShouldBeFalse("the card stands in for the pan and the full frame while it is on show");

        rig.Press("RoiSizeInc");                 // 640x360 to 640x480, the proposal's alone
        rig.Capture.TakeChanges().ShouldBeNull("a proposal is nothing the capture takes until it is committed");
        (reframe.Proposal.Width, reframe.Proposal.Height).ShouldBe((640, 480));
        rig.Press("ReframeCommit");
        rig.Press("ReframeCancel");

        rig.Posted.ShouldBe([new CommitReframeSignal(), new CancelReframeSignal()]);
    }

    /// <summary>
    /// The picture-in-picture is setup only, so it folds while a capture runs (#1612): its drag is idle-only, and it shows the
    /// selection, which the recenter's jogs of the running window do not move.
    /// </summary>
    [Fact]
    public void ThePictureInPictureIsOfferedWhileIdleAndFoldsWhileCapturing()
    {
        static bool HasPip(Host host) => host.GetCapturedLayout()
            .Any(static n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: PlanetaryControlPanel.PipKey } });

        using (var idle = new Rigged(1f, capturing: false))
        {
            HasPip(idle.Host).ShouldBeTrue();
        }
        using var capturing = new Rigged(1f, capturing: true);
        HasPip(capturing.Host).ShouldBeFalse();
    }

    /// <summary>
    /// The scrolled panel registers a region of its own over every row, which swallows a press no row claims, so the PiP's
    /// drag is a press DECLARED on the PiP: the router hands the view the press, every move and the release.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void APressOnThePictureInPictureReachesTheViewAndOwnsTheDragThatFollows(float scale)
    {
        using var renderer = new RgbaImageRenderer((uint)(PlanetaryControlPanel.BaseWidth * scale), (uint)(1100 * scale));
        var host = new Host(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
        var panel = new PlanetaryControlPanel();
        var seen = new List<string>();
        DragCapture? Press(PointerPress press)
        {
            seen.Add("press");
            return new DragCapture(_ => seen.Add("move"), _ => seen.Add("release"));
        }
        host.Paint(panel, new NodePlanetaryCapture(), Rig, bus: null, new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * scale, 935 * scale),
            pipPress: Press);

        var pip = host.GetRegisteredRegions().Single(static r => r.Result is HitResult.ButtonHit { Action: PlanetaryControlPanel.PipKey });
        var (x, y) = (pip.X + pip.Width / 2f, pip.Y + pip.Height / 2f);
        var router = new InputRouter(host.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [host] };
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseMove(x + 30f, y + 400f, MouseButton.Left));
        router.Handle(new InputEvent.MouseUp(x + 30f, y + 400f));

        seen.ShouldBe(["press", "move", "release"], "the drag stays the PiP's wherever the pointer goes");
    }

    /// <summary>A panel that fits takes no wheel, which goes on to the view behind it as it did before the panel could scroll.</summary>
    [Fact]
    public void APanelThatFitsTakesNoWheel()
    {
        using var rig = new Rigged(1f, capturing: false);
        var router = new InputRouter(rig.Host.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [rig.Host] };

        router.Handle(new InputEvent.Scroll(-3f, 100f, 400f)).ShouldBeFalse();
    }

    [Fact]
    public void ThePictureInPicturesDragCentresTheSelectionOnTheSensorKeepingItOnIt()
    {
        var panel = new PlanetaryControlPanel();
        panel.FollowCamera(Rig);

        panel.CentreRoiOn(1000f, 500f);
        panel.Roi.ShouldBe(new RoiRect(680, 320, 640, 360));

        panel.CentreRoiOn(1900f, 1090f);
        panel.Roi.ShouldBe(new RoiRect(1936 - 640, 1096 - 360, 640, 360), "snapped back onto the sensor");
    }
}

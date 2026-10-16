using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.DAL;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
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
            RectF32 rect, TimeSpan timeLeft = default)
        {
            BeginFrame();
            panel.FollowCamera(telemetry);
            RenderLayout(panel.Build(capture, telemetry, timeLeft, bus), rect);
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

        public Rigged(float scale, bool capturing, PreviewOTATelemetry? telemetry = null)
        {
            _scale = scale;
            Telemetry = telemetry ?? Rig;
            _renderer = new RgbaImageRenderer((uint)(PlanetaryControlPanel.BaseWidth * scale), (uint)(1100 * scale));
            Host = new Host(_renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
            _bus.Subscribe<StartVideoCaptureSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopVideoCaptureSignal>(s => Posted.Add(s));
            _bus.Subscribe<JogFocuserSignal>(s => Posted.Add(s));
            _bus.Subscribe<JogMountSignal>(s => Posted.Add(s));
            _bus.Subscribe<StartPlanetaryRecordingSignal>(s => Posted.Add(s));
            _bus.Subscribe<StopPlanetaryRecordingSignal>(s => Posted.Add(s));
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

        public Host Host { get; }

        public List<object> Posted { get; } = [];

        /// <summary>A frame of the panel, in its design width and a height it never needs.</summary>
        public void Paint() => Host.Paint(Panel, Capture, Telemetry, _bus,
            new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * _scale, 1100 * _scale));

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

        rig.Press("RecenterAuto");
        rig.Press("RecenterMount");
        rig.Press("RecenterDeadbandInc");   // 4 px to 8 px
        rig.Press("RecenterGainDec");       // 50 % to 25 %

        var recenter = rig.Capture.TakeChanges().ShouldNotBeNull().Recenter.ShouldNotBeNull();
        (recenter.Auto, recenter.MountJog, recenter.DeadbandPixels, recenter.Gain).ShouldBe((true, true, 8, 0.25));

        rig.Paint();
        rig.Capture.TakeChanges().ShouldBeNull("a frame that changed nothing stages nothing");
        rig.Capture.RecenterForStart.ShouldBe(recenter, "and the next Start carries it");
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
    [InlineData("MountNudgeN", GuideDirection.North)]
    [InlineData("MountNudgeS", GuideDirection.South)]
    [InlineData("MountNudgeE", GuideDirection.East)]
    [InlineData("MountNudgeW", GuideDirection.West)]
    public void AMountNudgePostsItsDirection(string action, GuideDirection direction)
    {
        using var rig = new Rigged(1f, capturing: false);

        rig.Press(action);

        rig.Posted.ShouldBe([new JogMountSignal(direction, 30.0)]);
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

    [Fact]
    public void ShowRoiOnImageTogglesTheOverlay()
    {
        using var rig = new Rigged(1f, capturing: true);
        rig.Panel.ShowRoiOnImage.ShouldBeTrue("the ROI is outlined on the stream until the user says otherwise");

        rig.Press("RoiOverlayToggle");

        rig.Panel.ShowRoiOnImage.ShouldBeFalse();
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

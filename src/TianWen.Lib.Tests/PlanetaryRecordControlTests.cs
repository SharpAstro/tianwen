using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary panel's Record control (#1344) as the GUI paints it: what a press on it posts, when it offers no press,
/// and what it says of the node's recording. The signals' round trip to the node is <c>PlanetaryRecordingThroughTheNodeTests</c>'.
/// </summary>
public class PlanetaryRecordControlTests
{
    private static readonly DateTimeOffset Started = new DateTimeOffset(2026, 10, 16, 21, 30, 0, TimeSpan.Zero);

    /// <summary>The smallest panel: the control's rows in a column, painted as <c>VkPlanetaryTab</c> paints its panel.</summary>
    private sealed class Panel(RgbaImageRenderer renderer) : PixelWidgetBase<RgbaImage>(renderer)
    {
        public void Paint(PlanetaryRecordControl control, PlanetaryRecordingDto? recording, bool capturing, TimeSpan timeLeft, SignalBus bus)
        {
            BeginFrame();
            RenderLayout(Layout.Builder.VStack([.. control.Rows(recording, capturing, timeLeft, bus, 14f, 26f, 3f)]).WithGap(5f),
                new RectF32(0f, 0f, Renderer.Width, Renderer.Height));
        }
    }

    // The panel's design size, 280 wide as VkPlanetaryTab's, in device pixels at the scale painted.
    private static RgbaImageRenderer Surface(float scale) => new RgbaImageRenderer((uint)(280 * scale), (uint)(200 * scale));

    private static (Panel Panel, List<object> Posted, SignalBus Bus) Paint(RgbaImageRenderer renderer, float scale, PlanetaryRecordControl control,
        PlanetaryRecordingDto? recording, bool capturing, TimeSpan timeLeft = default)
    {
        var bus = new SignalBus();
        var posted = new List<object>();
        bus.Subscribe<StartPlanetaryRecordingSignal>(s => posted.Add(s));
        bus.Subscribe<StopPlanetaryRecordingSignal>(s => posted.Add(s));
        var panel = new Panel(renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
        panel.Paint(control, recording, capturing, timeLeft, bus);
        return (panel, posted, bus);
    }

    private static ClickableRegion? Toggle(Panel panel)
        => panel.GetRegisteredRegions().Where(static r => r.Result is HitResult.ButtonHit { Action: "PlanetaryRecordToggle" })
            .Select(static r => (ClickableRegion?)r).SingleOrDefault();

    /// <summary>Every text run the control's rows declare, in order.</summary>
    private static string[] Texts(PlanetaryRecordControl control, PlanetaryRecordingDto? recording, bool capturing, TimeSpan timeLeft = default)
    {
        var texts = new List<string>();
        foreach (var row in control.Rows(recording, capturing, timeLeft, bus: null, 14f, 26f, 3f))
        {
            Walk(row, texts);
        }
        return [.. texts];

        static void Walk(Layout.Node node, List<string> into)
        {
            switch (node)
            {
                case Layout.Node.Leaf { Content: Layout.Content.Text text }:
                    into.Add(text.Value);
                    break;
                case Layout.Node.Stack stack:
                    foreach (var child in stack.Children)
                    {
                        Walk(child, into);
                    }
                    break;
            }
        }
    }

    /// <summary>Presses the control's button the way a click arrives, through the router, where it was painted at <paramref name="scale"/>.</summary>
    private static void Press(Panel panel, SignalBus bus, float scale)
    {
        var region = Toggle(panel).ShouldNotBeNull("the button offers a press");
        region.Height.ShouldBe(26f * scale, 0.5f, "the button fills its row, the row's design height at the scale painted");
        var (x, y) = (region.X + region.Width / 2f, region.Y + region.Height / 2f);
        var router = new InputRouter(panel.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [panel] };
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        bus.ProcessPending();
    }

    private static PlanetaryRecordingDto Recording(bool recording, int written = 1234, int dropped = 0, string? endReason = null,
        bool whole = false, string? failure = null) => new PlanetaryRecordingDto
    {
        Path = "C:/Images/Planetary/2026-10-16/Jupiter_Red_2026-10-16T213000Z.ser",
        StartedUtc = Started,
        EndsUtc = Started.AddSeconds(60),
        Recording = recording,
        Written = whole,
        FramesWritten = written,
        FramesDropped = dropped,
        EndReason = endReason,
        FailureReason = failure,
    };

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void RecordAsksForTheDurationTheFieldStates(float scale)
    {
        using var renderer = Surface(scale);
        var control = new PlanetaryRecordControl();
        control.Duration.ShouldBe(TimeSpan.FromSeconds(60), "a minute until the user types another");
        control.DurationInput.Text = "90";

        var (panel, posted, bus) = Paint(renderer, scale, control, recording: null, capturing: true);
        Press(panel, bus, scale);

        posted.ShouldBe([new StartPlanetaryRecordingSignal(TimeSpan.FromSeconds(90))]);
        panel.GetRegisteredRegions().ShouldContain(static r => r.Result is HitResult.TextInputHit, "the duration is a field the user types in");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WithNoCaptureGoingOnRecordOffersNoPressAndSaysWhy(float scale)
    {
        using var renderer = Surface(scale);
        var (panel, _, _) = Paint(renderer, scale, new PlanetaryRecordControl(), recording: null, capturing: false);

        Toggle(panel).ShouldBeNull("a recording is of the capture going on");
        Texts(new PlanetaryRecordControl(), recording: null, capturing: false).ShouldContain("Start the capture to record it");
    }

    /// <summary>
    /// Record and its Stop go to the capture's own node, so with no capture on show neither is offered, a recording the last
    /// state read still calls going on included (review round 1 of #1610, Q1).
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WithNoCaptureOnShowNeitherRecordNorStopIsOffered(float scale)
    {
        using var renderer = Surface(scale);
        var (panel, _, _) = Paint(renderer, scale, new PlanetaryRecordControl(), Recording(recording: true), capturing: false);

        Toggle(panel).ShouldBeNull("there is no capture's node to send a stop to");
    }

    [Theory]
    [InlineData("", 1f)]
    [InlineData("abc", 1f)]
    [InlineData("0", 1.5f)]
    [InlineData("-5", 1f)]
    [InlineData("86401", 1.5f)]
    public void AFieldThatStatesNoDurationOffersNoRecordAndSaysWhatItTakes(string text, float scale)
    {
        using var renderer = Surface(scale);
        var control = new PlanetaryRecordControl();
        control.DurationInput.Text = text;

        var (panel, _, _) = Paint(renderer, scale, control, recording: null, capturing: true);

        control.Duration.ShouldBeNull();
        Toggle(panel).ShouldBeNull();
        Texts(control, recording: null, capturing: true).ShouldContain("Seconds, above 0 and up to a day");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WhileItRecordsTheButtonStopsItAndThePanelShowsTheFileTheFramesAndTheTimeLeft(float scale)
    {
        using var renderer = Surface(scale);
        var recording = Recording(recording: true, written: 1234, dropped: 2);

        var (panel, posted, bus) = Paint(renderer, scale, new PlanetaryRecordControl(), recording, capturing: true, TimeSpan.FromSeconds(41.2));
        Press(panel, bus, scale);

        posted.ShouldBe([new StopPlanetaryRecordingSignal()]);
        string.Join(" | ", PlanetaryRecordControl.Describe(recording, TimeSpan.FromSeconds(41.2)))
            .ShouldBe("Jupiter_Red_2026-10-16T213000Z.ser | 1234 written   2 dropped   0:42 left");
        Texts(new PlanetaryRecordControl(), recording, capturing: true, TimeSpan.FromSeconds(41.2))
            .ShouldBe(["Secs", "Stop", "Jupiter_Red_2026-10-16T213000Z.ser", "1234 written   2 dropped   0:42 left"]);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void AStopIsOfferedWhileItRecordsEvenWithTheFieldEmpty(float scale)
    {
        using var renderer = Surface(scale);
        var control = new PlanetaryRecordControl();
        control.DurationInput.Text = "";

        var (panel, posted, bus) = Paint(renderer, scale, control, Recording(recording: true), capturing: true);
        Press(panel, bus, scale);

        posted.ShouldBe([new StopPlanetaryRecordingSignal()]);
    }

    [Fact]
    public void OnceItHasEndedThePanelSaysWhyOrWhyItFailed()
    {
        string.Join(" | ", PlanetaryRecordControl.Describe(Recording(false, 11760, 0, "its duration is over", whole: true), TimeSpan.Zero))
            .ShouldBe("Jupiter_Red_2026-10-16T213000Z.ser | Ended because its duration is over | 11760 written   0 dropped");
        PlanetaryRecordControl.Describe(Recording(false, 500, 0, "it was stopped", whole: false), TimeSpan.Zero)[1]
            .ShouldBe("Ended because it was stopped, finishing the file");
        PlanetaryRecordControl.Describe(Recording(false, 80, 0, "it could not be written", whole: true, failure: "The disk is full"), TimeSpan.Zero)[1]
            .ShouldBe("Failed: The disk is full");

        // An hour or more reads with its hours.
        PlanetaryRecordControl.Describe(Recording(true), TimeSpan.FromMinutes(75))[1].ShouldEndWith("1:15:00 left");
    }
}

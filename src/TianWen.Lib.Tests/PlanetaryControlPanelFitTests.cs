using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary capture panel fits the left half of a 1080-line screen (#1612): about 935 design units are left under the
/// GUI's status bar and the Live Session top strip, at 100 %. Arranged in its tallest states at scales 1 and 1.5, every control
/// and every row lies inside that height whole: while a capture runs without scrolling, and while idle once the wheel has
/// brought it there. A section added to the panel passes this too.
/// </summary>
public class PlanetaryControlPanelFitTests
{
    /// <summary>What the left half of a 1080-line screen leaves the panel, in design units (#1612).</summary>
    private const float LeftHalfHeight = 935f;

    /// <summary>Every control's row is this tall, so a region the viewport cut short is shorter.</summary>
    private const float RowHeight = 26f;

    // Every control the panel offers in its tallest states, by the hit it registers.
    private static readonly string[] Controls =
    [
        "PlanetaryCaptureToggle", "ExposureDec", "ExposureInc", "GainDec", "GainInc", "Depth8", "Depth16", "HighSpeed",
        "RoiSizeDec", "RoiSizeInc", "RoiPanLeft", "RoiPanRight", "RoiPanUp", "RoiPanDown", "RoiOverlayToggle",
        "RecenterAuto", "RecenterDeadbandDec", "RecenterDeadbandInc", "RecenterGainDec", "RecenterGainInc", "RecenterMount",
        "MountNudgeN", "MountNudgeS", "MountNudgeE", "MountNudgeW",
        "FocCoarseIn", "FocFineIn", "FocFineOut", "FocCoarseOut",
    ];

    private static readonly PlanetaryRecordingDto Ended = new PlanetaryRecordingDto
    {
        Path = "C:/Images/Planetary/2026-10-16/Jupiter_Red_2026-10-16T213000Z.ser",
        StartedUtc = new DateTimeOffset(2026, 10, 16, 21, 30, 0, TimeSpan.Zero),
        EndsUtc = new DateTimeOffset(2026, 10, 16, 21, 31, 0, TimeSpan.Zero),
        Recording = false,
        Written = true,
        FramesWritten = 11760,
        EndReason = "its duration is over",
    };

    /// <summary>A panel painted at <paramref name="scale"/> on a surface taller than any state of it.</summary>
    private sealed class Painted : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;

        public Painted(float scale)
        {
            Scale = scale;
            _renderer = new RgbaImageRenderer((uint)(PlanetaryControlPanel.BaseWidth * scale), (uint)(1200 * scale));
            Host = new PlanetaryControlPanelTests.Host(_renderer) { DpiScale = scale, FontPath = FontResolver.ResolveSystemFont() };
        }

        public float Scale { get; }

        public PlanetaryControlPanelTests.Host Host { get; }

        public PlanetaryControlPanel Panel { get; } = new PlanetaryControlPanel();

        public NodePlanetaryCapture Capture { get; } = new NodePlanetaryCapture();

        /// <summary>The left half of a 1080-line screen, in device pixels at the scale painted.</summary>
        public RectF32 LeftHalf => new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * Scale, LeftHalfHeight * Scale);

        public void Paint(RectF32 rect) => Host.Paint(Panel, Capture, PlanetaryControlPanelTests.Rig, bus: null, rect);

        /// <summary>
        /// The tallest state: the depth and high-speed rows, the readout, auto-recenter's offset row, a focuser connected, and
        /// an ended recording with the duration field's hint. With <paramref name="capturing"/> false, the capture has stopped
        /// since, and its Record is greyed with the line saying why.
        /// </summary>
        public void Tallest(bool capturing)
        {
            Panel.Record.DurationInput.Text = "";
            Capture.Began();
            Capture.UpdateFromAnswer(new PlanetaryStateDto
            {
                Camera = "ZWO ASI462MC",
                Running = true,
                RoiWidth = 640,
                RoiHeight = 360,
                FramesReceived = 48211,
                DroppedFrames = 3,
                FramesPerSecond = 91.6,
                BitDepth = BitDepth.Int16,
                OffsetX = 1.5,
                OffsetY = -0.5,
                RecenterActuator = RecenterActuator.Roi,
                Recording = Ended,
            });
            // Auto-recenter on as the user turns it on, a press where it is painted: its offset row shows while the capture runs.
            Paint(new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * Scale, 1200 * Scale));
            Host.Press("RecenterAuto");
            if (!capturing)
            {
                Capture.Ended();
            }
        }

        /// <summary>
        /// Where the panel's last row ends, in design units from its top, arranged with all the room it wants: the height the
        /// left half has to hold, less the panel's bottom padding. Written to the test's output, so a section added later
        /// can see how much room is left.
        /// </summary>
        public float LastRowEnds()
        {
            Paint(new RectF32(0f, 0f, PlanetaryControlPanel.BaseWidth * Scale, 1200 * Scale));
            var ends = Host.GetCapturedLayout().Where(static n => n.Node is Layout.Node.Leaf).Max(static n => n.Bounds.Y + n.Bounds.Height) / Scale;
            TestContext.Current.TestOutputHelper?.WriteLine($"At scale {Scale}, the last row ends {ends:0.#} design units down, of {LeftHalfHeight}.");
            return ends;
        }

        /// <summary>Every text the panel's tree says, from the tree itself, so a row the paint dropped is still counted.</summary>
        public HashSet<string> TextsOfTheTree()
        {
            var texts = new HashSet<string>();
            Walk(Panel.Build(Capture, PlanetaryControlPanelTests.Rig, TimeSpan.Zero, bus: null), texts);
            return texts;

            static void Walk(Layout.Node node, HashSet<string> into)
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

        /// <summary>The controls and the texts painted whole inside <paramref name="rect"/> on the last frame.</summary>
        public (HashSet<string> Controls, HashSet<string> Texts, bool Field) WholeInside(RectF32 rect)
        {
            var controls = new HashSet<string>();
            var field = false;
            foreach (var region in Host.GetRegisteredRegions())
            {
                if (region.Height < RowHeight * Scale - 0.5f || !Inside(rect, region.X, region.Y, region.Width, region.Height))
                {
                    continue;
                }
                if (region.Result is HitResult.ButtonHit { Action: var action })
                {
                    controls.Add(action);
                }
                field |= region.Result is HitResult.TextInputHit;
            }

            var texts = new HashSet<string>();
            foreach (var arranged in Host.GetCapturedLayout())
            {
                if (arranged.Node is Layout.Node.Leaf { Content: Layout.Content.Text text }
                    && Inside(rect, arranged.Bounds.X, arranged.Bounds.Y, arranged.Bounds.Width, arranged.Bounds.Height))
                {
                    texts.Add(text.Value);
                }
            }
            return (controls, texts, field);
        }

        private static bool Inside(RectF32 rect, float x, float y, float w, float h)
            => x >= rect.X - 0.5f && x + w <= rect.X + rect.Width + 0.5f && y >= rect.Y - 0.5f && y + h <= rect.Y + rect.Height + 0.5f;

        public void Dispose() => _renderer.Dispose();
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WhileCapturingEveryControlAndRowFitsTheLeftHalfOfA1080LineScreenWithoutScrolling(float scale)
    {
        using var painted = new Painted(scale);
        painted.Tallest(capturing: true);
        var texts = painted.TextsOfTheTree();
        texts.Any(static t => t.StartsWith("off ", StringComparison.Ordinal)).ShouldBeTrue("auto-recenter's offset row is in the state");
        texts.ShouldContain("Seconds, above 0 and up to a day");
        texts.ShouldContain("Ended because its duration is over");
        texts.ShouldContain("fine 10  /  coarse 100");
        painted.LastRowEnds();

        painted.Paint(painted.LeftHalf);
        var inside = painted.WholeInside(painted.LeftHalf);

        Controls.Except(inside.Controls).ToArray().ShouldBeEmpty($"every live control lies inside {LeftHalfHeight} design units while capturing");
        inside.Field.ShouldBeTrue("so does the recording's duration");
        texts.Except(inside.Texts).ToArray().ShouldBeEmpty("and every row, the focuser's hint the last of them");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void WhileIdleEveryControlAndRowIsInsideTheLeftHalfOrBroughtThereByTheWheel(float scale)
    {
        using var painted = new Painted(scale);
        painted.Tallest(capturing: false);
        var texts = painted.TextsOfTheTree();
        texts.ShouldContain("Start the capture to record again", "the greyed Record says why");
        painted.LastRowEnds();

        var rect = painted.LeftHalf;
        painted.Paint(rect);
        var (controls, shown, field) = painted.WholeInside(rect);

        // The wheel at the panel's foot, as far as it goes, through the router as a wheel arrives.
        var router = new InputRouter(painted.Host.Ui, new BackgroundTaskTracker(), static () => { }) { Widgets = () => [painted.Host] };
        router.Handle(new InputEvent.Scroll(-1000f, rect.Width / 2f, rect.Height - 4f));
        painted.Paint(rect);
        var scrolled = painted.WholeInside(rect);
        controls.UnionWith(scrolled.Controls);
        shown.UnionWith(scrolled.Texts);

        Controls.Except(controls).ToArray().ShouldBeEmpty($"every control is inside {LeftHalfHeight} design units, or the wheel brings it there");
        (field || scrolled.Field).ShouldBeTrue();
        texts.Except(shown).ToArray().ShouldBeEmpty("and every row");
    }
}

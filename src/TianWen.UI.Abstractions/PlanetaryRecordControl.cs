using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using DIR.Lib;
using TianWen.Hosting.Dto;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The planetary panel's Record control (#1344, step 2 of Phase G in docs/plans/live-planetary-capture.md): a duration, a
/// Record and a Stop, and the node's recording as the capture's state reports it (the file, the frames written and dropped,
/// the time left; once it has ended, why, or why it failed). It asks the node through
/// <see cref="StartPlanetaryRecordingSignal"/> and <see cref="StopPlanetaryRecordingSignal"/> and writes nothing itself: the
/// node is the recording's one writer. Declared rows, so the GUI's panel and a test paint the same nodes. Render thread only.
/// </summary>
public sealed class PlanetaryRecordControl
{
    /// <summary>The longest recording the field takes: a day, far past any planet's useful run, and inside what a clock adds.</summary>
    public static readonly TimeSpan LongestDuration = TimeSpan.FromDays(1);

    /// <summary>The duration field, in seconds; a minute until the user types another.</summary>
    public TextInputState DurationInput { get; } = new TextInputState { Text = "60", Placeholder = "60" };

    /// <summary>The duration the field states; null when it states no number of seconds above 0 and up to a day.</summary>
    public TimeSpan? Duration =>
        double.TryParse(DurationInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0 && seconds <= LongestDuration.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// The control's rows: the duration and the button, then what the node says of its recording. <paramref name="capturing"/>
    /// is whether this view's capture runs, since a recording is of the capture going on; <paramref name="timeLeft"/> is the
    /// recording's by this computer's clock (<see cref="PlanetaryCaptureController.RecordingTimeLeft"/>). Sizes are design units.
    /// </summary>
    public ImmutableArray<Layout.Node> Rows(PlanetaryRecordingDto? recording, bool capturing, TimeSpan timeLeft, SignalBus? bus,
        float fontSize, float rowHeight, float gap)
    {
        var recordingNow = recording is { Recording: true };
        var duration = Duration;
        // Both go to the capture's own node, so neither is offered with no capture on show: Stop while a recording goes on,
        // Record while the field states a duration.
        var enabled = capturing && (recordingNow || duration is not null);
        var fill = !enabled ? GuiTheme.AltRowBg : recordingNow ? GuiTheme.DangerButtonBg : GuiTheme.GoButtonBg;
        var button = Layout.Builder.Text(recordingNow ? "Stop" : "Record", fontSize, enabled ? GuiTheme.InkOn(fill) : GuiTheme.Palette.DimText,
                TextAlign.Center, TextAlign.Center, widthSample: "Record")
            .PadX(10f).HStar().Bg(fill)
            .Clickable(enabled ? new HitResult.ButtonHit("PlanetaryRecordToggle") : null,
                enabled ? (Action<InputModifier>)(_ =>
                {
                    if (recordingNow)
                    {
                        bus?.Post(new StopPlanetaryRecordingSignal());
                    }
                    else if (Duration is { } asked)
                    {
                        bus?.Post(new StartPlanetaryRecordingSignal(asked));
                    }
                }) : null);
        if (enabled)
        {
            button = button.BgHover(GuiTheme.Hover(fill));
        }

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();
        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text("Secs", fontSize, GuiTheme.Palette.DimText, TextAlign.Near, TextAlign.Center).WFixed(52f).HStar(),
                Layout.Builder.TextInput(DurationInput, fontSize).Stretch(),
                button)
            .RowH(rowHeight).WithGap(gap));

        var small = fontSize * 0.85f;
        if (duration is null)
        {
            rows.Add(Line("Seconds, above 0 and up to a day", small, GuiTheme.Palette.Warn, rowHeight));
        }
        if (recording is null)
        {
            if (!capturing)
            {
                rows.Add(Line("Start the capture to record it", small, GuiTheme.Palette.DimText, rowHeight));
            }
            return rows.ToImmutable();
        }

        var lines = Describe(recording, timeLeft);
        // The file's name keeps its end, where the time it was taken is.
        rows.Add(Layout.Builder.Text(lines[0], small, GuiTheme.Palette.HeaderText, TextAlign.Near, TextAlign.Center, TextTrim.Start).RowH(rowHeight * 0.8f));
        for (var i = 1; i < lines.Length; i++)
        {
            var failed = i == 1 && recording.FailureReason is not null;
            rows.Add(Line(lines[i], small, failed ? GuiTheme.Palette.Error : GuiTheme.Palette.DimText, rowHeight));
        }
        return rows.ToImmutable();
    }

    private static Layout.Node Line(string text, float fontSize, RGBAColor32 color, float rowHeight)
        => Layout.Builder.Text(text, fontSize, color, TextAlign.Near, TextAlign.Center).RowH(rowHeight * 0.8f);

    /// <summary>
    /// What the control says of <paramref name="recording"/>, one line each: its file's name, then, while it records, the
    /// frames written and dropped and the time left; once it has ended, why (or why it failed), then the frames.
    /// </summary>
    public static ImmutableArray<string> Describe(PlanetaryRecordingDto recording, TimeSpan timeLeft)
    {
        var file = Path.GetFileName(recording.Path);
        var frames = $"{recording.FramesWritten} written   {recording.FramesDropped} dropped";
        if (recording.Recording)
        {
            return [file, $"{frames}   {Clock(timeLeft)} left"];
        }

        var why = recording.FailureReason is { } failure ? $"Failed: {failure}"
            : recording.Written ? $"Ended because {recording.EndReason}"
            : $"Ended because {recording.EndReason}, finishing the file";
        return [file, why, frames];
    }

    // Minutes and seconds, the seconds rounded UP so the last one shows until the recording ends; hours once there are any.
    private static string Clock(TimeSpan left)
    {
        var whole = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, left.TotalSeconds)));
        return whole.TotalHours >= 1
            ? whole.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : whole.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}

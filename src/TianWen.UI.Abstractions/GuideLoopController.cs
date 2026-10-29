using System;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The Guider tab's loop of the guide camera while no session runs (#1915, docs/plans/live-session-preview.md): a second
/// <see cref="LiveViewController"/> (the node's live view, asked for the profile's guide camera) beside the Preview's, plus
/// the exposure and gain the tab's steppers set. It shares the controller's frame reader, state poll and staged controls,
/// never a copy of them.
/// </summary>
public sealed class GuideLoopController
{
    public GuideLoopController(ITimeProvider timeProvider, ILogger<LiveViewController> logger)
    {
        Run = new GuidingRunController(timeProvider, logger);
        // The frames are the guider's source, which a guiding run takes over from the loop and gives back, and the watching goes
        // on through the run, so the picture and the Loop button are the same across Calibrate, Guide and Stop (#1915).
        View = new LiveViewController(timeProvider, logger) { PolledFrameSource = FrameSources.Guider, KeepsWatching = Run.KeepsLoopWatching };
    }

    /// <summary>The loop's live view: the frames (<see cref="LiveViewController.Source"/>), the node's state, start and stop.</summary>
    public LiveViewController View { get; }

    /// <summary>Calibrating and guiding off the loop (#1915): the node's guiding run, which replaces the loop and puts it back.</summary>
    public GuidingRunController Run { get; }

    /// <summary>The exposure the loop starts at and the stepper shows, in seconds. A guide frame is about a second.</summary>
    public double ExposureSeconds { get; set; } = 1.0;

    /// <summary>The gain the loop asks for; null keeps the camera's own.</summary>
    public int? Gain { get; set; }

    /// <summary>The step a gain button takes: a camera's gain has no range here, so a tenth of a hundred.</summary>
    public const int GainStep = 10;

    /// <summary>Steps <see cref="ExposureSeconds"/> along the Preview's ladder and sends it when the loop runs.</summary>
    public void StepExposure(int direction)
    {
        ExposureSeconds = LiveSessionActions.StepExposure(ExposureSeconds, direction);
        if (View.IsLive && !Run.IsRunning)
        {
            View.SetExposure(TimeSpan.FromSeconds(ExposureSeconds));
        }
    }

    /// <summary>Steps <see cref="Gain"/> (from the camera's own as zero) and sends it when the loop runs.</summary>
    public void StepGain(int direction)
    {
        Gain = Math.Clamp((Gain ?? 0) + direction * GainStep, 0, short.MaxValue);
        if (View.IsLive && !Run.IsRunning)
        {
            View.SetGain((short)Gain.Value);
        }
    }

    /// <summary>The gain stepper's value: the number, or "camera" while none is asked for.</summary>
    public string GainLabel => Gain is { } gain ? $"Gain: {gain}" : "Gain: camera";
}

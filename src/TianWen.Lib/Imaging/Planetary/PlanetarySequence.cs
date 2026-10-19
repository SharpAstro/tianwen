using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One step of a planetary sequence (#1340): the filter it is taken through, the stream's settings, and how long it records.
/// </summary>
/// <param name="FilterPosition">The wheel's slot, counted from 0; -1 leaves the wheel where it is (a sequence with no wheel).</param>
/// <param name="Gain">The stream's gain; null keeps the gain it streams at.</param>
/// <param name="BitDepth">The depth it streams in; null keeps the depth it streams in.</param>
public sealed record PlanetarySequenceStep(int FilterPosition, TimeSpan Exposure, short? Gain, RecordingLength Length, BitDepth? BitDepth = null)
{
    /// <summary>A step that leaves the wheel where it is.</summary>
    public const int NoFilterChange = -1;
}

/// <summary>
/// A mono filter-wheel sequence (Phase G of docs/plans/live-planetary-capture.md, #1340): its steps taken in order as one SET,
/// the set repeated <see cref="Sets"/> times, with <see cref="PauseBetweenSets"/> between two, and no set begun past
/// <see cref="Until"/>. The owner's 2026-10-07 Saturn was ten sets of Red, Green and Blue, 10,000 frames a file.
/// </summary>
public sealed record PlanetarySequence(ImmutableArray<PlanetarySequenceStep> Steps, int Sets, TimeSpan PauseBetweenSets = default, DateTimeOffset? Until = null)
{
    /// <summary>
    /// Why the sequence cannot be run with a wheel holding <paramref name="filters"/>, in words, or null when it can; a null
    /// <paramref name="filters"/> is no wheel at all, which only a sequence that never changes the filter runs without.
    /// </summary>
    public string? Invalid(IReadOnlyList<InstalledFilter>? filters)
    {
        if (Steps.IsDefaultOrEmpty)
        {
            return "A sequence needs at least one step";
        }
        if (Sets < 1)
        {
            return "A sequence runs its steps at least once";
        }
        if (PauseBetweenSets < TimeSpan.Zero)
        {
            return "A pause between sets is zero or longer";
        }
        for (var i = 0; i < Steps.Length; i++)
        {
            var step = Steps[i];
            var which = $"Step {i + 1}";
            if (step.Exposure <= TimeSpan.Zero)
            {
                return $"{which} needs a positive exposure";
            }
            if (step.Gain is < 0)
            {
                return $"{which}: a gain is 0 or more";
            }
            if (step.Length.Invalid is { } length)
            {
                return $"{which}: {length}";
            }
            if (step.FilterPosition == PlanetarySequenceStep.NoFilterChange)
            {
                continue;
            }
            if (filters is null)
            {
                return $"{which} is taken through a filter, and the camera's telescope has no filter wheel";
            }
            if (step.FilterPosition < 0 || step.FilterPosition >= filters.Count)
            {
                return filters.Count == 0
                    ? $"{which} is taken through a filter, and the wheel names no filters"
                    : $"{which} asks for position {step.FilterPosition}; the wheel has positions 0 to {filters.Count - 1}";
            }
        }
        return null;
    }
}

/// <summary>Where a <see cref="PlanetarySequenceRunner"/> is.</summary>
public enum PlanetarySequencePhase
{
    /// <summary>Asked for, not begun.</summary>
    Starting,

    /// <summary>The stream is stopped for a step's filter and settings.</summary>
    ChangingFilter,

    /// <summary>A step records.</summary>
    Recording,

    /// <summary>Between two sets.</summary>
    Pausing,

    /// <summary>Every set taken, or its end time came.</summary>
    Done,

    /// <summary>Stopped before it was done.</summary>
    Stopped,

    /// <summary>Ended by a failure, said in <see cref="PlanetarySequenceProgress.FailureReason"/>.</summary>
    Failed,
}

/// <summary>
/// Where a sequence is: its phase, the set and the step (both counted from 1, 0 before the first), the filter of the step, and the
/// files written whole so far.
/// </summary>
public sealed record PlanetarySequenceProgress(
    PlanetarySequencePhase Phase,
    int Set,
    int Sets,
    int Step,
    int Steps,
    string? Filter,
    ImmutableArray<string> Files,
    string? FailureReason = null)
{
    /// <summary>Whether it has ended, however.</summary>
    public bool HasEnded => Phase is PlanetarySequencePhase.Done or PlanetarySequencePhase.Stopped or PlanetarySequencePhase.Failed;
}

/// <summary>
/// Runs a <see cref="PlanetarySequence"/> on a capture that streams (#1340): for each step, ONE interlude of the capture
/// (<see cref="PlanetaryCapture.InterludeAsync"/>) stops the stream, turns the wheel through the one filter change
/// (<see cref="FilterChange.ChangeAsync"/>, the focus offset of the filter arrived at less the one left), and starts the stream
/// again at the step's exposure, gain and depth, its first frames dropped; then ONE recording
/// (<see cref="PlanetaryCapture.TryStartRecording(string, RecordingLength, out SerRecording?, out string?)"/>) takes the step's
/// time or frames into a file named for its filter. The capture goes on streaming throughout, so its live view and stack carry
/// on. The wheel and the focuser are its caller's to claim for the run; the capture keeps its claim on the camera.
/// </summary>
/// <param name="wheel">The wheel of the OTA the capture streams; null when it has none.</param>
/// <param name="focuser">The OTA's connected focuser, moved by each change's focus offset; null for none.</param>
public sealed class PlanetarySequenceRunner(PlanetaryCapture capture, IFilterWheelDriver? wheel, FilterFocuser? focuser, ITimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>What a sequence's interlude is called, which a refusal names.</summary>
    public const string StepChange = "a sequence's step";

    private PlanetarySequenceProgress _progress = new(PlanetarySequencePhase.Starting, 0, 0, 0, 0, null, []);

    /// <summary>Where it is now: one reference, replaced whole, so a reader never sees half a step.</summary>
    public PlanetarySequenceProgress Progress => Volatile.Read(ref _progress);

    /// <summary>
    /// Takes every set of <paramref name="sequence"/>, each step's file at the path <paramref name="pathFor"/> gives for its
    /// filter's name (null where the step leaves the wheel) and the instant it starts. Ends once every set is taken or its end
    /// time has come; a stop (<paramref name="cancellationToken"/>) ends the step's recording, whose frames so far are kept.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is not one this wheel can run, a step's change or recording was
    /// refused, or the capture ended under it; the reason is <see cref="Progress"/>'s too.</exception>
    public async Task RunAsync(PlanetarySequence sequence, Func<string?, DateTimeOffset, string> pathFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var filters = wheel?.Filters;
        var steps = sequence.Steps;
        var files = ImmutableArray<string>.Empty;
        var (set, index) = (0, 0);
        string? filter = null;
        void Note(PlanetarySequencePhase phase, string? failure = null)
            => Volatile.Write(ref _progress, new PlanetarySequenceProgress(phase, set, sequence.Sets, index, steps.IsDefault ? 0 : steps.Length, filter, files, failure));

        try
        {
            if (sequence.Invalid(filters) is { } invalid)
            {
                throw new InvalidOperationException(invalid);
            }

            for (set = 1; set <= sequence.Sets; set++)
            {
                if (set > 1 && sequence.PauseBetweenSets > TimeSpan.Zero)
                {
                    index = 0;
                    Note(PlanetarySequencePhase.Pausing);
                    await timeProvider.SleepAsync(sequence.PauseBetweenSets, cancellationToken);
                }
                if (timeProvider.GetUtcNow() >= sequence.Until)
                {
                    logger.LogInformation("Planetary sequence: its end time {Until} has come; {Sets} of {All} sets taken.", sequence.Until, set - 1, sequence.Sets);
                    set--;
                    break;
                }

                for (index = 1; index <= steps.Length; index++)
                {
                    var step = steps[index - 1];
                    filter = step.FilterPosition != PlanetarySequenceStep.NoFilterChange && filters is { } named
                        ? FilterChange.NameOf(named, step.FilterPosition)
                        : null;
                    Note(PlanetarySequencePhase.ChangingFilter);
                    await ChangeAsync(step, cancellationToken);

                    Note(PlanetarySequencePhase.Recording);
                    var path = pathFor(filter, timeProvider.GetUtcNow());
                    var recording = await RecordAsync(path, step, cancellationToken);
                    files = files.Add(recording.Path);
                    logger.LogInformation("Planetary sequence: set {Set} of {Sets}, step {Step} of {Steps} ({Filter}): {Frames} frames in {Path} ({Reason}).",
                        set, sequence.Sets, index, steps.Length, filter ?? "no filter change", recording.FramesWritten, recording.Path, recording.EndReason);
                }
                index = steps.Length;
            }
            set = Math.Min(set, sequence.Sets);
            Note(PlanetarySequencePhase.Done);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Note(PlanetarySequencePhase.Stopped);
            throw;
        }
        catch (Exception ex)
        {
            Note(PlanetarySequencePhase.Failed, StatusText.FromException(ex));
            throw;
        }
    }

    // The stream stopped for the step: the wheel turned to its filter and the focuser moved by the offset, then the stream
    // started again at its exposure, gain and depth.
    private async Task ChangeAsync(PlanetarySequenceStep step, CancellationToken cancellationToken)
    {
        async ValueTask TurnAsync(CancellationToken token)
        {
            if (step.FilterPosition != PlanetarySequenceStep.NoFilterChange && wheel is { } turning)
            {
                await FilterChange.ChangeAsync(turning, FilterWheelMotion.Direct(turning), step.FilterPosition, focuser, timeProvider, logger,
                    turning.Name, token);
            }
        }

        var interlude = new PlanetaryInterlude(StepChange, TurnAsync)
        {
            RestartAt = had => had with
            {
                Options = had.Options with
                {
                    Exposure = step.Exposure,
                    Gain = step.Gain ?? had.Options.Gain,
                    BitDepth = step.BitDepth ?? had.Options.BitDepth,
                },
            },
        };
        if (await capture.InterludeAsync(interlude, cancellationToken) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    // The step's recording, from the first frame after the change to its time or frames. A stop ends it and waits for its file
    // to close, so the frames taken are kept and the file is whole before the stop is heard.
    private async Task<SerRecording> RecordAsync(string path, PlanetarySequenceStep step, CancellationToken cancellationToken)
    {
        if (!capture.TryStartRecording(path, step.Length, out var recording, out var refusal))
        {
            throw new InvalidOperationException(refusal);
        }
        try
        {
            await recording.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            recording.End("the sequence was stopped");
            await recording.Completion;
            throw;
        }

        if (recording.FailureReason is { } failure)
        {
            throw new InvalidOperationException($"The recording to {recording.Path} could not be written: {failure}");
        }
        if (!capture.IsCapturing)
        {
            throw new InvalidOperationException(capture.FailureReason is { } why
                ? $"The {capture.Name} ended during the sequence: {why}"
                : $"The {capture.Name} ended during the sequence");
        }
        return recording;
    }
}

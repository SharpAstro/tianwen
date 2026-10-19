using System;
using System.Globalization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// How long a recording runs (<see cref="SerRecording"/>): for a TIME or for a number of FRAMES, whichever its asker names. A
/// sequence's step chooses one or the other (#1340, the owner, 2026-10-19), and a frame target holds the same frames on any
/// night, where the rate a time gives depends on the camera and the window.
/// </summary>
public readonly record struct RecordingLength
{
    private RecordingLength(TimeSpan? time, int? frames) => (Time, Frames) = (time, frames);

    /// <summary>The time it records for; null for a frame target.</summary>
    public TimeSpan? Time { get; }

    /// <summary>The frames it records; null for a time.</summary>
    public int? Frames { get; }

    /// <summary>A recording of <paramref name="time"/>.</summary>
    public static RecordingLength For(TimeSpan time) => new(time, null);

    /// <summary>A recording of <paramref name="frames"/> frames.</summary>
    public static RecordingLength OfFrames(int frames) => new(null, frames);

    /// <summary>Why it cannot be recorded, in words: no length, or one that is not positive; null when it can.</summary>
    public string? Invalid => (Time, Frames) switch
    {
        ({ } time, null) when time > TimeSpan.Zero => null,
        (null, { } frames) when frames > 0 => null,
        (null, null) => "A recording needs a length: a time or a number of frames",
        _ => "A recording needs a positive length",
    };

    /// <summary>"60 s" or "10000 frames".</summary>
    public override string ToString()
        => Time is { } time ? string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.###} s") : $"{Frames} frames";
}

using System;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A live planetary capture as the node's run: <c>POST /api/v1/planetary</c> (P5 part 5 of
/// docs/plans/hardware-in-the-server.md, #934). The node streams the OTA's camera, stacks the rolling window and recentres
/// the disk; a client watches the live frame and the rolling master at <c>/frames/planetary/live</c> and
/// <c>/frames/planetary/master</c>. It ends on <c>DELETE /api/v1/planetary</c>, and by itself once no client has watched it
/// for the node's detach grace.
/// </summary>
public sealed class PlanetaryRequestDto
{
    /// <summary>The OTA whose camera streams.</summary>
    public int OtaIndex { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Exposure per frame, in milliseconds.</summary>
    public double ExposureMs { get; set; } = 10.0;

    /// <summary>Gain, or null to keep the camera's.</summary>
    public short? Gain { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>The readout window's size, snapped to the camera's ROI rule.</summary>
    public int RoiWidth { get; set; } = 640;

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    public int RoiHeight { get; set; } = 320;

    /// <summary>The recenter, as it starts; null starts it on its defaults (<see cref="PlanetaryRecenterDto"/>).</summary>
    public PlanetaryRecenterDto? Recenter { get; init; }
}

/// <summary>
/// The centre-of-mass recenter's settings (<see cref="PlanetaryCapture.ConfigureRecenter"/>), given WHOLE: a field left
/// out takes its default, never the value it had. The defaults are the GUI's: auto-recenter ON through the readout
/// window, the mount nudge OFF, since its sign is uncalibrated.
/// </summary>
public sealed class PlanetaryRecenterDto
{
    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    public bool Auto { get; set; } = true;

    /// <summary>Allow the coarse mount nudge when the window is at the sensor's edge.</summary>
    public bool MountJog { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    public int DeadbandPixels { get; set; } = 4;

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>The fraction of the measured offset corrected per frame, (0, 1].</summary>
    public double Gain { get; set; } = 0.5;

    public bool FlipRa { get; init; }

    public bool FlipDec { get; init; }
}

/// <summary>
/// A change to the capture going on: <c>PUT /api/v1/planetary/controls</c>, taking effect after the next frame. Each field
/// left null is left as it is.
/// </summary>
public sealed class PlanetaryControlsDto
{
    public double? ExposureMs { get; init; }

    public short? Gain { get; init; }

    /// <summary>A new readout window size; both or neither. The live stack starts again at the new framing.</summary>
    public int? RoiWidth { get; init; }

    public int? RoiHeight { get; init; }

    /// <summary>Pans the readout window by this many pixels: the mount-free framing nudge.</summary>
    public int? JogX { get; init; }

    public int? JogY { get; init; }

    /// <summary>The recenter's settings, replaced whole.</summary>
    public PlanetaryRecenterDto? Recenter { get; init; }
}

/// <summary>
/// The planetary capture going on, or the last one to end until the node's next run replaces it:
/// <c>GET /api/v1/planetary</c>.
/// </summary>
public sealed class PlanetaryStateDto
{
    public int OtaIndex { get; init; }

    /// <summary>The camera's name.</summary>
    public required string Camera { get; init; }

    /// <summary>The readout window applied at the start, after snapping.</summary>
    public int RoiWidth { get; init; }

    public int RoiHeight { get; init; }

    public bool Running { get; init; }

    public int FramesReceived { get; init; }

    /// <summary>Frames the camera reported dropped; 0 for one that cannot report it.</summary>
    public int DroppedFrames { get; init; }

    public double FramesPerSecond { get; init; }

    /// <summary>Masters the node has stacked so far.</summary>
    public int Masters { get; init; }

    /// <summary>Frames in the latest master's window.</summary>
    public int StackedFrames { get; init; }

    /// <summary>The disk's last centre-of-mass offset from the frame's centre, in pixels.</summary>
    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    /// <summary>Which actuator the latest recenter frame engaged.</summary>
    public RecenterActuator RecenterActuator { get; init; }

    /// <summary>Why it failed, in words; null while it runs, and when it ended on a stop.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The recording to disk going on, or the last one to end; null before any.</summary>
    public PlanetaryRecordingDto? Recording { get; init; }
}

/// <summary>
/// A recording of the capture going on to a SER file under the node's image folder: <c>POST /api/v1/planetary/record</c>
/// (P5 part 5d). It finishes its duration whether or not anyone watches; <c>DELETE /api/v1/planetary/record</c> ends it
/// sooner.
/// </summary>
public sealed class PlanetaryRecordRequestDto
{
    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>How long to record, in seconds.</summary>
    public double DurationSeconds { get; set; } = 60;
}

/// <summary>A recording to disk, going on or the last to end (<see cref="SerRecording"/>).</summary>
public sealed class PlanetaryRecordingDto
{
    /// <summary>The SER file, on the node's disk.</summary>
    public required string Path { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>When it ends by itself.</summary>
    public DateTimeOffset EndsUtc { get; init; }

    public bool Recording { get; init; }

    /// <summary>Whether the file is whole: closed, its header written. A recording that has ended may still be writing.</summary>
    public bool Written { get; init; }

    public int FramesWritten { get; init; }

    /// <summary>Frames the disk could not keep up with, never recorded.</summary>
    public int FramesDropped { get; init; }

    /// <summary>Why it ended, in words; null while it records.</summary>
    public string? EndReason { get; init; }

    /// <summary>Why the file could not be written, in words.</summary>
    public string? FailureReason { get; init; }

    public static PlanetaryRecordingDto From(SerRecording recording) => new PlanetaryRecordingDto
    {
        Path = recording.Path,
        StartedUtc = recording.StartedAt,
        EndsUtc = recording.EndsAt,
        Recording = recording.IsRecording,
        Written = recording.Completion.IsCompleted,
        FramesWritten = recording.FramesWritten,
        FramesDropped = recording.FramesDropped,
        EndReason = recording.EndReason,
        FailureReason = recording.FailureReason,
    };
}

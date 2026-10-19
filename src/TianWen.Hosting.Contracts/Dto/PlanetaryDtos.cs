using System;
using TianWen.Lib.Imaging;
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

    /// <summary>
    /// The depth the camera streams in, one of its device state's <see cref="CameraDeviceStateDto.VideoBitDepths"/>; null
    /// for the camera's own. A camera that cannot stream in it keeps its own, which <see cref="PlanetaryStateDto.BitDepth"/>
    /// reports.
    /// </summary>
    public BitDepth? BitDepth { get; init; }

    /// <summary>The camera's high-speed readout, where it has one (<see cref="CameraDeviceStateDto.CanFastReadout"/>); null for on.</summary>
    public bool? HighSpeed { get; init; }

    /// <summary>The recenter, as it starts; null starts it on its defaults (<see cref="PlanetaryRecenterDto"/>).</summary>
    public PlanetaryRecenterDto? Recenter { get; init; }
}

/// <summary>
/// The centre-of-mass recenter's settings (<see cref="PlanetaryCapture.ConfigureRecenter"/>), given WHOLE: a field left
/// out takes its default, never the value it had. The defaults are the GUI's: the recenter and the mount nudge both OFF.
/// Recentering is opt-in (the user, 2026-09-28): the operator starts it once the rig is ready for it. It also costs the
/// capture loop about 20 ms a frame at full frame, which took an ASI462MC's capture from 31.9 frames a second to 20.
/// </summary>
public sealed class PlanetaryRecenterDto
{
    /// <summary>Recenter the disk by panning the readout window.</summary>
    public bool Auto { get; init; }

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

    /// <summary>A new depth to stream in (<see cref="PlanetaryRequestDto.BitDepth"/>): the camera restarts its stream in it.</summary>
    public BitDepth? BitDepth { get; init; }

    /// <summary>Switches the camera's high-speed readout (<see cref="PlanetaryRequestDto.HighSpeed"/>).</summary>
    public bool? HighSpeed { get; init; }
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

    /// <summary>The camera's measured rate; null until it can be measured.</summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>The depth the newest frame came in; null before the first.</summary>
    public BitDepth? BitDepth { get; init; }

    /// <summary>Masters the node has stacked so far.</summary>
    public int Masters { get; init; }

    /// <summary>Frames in the latest master's window.</summary>
    public int StackedFrames { get; init; }

    /// <summary>The disk's last centre-of-mass offset from the frame's centre, in pixels; null until a disk is found.</summary>
    public double? OffsetX { get; init; }

    /// <inheritdoc cref="OffsetX"/>
    public double? OffsetY { get; init; }

    /// <summary>Which actuator the latest recenter frame engaged.</summary>
    public RecenterActuator RecenterActuator { get; init; }

    /// <summary>Why it failed, in words; null while it runs, and when it ended on a stop.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The recording to disk going on, or the last one to end; null before any.</summary>
    public PlanetaryRecordingDto? Recording { get; init; }

    /// <summary>The sequence going on, or the last one to end on this capture; null before any (#1340).</summary>
    public PlanetarySequenceDto? Sequence { get; init; }
}

/// <summary>
/// One step of a sequence (<see cref="PlanetarySequenceStep"/>): the filter, the stream's settings, and how long it records, a
/// time (<see cref="DurationSeconds"/>) or a number of frames (<see cref="Frames"/>), exactly one of the two.
/// </summary>
public sealed class PlanetarySequenceStepDto
{
    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>The wheel's slot, counted from 0; -1 leaves the wheel where it is.</summary>
    public int FilterPosition { get; set; } = PlanetarySequenceStep.NoFilterChange;

    /// <summary>Exposure per frame, in milliseconds.</summary>
    public double ExposureMs { get; init; }

    /// <summary>Gain, or null to keep the gain the capture streams at.</summary>
    public short? Gain { get; init; }

    /// <summary>The depth to stream in, or null to keep the capture's.</summary>
    public BitDepth? BitDepth { get; init; }

    /// <summary>How long it records, in seconds; null when it records a number of <see cref="Frames"/>.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>How many frames it records; null when it records for <see cref="DurationSeconds"/>.</summary>
    public int? Frames { get; init; }

    /// <summary>The step, or why it is not one, in words.</summary>
    public bool TryToStep([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlanetarySequenceStep? step,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? invalid)
    {
        step = null;
        RecordingLength length;
        switch (DurationSeconds, Frames)
        {
            case ({ } seconds, null) when double.IsFinite(seconds):
                length = RecordingLength.For(TimeSpan.FromSeconds(seconds));
                break;
            case (null, { } frames):
                length = RecordingLength.OfFrames(frames);
                break;
            case (null, null):
                invalid = "records for a time or a number of frames, and names neither";
                return false;
            case (not null, not null):
                invalid = "records for a time or a number of frames, and names both";
                return false;
            default:
                invalid = "needs a finite duration";
                return false;
        }
        if (!double.IsFinite(ExposureMs))
        {
            invalid = "needs a finite exposure";
            return false;
        }
        step = new PlanetarySequenceStep(FilterPosition, TimeSpan.FromMilliseconds(ExposureMs), Gain, length, BitDepth);
        invalid = null;
        return true;
    }
}

/// <summary>
/// A mono filter-wheel sequence on the planetary capture going on: <c>POST /api/v1/planetary/sequence</c> (#1340). Its steps
/// taken in order as one set, the set repeated <see cref="Sets"/> times; it claims the OTA's filter wheel and focuser until it
/// ends, and <c>DELETE /api/v1/planetary/sequence</c> stops it, the step's frames so far kept.
/// </summary>
public sealed class PlanetarySequenceRequestDto
{
    public PlanetarySequenceStepDto[] Steps { get; init; } = [];

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>How many times the steps are taken.</summary>
    public int Sets { get; set; } = 1;

    /// <summary>Seconds to wait between two sets.</summary>
    public double PauseSeconds { get; init; }

    /// <summary>No set is begun from this instant on; null for none.</summary>
    public DateTimeOffset? UntilUtc { get; init; }
}

/// <summary>Where a sequence is (<see cref="PlanetarySequenceProgress"/>).</summary>
public sealed class PlanetarySequenceDto
{
    public PlanetarySequencePhase Phase { get; init; }

    /// <summary>The set it takes, counted from 1; 0 before the first.</summary>
    public int Set { get; init; }

    public int Sets { get; init; }

    /// <summary>The step of the set it takes, counted from 1; 0 before the first and while it pauses.</summary>
    public int Step { get; init; }

    public int Steps { get; init; }

    /// <summary>The filter of the step; null for one that leaves the wheel.</summary>
    public string? Filter { get; init; }

    /// <summary>The files written whole so far, in order, on the node's disk.</summary>
    public string[] Files { get; init; } = [];

    /// <summary>Why it failed, in words; null unless it did.</summary>
    public string? FailureReason { get; init; }

    public static PlanetarySequenceDto From(PlanetarySequenceProgress progress) => new PlanetarySequenceDto
    {
        Phase = progress.Phase,
        Set = progress.Set,
        Sets = progress.Sets,
        Step = progress.Step,
        Steps = progress.Steps,
        Filter = progress.Filter,
        Files = [.. progress.Files],
        FailureReason = progress.FailureReason,
    };
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

    /// <summary>When it ends by itself; null for one that ends at its <see cref="FrameTarget"/>.</summary>
    public DateTimeOffset? EndsUtc { get; init; }

    /// <summary>The frames it ends at; null for one that ends at <see cref="EndsUtc"/>.</summary>
    public int? FrameTarget { get; init; }

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
        FrameTarget = recording.FrameTarget,
        Recording = recording.IsRecording,
        Written = recording.Completion.IsCompleted,
        FramesWritten = recording.FramesWritten,
        FramesDropped = recording.FramesDropped,
        EndReason = recording.EndReason,
        FailureReason = recording.FailureReason,
    };
}

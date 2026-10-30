using System;

namespace TianWen.Hosting.Dto;

/// <summary>
/// A dark library the node takes with one camera: <c>POST /api/v1/darks</c> (P5 part 1 of
/// docs/plans/hardware-in-the-server.md, #934), what the CLI's <c>darks</c> does, as a run the node owns.
/// </summary>
public sealed class DarkLibraryRequestDto
{
    /// <summary>The camera, connected to the node, as the device plane names it.</summary>
    public required string DeviceUri { get; init; }

    /// <summary>
    /// Exposure per frame, in seconds. A dark must match its light's exposure exactly, so it is never rounded. For a bias,
    /// the shortest exposure the camera takes.
    /// </summary>
    public required double ExposureSeconds { get; init; }

    /// <summary>Frames to take.</summary>
    public required int Count { get; init; }

    /// <summary>Gain, or null to leave the camera's.</summary>
    public short? Gain { get; init; }

    /// <summary>Offset, or null to leave the camera's.</summary>
    public int? Offset { get; init; }

    /// <summary>Readout mode by name, or null to leave the camera's.</summary>
    public string? ReadoutMode { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Binning, both axes.</summary>
    public int Bin { get; set; } = 1;

    /// <summary>Bias frames rather than darks: labelled so in their headers, which is what a stacker matches on.</summary>
    public bool Bias { get; init; }

    /// <summary>
    /// The sensor temperature, in °C, to cool to through the node's ramp before the first frame, or null to shoot at whatever
    /// the sensor is. The run reads the sensor back and takes no frame short of it.
    /// </summary>
    public double? SetpointC { get; init; }

    /// <summary>Warm the sensor through the node's ramp and switch its cooler off once the frames are taken (not when the run
    /// is stopped), leaving the camera connected; otherwise the cooler is left held where the run took it.</summary>
    public bool WarmWhenFinished { get; init; }

    /// <summary>
    /// Which camera of the profile this is (#1915): <see cref="LiveViewCamera.Guider"/> files the frames in the guide camera's
    /// own dark library, which its loop and its guiding apply; an OTA's camera (the default) keeps the library it always had.
    /// </summary>
    public LiveViewCamera CameraRole { get; init; }

    /// <summary>
    /// Every exposure to take <see cref="Count"/> frames of, in seconds, when there is more than one (a guide camera's library is
    /// a row per exposure it is looped at). Null or empty takes <see cref="ExposureSeconds"/> alone.
    /// </summary>
    public double[]? ExposuresSeconds { get; init; }
}

/// <summary>
/// A dark library the node is taking, or took last: <c>GET /api/v1/darks</c>. Kept after it ends until the node's next
/// run replaces it, as a session's state is, so a client polling it sees how it ended.
/// </summary>
public sealed class DarkLibraryStateDto
{
    /// <summary>The camera's name.</summary>
    public required string Camera { get; init; }

    public required string DeviceUri { get; init; }

    public bool Bias { get; init; }

    public double ExposureSeconds { get; init; }

    /// <summary>Every exposure the run takes, in order (one entry for a single exposure); <see cref="ExposureSeconds"/> is the one in hand.</summary>
    public double[] ExposuresSeconds { get; set; } = [];

    /// <summary>Whether this is the guide camera's library (<see cref="LiveViewCamera.Guider"/>).</summary>
    public LiveViewCamera CameraRole { get; init; }

    /// <summary>Frames asked for, per exposure.</summary>
    public int Count { get; init; }

    /// <summary>The setpoint the run cools to before its first frame, in °C; null when it shoots at whatever the sensor is.</summary>
    public double? SetpointC { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Frames written so far, in order.</summary>
    public DarkLibraryFrameDto[] Frames { get; set; } = [];

    /// <summary>Whether it is going on.</summary>
    public bool Running { get; init; }

    /// <summary>Whether it ended on a stop before its count.</summary>
    public bool Stopped { get; init; }

    /// <summary>Why it failed, in words; null while it runs, and when it ended well or was stopped.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>One frame a dark library wrote, and the sensor temperature it was taken at.</summary>
public sealed class DarkLibraryFrameDto
{
    /// <summary>The FITS file, a path on the node.</summary>
    public required string Path { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>The sensor temperature the camera stamped, in °C; null when it reports none.</summary>
    public double? SensorTemperatureC { get; init; }
}

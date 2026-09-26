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

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Binning, both axes.</summary>
    public int Bin { get; set; } = 1;

    /// <summary>Bias frames rather than darks: labelled so in their headers, which is what a stacker matches on.</summary>
    public bool Bias { get; init; }
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

    /// <summary>Frames asked for.</summary>
    public int Count { get; init; }

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

namespace TianWen.Hosting.Dto;

/// <summary>
/// Request body for <c>POST /api/v1/session/flats</c>. Kicks off an on-demand flat run
/// (<see cref="TianWen.Lib.Sequencing.ISession.RunFlatsOnlyAsync"/>) against the active (or
/// <c>?profileId=</c>) profile: connect the flat-relevant devices, cool to setpoint, capture, finalise.
/// All fields are optional -- unset flat knobs use <see cref="TianWen.Lib.Sequencing.SessionConfiguration"/>
/// defaults. Poll <c>GET /api/v1/session/state</c> for phase progress; the frames land under
/// <c>Flats/&lt;date&gt;/&lt;filter&gt;/Flat/</c> with the same headers as lights.
/// </summary>
public sealed class FlatsRequestDto
{
    /// <summary>Illumination source: <c>calibrator</c> (any cover/calibrator assigned to the OTA -- a
    /// flip-flat, a driver panel, or a hand-switched <c>ManualCoverDevice</c>; the default) or <c>sky</c>
    /// (twilight sky-flats -- needs the mount). A manual panel is selected by assigning a Manual Light
    /// Panel device to the OTA's cover slot, not by a source value.</summary>
    public string? Source { get; init; }

    /// <summary>Twilight period for <c>sky</c>: <c>dusk</c> (default) or <c>dawn</c>. Ignored otherwise.</summary>
    public string? Period { get; init; }

    /// <summary>Flat frames to keep per filter. Null = config default.</summary>
    public int? Count { get; init; }

    /// <summary>Target exposure level as a fraction of full well, 0..1. Null = config default (0.5).</summary>
    public double? Target { get; init; }

    /// <summary>Acceptance band around <see cref="Target"/>, 0..1. Null = config default (0.05).</summary>
    public double? Tolerance { get; init; }

    /// <summary>Minimum auto-exposure clamp in seconds. Null = config default.</summary>
    public double? MinExposureSeconds { get; init; }

    /// <summary>Maximum auto-exposure clamp in seconds. Null = config default.</summary>
    public double? MaxExposureSeconds { get; init; }

    /// <summary>First metering exposure in seconds (calibrator source only). Null = config default.</summary>
    public double? InitialExposureSeconds { get; init; }

    /// <summary>Calibrator panel brightness percentage, 0..100 (calibrator source only). Null = config default.</summary>
    public int? BrightnessPercent { get; init; }

    /// <summary>Maximum auto-exposure metering brackets (calibrator/manual). Null = config default.</summary>
    public int? MaxBrackets { get; init; }

    /// <summary>
    /// The configuration the flat run starts from, sent whole, as a session's is: a client's session setup, whose cooler
    /// setpoint makes the flats match the lights' temperature (P6 of docs/plans/hardware-in-the-server.md, #936, where the
    /// GUI's flat run moved onto the node). The flat knobs above are laid over it. Null starts from the node's declared
    /// defaults.
    /// </summary>
    public SessionConfigApiDto? Configuration { get; init; }
}

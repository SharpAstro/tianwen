namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Controls where pulse guide corrections are routed.
/// Configured as a query parameter on the guider device URI (e.g., <c>?pulseGuideSource=Camera</c>).
/// </summary>
public enum PulseGuideSource : byte
{
    /// <summary>
    /// Try mount pulse guiding first, fall back to camera ST-4, throw if neither supports it.
    /// The mount is preferred because camera ST-4 capability only reflects the presence of a
    /// socket, not a connected guide cable — pulses into an unwired port are silent no-ops.
    /// </summary>
    Auto,
    /// <summary>Force ST-4 via guider camera (fail if camera doesn't support it).</summary>
    Camera,
    /// <summary>Force mount pulse guiding (fail if mount doesn't support it).</summary>
    Mount
}

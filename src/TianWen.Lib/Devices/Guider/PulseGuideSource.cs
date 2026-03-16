namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Controls where pulse guide corrections are routed.
/// Configured as a query parameter on the guider device URI (e.g., <c>?pulseGuideSource=Camera</c>).
/// </summary>
public enum PulseGuideSource : byte
{
    /// <summary>Try camera ST-4 first, fall back to mount, throw if neither supports it.</summary>
    Auto,
    /// <summary>Force ST-4 via guider camera (fail if camera doesn't support it).</summary>
    Camera,
    /// <summary>Force mount pulse guiding (fail if mount doesn't support it).</summary>
    Mount
}

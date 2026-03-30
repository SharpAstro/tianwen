using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Flags describing what configuration options a guider device supports.
/// Used by the equipment tab to decide which settings to render.
/// </summary>
[Flags]
public enum GuiderCapabilities : byte
{
    None = 0,

    /// <summary>Pulse guide source can be configured (Auto/Camera/Mount).</summary>
    ConfigurablePulseGuideSource = 1 << 0,

    /// <summary>DEC reversal after meridian flip is configurable.</summary>
    ConfigurableDecFlip = 1 << 1,

    /// <summary>Neural guide model is available for online learning.</summary>
    NeuralGuiding = 1 << 2,
}

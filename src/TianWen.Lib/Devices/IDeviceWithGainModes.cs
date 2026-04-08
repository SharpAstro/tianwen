using System.Collections.Generic;

namespace TianWen.Lib.Devices;

/// <summary>
/// Implemented by camera devices that use named gain modes (e.g. Canon ISO)
/// instead of numeric gain values.
/// </summary>
public interface IDeviceWithGainModes
{
    /// <summary>
    /// Ordered list of named gain modes (e.g. "ISO 100", "ISO 200", ...).
    /// The index corresponds to the gain value stored in session settings.
    /// </summary>
    IReadOnlyList<string> GainModes { get; }
}

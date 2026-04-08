using FC.SDK.Canon;
using System;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// Exception wrapping a Canon EDS error code from FC.SDK.
/// </summary>
public sealed class CanonDriverException(EdsError error, string message)
    : Exception($"Canon EDS error {error} (0x{(uint)error:X8}): {message}")
{
    public EdsError EdsError { get; } = error;
}

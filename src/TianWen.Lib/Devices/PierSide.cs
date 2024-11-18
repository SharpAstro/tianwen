namespace TianWen.Lib.Devices;

/// <summary>
/// The pointing state of the mount.
/// </summary>
public enum PointingState
{
    /// <summary>
    /// Normal pointing state
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Unknown or indeterminate.
    /// </summary>
    Unknown = -1,

    /// <summary>
    /// Through the pole pointing state
    /// </summary>
    ThroughThePole = 1
}
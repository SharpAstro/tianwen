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

public static class PointingStateExtensions
{
    extension(PointingState state)
    {
        /// <summary>
        /// Returns the opposite pointing state (Normal ↔ ThroughThePole).
        /// Unknown remains Unknown.
        /// </summary>
        public PointingState Flipped => state switch
        {
            PointingState.Normal => PointingState.ThroughThePole,
            PointingState.ThroughThePole => PointingState.Normal,
            _ => state
        };
    }
}
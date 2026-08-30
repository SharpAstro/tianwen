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

/// <summary>
/// How a driver KNOWS the pointing state it reports from <see cref="IMountDriver.GetSideOfPierAsync"/>.
/// The same enum value means two different things across drivers -- "which state the mount is IN"
/// (a Dec encoder half, OnStep's <c>:Gm#</c>, an ASCOM device's own <c>SideOfPier</c>) versus "which
/// state a mount WOULD be in if its firmware flipped the instant it crossed the meridian" (derived from
/// the hour angle) -- and only the first may drive a mechanical safety limit: west of the meridian a
/// computed answer always reads as post-flip, which silences the meridian limit exactly when an
/// unflipped mount is tracking into its pier.
/// </summary>
public enum PointingStateSource
{
    /// <summary>The driver has no notion of a pointing state (a tracker, an alt-az mount); whatever it returns is a placeholder.</summary>
    None = 0,

    /// <summary>
    /// Derived from the hour angle: a prediction of the counterweight-down state for the current
    /// pointing, not an observation. Right for a mount whose firmware flips by itself; silent about one
    /// that tracks past the meridian until its next goto. The default, so an unaware driver can never
    /// silence the limit -- it falls back to the hour-angle tier instead.
    /// </summary>
    Computed = 1,

    /// <summary>Read from the mount's mechanics or firmware. The only source a mechanical limit may trust.</summary>
    Measured = 2,
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
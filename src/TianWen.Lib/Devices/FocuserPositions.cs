namespace TianWen.Lib.Devices;

/// <summary>
/// The ONE marker for "no focuser position": a frame taken with no focuser, a camera never told one, a focuser whose read
/// failed (#1764). Everywhere a focus position is an <see cref="int"/> (<see cref="ICameraDriver.FocusPosition"/>,
/// <see cref="Imaging.ImageMeta.FocusPos"/>, <see cref="Sequencing.CameraExposureState.FocusPosition"/>,
/// <see cref="FocuserReading.Position"/>), this is the value that says there is none.
/// </summary>
/// <remarks>
/// <para>It used to be -1, which stopped meaning "none" once a focuser's counter could go negative: a QHY QFOC keeps a signed
/// counter (<see cref="IFocuserDriver.MinStep"/>, #1763), so -1 is a position it can be at. <see cref="int.MinValue"/> is no
/// focuser's position: a floor that low would be a travel of two billion steps below zero.</para>
/// <para>On the wire it is null, never the number (the DTOs map it), and on screen it is a dash. A FITS file carries no
/// <c>FOCUSPOS</c> card for it, and a file without one reads back as it.</para>
/// </remarks>
public static class FocuserPositions
{
    /// <summary>No focuser position.</summary>
    public const int None = int.MinValue;

    /// <summary>True when <paramref name="position"/> is a position, not <see cref="None"/>.</summary>
    public static bool IsKnown(int position) => position != None;

    /// <summary><paramref name="position"/> as the wire carries it: null for <see cref="None"/>.</summary>
    public static int? ForWire(int position) => position == None ? null : position;

    /// <summary>A position off the wire: null is <see cref="None"/>.</summary>
    public static int FromWire(int? position) => position ?? None;
}

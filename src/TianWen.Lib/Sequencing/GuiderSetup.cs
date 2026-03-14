namespace TianWen.Lib.Sequencing;

public record GuiderSetup(Camera? Camera = null, Focuser? Focuser = null, OTA? OAG = null)
{
    /// <summary>
    /// Whether the guider is an Off-Axis Guider mounted on one of the imaging OTAs.
    /// When true, refocusing that OTA may disrupt guiding and guiding should be paused during focus.
    /// </summary>
    public bool IsOAG => OAG != null;

    /// <summary>
    /// Whether the guider has a dedicated focuser (i.e., auto-focus is possible for the guide scope).
    /// </summary>
    public bool HasFocuser => Focuser != null;

    /// <summary>
    /// Whether the guider has a dedicated camera (built-in guider) rather than relying on
    /// an external guider application (e.g., PHD2) that manages its own camera.
    /// </summary>
    public bool HasCamera => Camera != null;
}

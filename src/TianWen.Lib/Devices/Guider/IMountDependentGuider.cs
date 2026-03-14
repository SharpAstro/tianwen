namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Implemented by guider drivers that need direct access to the mount driver
/// for sending pulse guide corrections (e.g., built-in guiders using <see cref="GuideLoop"/>).
/// External guiders like PHD2 manage their own mount connection and do not implement this.
/// </summary>
public interface IMountDependentGuider : IGuider
{
    /// <summary>
    /// Sets the mount driver instance for pulse guide corrections.
    /// Called by <see cref="TianWen.Lib.Sequencing.SessionFactory"/> after construction
    /// to ensure the guider shares the same mount driver as the session.
    /// </summary>
    void SetMountDriver(IMountDriver mount);
}

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Implemented by guider drivers that need direct access to the mount and camera drivers
/// for sending pulse guide corrections (e.g., built-in guiders using <see cref="GuideLoop"/>).
/// External guiders like PHD2 manage their own connections and do not implement this.
/// </summary>
public interface IDeviceDependentGuider : IGuider
{
    /// <summary>
    /// Wires the mount and camera into this guider.
    /// Called by <see cref="TianWen.Lib.Sequencing.SessionFactory"/> after construction
    /// to ensure the guider shares the same device drivers as the session.
    /// The pulse guide routing (<see cref="PulseGuideSource"/>) is read from the guider device URI.
    /// </summary>
    /// <param name="mount">Mount driver for position queries and optional pulse guiding.</param>
    /// <param name="camera">Guider camera for frame capture and optional ST-4 pulse guiding.</param>
    /// <summary>
    /// Whether this guider requires a dedicated camera driver. Built-in guiders need a camera
    /// for frame capture; fake/external guiders may not.
    /// </summary>
    bool RequiresCamera => true;

    void LinkDevices(IMountDriver mount, ICameraDriver camera);
}

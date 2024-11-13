namespace TianWen.Lib.Devices.INDI;

/// <summary>
/// Enum representing different INDI driver interfaces.
/// </summary>
public enum IndiDriverInterface
{
    /// <summary>
    /// Default interface for all INDI devices.
    /// </summary>
    General = 0,

    /// <summary>
    /// Telescope interface, must subclass INDI::Telescope.
    /// </summary>
    Telescope = (1 << 0),

    /// <summary>
    /// CCD interface, must subclass INDI::CCD.
    /// </summary>
    CCD = (1 << 1),

    /// <summary>
    /// Guider interface, must subclass INDI::GuiderInterface.
    /// </summary>
    Guider = (1 << 2),

    /// <summary>
    /// Focuser interface, must subclass INDI::FocuserInterface.
    /// </summary>
    Focuser = (1 << 3),

    /// <summary>
    /// Filter interface, must subclass INDI::FilterInterface.
    /// </summary>
    Filter = (1 << 4),

    /// <summary>
    /// Dome interface, must subclass INDI::Dome.
    /// </summary>
    Dome = (1 << 5),

    /// <summary>
    /// GPS interface, must subclass INDI::GPS.
    /// </summary>
    GPS = (1 << 6),

    /// <summary>
    /// Weather interface, must subclass INDI::Weather.
    /// </summary>
    Weather = (1 << 7),

    /// <summary>
    /// Adaptive Optics Interface.
    /// </summary>
    AO = (1 << 8),

    /// <summary>
    /// Dust Cap Interface.
    /// </summary>
    Dustcap = (1 << 9),

    /// <summary>
    /// Light Box Interface.
    /// </summary>
    Lightbox = (1 << 10),

    /// <summary>
    /// Detector interface, must subclass INDI::Detector.
    /// </summary>
    Detector = (1 << 11),

    /// <summary>
    /// Rotator interface, must subclass INDI::RotatorInterface.
    /// </summary>
    Rotator = (1 << 12),

    /// <summary>
    /// Spectrograph interface.
    /// </summary>
    Spectrograph = (1 << 13),

    /// <summary>
    /// Correlators (interferometers) interface.
    /// </summary>
    Correlator = (1 << 14),

    /// <summary>
    /// Auxiliary interface.
    /// </summary>
    Aux = (1 << 15),

    /// <summary>
    /// Digital Output (e.g. Relay) interface.
    /// </summary>
    Output = (1 << 16),

    /// <summary>
    /// Digital/Analog Input (e.g. GPIO) interface.
    /// </summary>
    Input = (1 << 17),

    /// <summary>
    /// Auxiliary interface.
    /// </summary>
    Power = (1 << 18),

    /// <summary>
    /// Sensor interface, combining Spectrograph, Detector, and Correlator interfaces.
    /// </summary>
    Sensor = Spectrograph | Detector | Correlator
}

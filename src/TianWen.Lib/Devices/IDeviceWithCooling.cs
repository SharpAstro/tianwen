namespace TianWen.Lib.Devices;

/// <summary>
/// Marker interface for camera devices that do NOT support CCD/sensor cooling (e.g. DSLRs).
/// Devices implementing this will have the setpoint control hidden in the session tab.
/// </summary>
public interface IUncooledCamera;

namespace TianWen.Lib.Devices;

public enum CameraState
{
    Idle = 0,
    Waiting = 1,
    Exposing = 2,
    Reading = 3,
    Download = 4,
    Error = 5,
    NotConnected = int.MaxValue
}
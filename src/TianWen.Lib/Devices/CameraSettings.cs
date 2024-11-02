namespace TianWen.Lib.Devices;

/// <summary>
/// Stores settings usually unchanged during Deep-Sky exposures.
/// </summary>
/// <param name="StartX"></param>
/// <param name="StartY"></param>
/// <param name="Width"></param>
/// <param name="Height"></param>
/// <param name="BinX"></param>
/// <param name="BinY"></param>
/// <param name="BitDepth"></param>
/// <param name="FastReadout"></param>
public readonly record struct CameraSettings(int StartX, int StartY, int Width, int Height, byte BinX, byte BinY, BitDepth BitDepth, bool FastReadout);
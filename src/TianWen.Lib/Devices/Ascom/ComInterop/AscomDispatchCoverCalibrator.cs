using System;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
[DispatchInterface]
internal sealed partial class AscomDispatchCoverCalibrator : IDisposable
{
    public AscomDispatchCoverCalibrator(DispatchObject dispatch)
    {
        _dispatch = dispatch;
    }

    public partial int MaxBrightness { get; }
    public partial int Brightness { get; }
    public partial int CoverState { get; }
    public partial int CalibratorState { get; }

    public partial void CalibratorOn(int Brightness);
    public partial void CalibratorOff();
    public partial void OpenCover();
    public partial void CloseCover();

    public void Dispose() { }
}

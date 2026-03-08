using System;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
[DispatchInterface]
internal sealed partial class AscomDispatchFocuser : IDisposable
{
    public AscomDispatchFocuser(DispatchObject dispatch)
    {
        _dispatch = dispatch;
    }

    public partial bool TempCompAvailable { get; }
    public partial bool Absolute { get; }
    public partial bool IsMoving { get; }
    public partial bool CanGetStepSize { get; }
    public partial bool TempComp { get; set; }
    public partial int Position { get; }
    public partial int MaxIncrement { get; }
    public partial int MaxStep { get; }
    public partial double StepSize { get; }
    public partial double Temperature { get; }

    public partial void Move(int Position);
    public partial void Halt();

    public void Dispose() { }
}

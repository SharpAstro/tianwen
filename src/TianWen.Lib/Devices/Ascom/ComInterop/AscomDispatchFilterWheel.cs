using System;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
[DispatchInterface]
internal sealed partial class AscomDispatchFilterWheel : IDisposable
{
    public AscomDispatchFilterWheel(DispatchObject dispatch)
    {
        _dispatch = dispatch;
    }

    public partial string[] Names { get; }
    public partial int[] FocusOffsets { get; }
    public partial short Position { get; set; }

    public void Dispose() { }
}

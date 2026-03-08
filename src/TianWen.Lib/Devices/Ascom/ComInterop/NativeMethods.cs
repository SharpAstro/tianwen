using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    internal static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    internal const int CLSCTX_ALL = 0x17; // CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER

    internal const int DISPATCH_METHOD = 0x1;
    internal const int DISPATCH_PROPERTYGET = 0x2;
    internal const int DISPATCH_PROPERTYPUT = 0x4;
    internal const int DISPATCH_PROPERTYPUTREF = 0x8;

    internal const int DISPID_PROPERTYPUT = -3;

    internal const int LOCALE_SYSTEM_DEFAULT = 0x0800;

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        int dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CLSIDFromProgID(
        string lpszProgID,
        out Guid lpclsid);

    [LibraryImport("oleaut32.dll")]
    internal static partial int VariantClear(nint pvarg);
}

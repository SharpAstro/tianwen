using System;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// The seam between an ASCOM dispatch wrapper and the COM object it drives. Two implementations:
/// <list type="bullet">
///   <item><see cref="DispatchObject"/> -- the in-proc raw-vtable IDispatch call (the local transport,
///     used for every driver that is CET-safe: native in-proc, comhost, or out-of-proc
///     <c>LocalServer32</c>).</item>
///   <item><see cref="RemoteDispatchTransport"/> -- the same typed surface projected over JSON-RPC to a
///     CET-off out-of-process host (<c>tianwen-ascomhost</c>), used only for the CET-incompatible
///     in-proc .NET Framework drivers (<c>InprocServer32 = mscoree.dll</c>) that otherwise fastfail our
///     CET-on process on connect (0xC0000409). See <see cref="AscomComServerClassifier"/> +
///     docs/plans/ascom-oop-host.md.</item>
/// </list>
/// The surface mirrors <see cref="DispatchObject"/> exactly, so the <c>[DispatchInterface]</c>
/// source generator and every hand-written wrapper are transport-agnostic -- they hold an
/// <see cref="IDispatchTransport"/> field and never know which side of the CET boundary the driver is on.
/// </summary>
[SupportedOSPlatform("windows")]
internal interface IDispatchTransport : IDisposable
{
    bool GetBool(string name);
    int GetInt(string name);
    short GetShort(string name);
    double GetDouble(string name);
    string GetString(string name);
    DateTime GetDateTime(string name);
    string[] GetStringArray(string name);
    int[] GetIntArray(string name);
    int[,] GetInt2DArray(string name);

    /// <summary>
    /// A camera's 2-D image property (<c>ImageArray</c>) as a frame channel, into <paramref name="recycled"/>
    /// when its shape matches. The default is the portable path, <see cref="GetInt2DArray"/> then the
    /// transpose, which the JSON-RPC transport keeps; the in-proc <see cref="DispatchObject"/> reads the
    /// SAFEARRAY straight into the plane instead (<see cref="SafeArrayMarshal.ToImageChannel"/>).
    /// </summary>
    Imaging.Channel GetImageChannel(string name, float[,]? recycled)
        => Imaging.Channel.FromWxHImageData(GetInt2DArray(name), recycled);
    object? GetObject(string name);

    void Set(string name, bool value);
    void Set(string name, int value);
    void Set(string name, short value);
    void Set(string name, double value);
    void Set(string name, string value);
    void Set(string name, DateTime value);

    void InvokeMethod(string name);
    void InvokeMethod(string name, params object[] args);
    bool InvokeMethodBool(string name, params object[] args);
    int InvokeMethodInt(string name, params object[] args);
    double InvokeMethodDouble(string name, params object[] args);
    object? InvokeMethodObject(string name, params object[] args);

    /// <summary>Invokes a method returning an IDispatch sub-object (e.g. telescope <c>AxisRates(axis)</c>).
    /// The caller owns and must dispose the returned transport.</summary>
    IDispatchTransport InvokeMethodDispatch(string name, params object[] args);

    /// <summary>Reads a parameterized property returning an IDispatch sub-object (e.g. an ASCOM
    /// collection's <c>Item(index)</c>). The caller owns and must dispose the returned transport.</summary>
    IDispatchTransport GetPropertyDispatch(string name, params object[] args);
}

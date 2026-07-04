using Shouldly;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TianWen.Lib.Devices.Ascom.ComInterop;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// Drives the typed <see cref="RemoteDispatchTransport"/> (the parent-side out-of-process transport)
/// against the real <c>tianwen-ascomhost</c> helper and a live <c>Scripting.Dictionary</c> COM object.
/// Proves the whole Phase-4 parent path: spawn + handshake + create + the typed get/set/invoke surface,
/// and -- critically -- that a COM failure comes back as a <see cref="COMException"/> carrying the
/// peer's HResult (the mechanism the driver's Platform-6 <c>catch (COMException) when (HResult == …)</c>
/// fallback relies on). Auto-skips off Windows or when the helper isn't built.
/// </summary>
[SupportedOSPlatform("windows")] // call sites are windows-only; the runtime SkipUnless guards execution
public class RemoteDispatchTransportTests
{
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);

    [Fact]
    public void GivenScriptingDictionaryWhenDrivenViaRemoteTransportThenTypedCallsRoundTrip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The ASCOM COM host is Windows-only.");
        Assert.SkipUnless(DispatchTransportFactory.TryLocateHelper(out var exe), "Skipped unless tianwen-ascomhost.exe has been built.");

        using var transport = RemoteDispatchTransport.Create(exe, "Scripting.Dictionary", Handshake);

        // getInt: a fresh dictionary is empty
        transport.GetInt("Count").ShouldBe(0);

        // invoke (void, with args): Add(key, item)
        transport.InvokeMethod("Add", "alpha", 1);
        transport.InvokeMethod("Add", "beta", 2);
        transport.GetInt("Count").ShouldBe(2);

        // invokeBool: Exists(key)
        transport.InvokeMethodBool("Exists", "alpha").ShouldBeTrue();
        transport.InvokeMethodBool("Exists", "missing").ShouldBeFalse();

        // Error propagation: setting CompareMode once the dictionary holds items is an illegal COM call.
        // It must surface as a COMException carrying the peer's HResult, not a generic error.
        var ex = Should.Throw<COMException>(() => transport.Set("CompareMode", 1));
        ex.HResult.ShouldNotBe(0);
    }

    [Fact]
    public void GivenANativeInProcComServerWhenClassifyingThenItStaysInProc()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Registry classification is Windows-only.");

        // Scripting.Dictionary is a native (scrrun.dll) in-proc server -- CET-safe, must NOT be routed
        // to the out-of-process host.
        AscomComServerClassifier.RequiresOutOfProcessHost("Scripting.Dictionary", out var reason).ShouldBeFalse();
        reason.ShouldNotBeNullOrEmpty();
    }
}

using Shouldly;
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using TianWen.Lib.Devices.Ascom.ComInterop;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// End-to-end proof of the out-of-process ASCOM COM host at the raw wire level: <see cref="AscomHostProcess"/>
/// spawns the real <c>tianwen-ascomhost.exe</c>, creates the named-pipe server, waits for the helper to
/// connect, and round-trips <c>create</c> / <c>getInt</c> / <c>setInt</c> / <c>release</c> against a real
/// COM object. Uses <c>Scripting.Dictionary</c> (scrrun.dll, present on every Windows install) as a
/// benign always-available <c>IDispatch</c>, so this exercises the whole transport + the raw-vtable
/// <c>DispatchObject</c> against genuine COM without needing an ASCOM Platform install or hardware.
/// <para>Auto-skips off Windows or when the helper exe hasn't been built, so a bare <c>dotnet test</c>
/// stays green.</para>
/// </summary>
[SupportedOSPlatform("windows")] // AscomHostProcess is windows-only; the runtime SkipUnless guards execution
public class AscomHostProcessTests
{
    [Fact]
    public void GivenTheBuiltHelperWhenDrivingRawJsonRpcThenCreateGetSetReleaseRoundTrip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The ASCOM COM host is Windows-only.");
        Assert.SkipUnless(DispatchTransportFactory.TryLocateHelper(out var exe), "Skipped unless tianwen-ascomhost.exe has been built.");

        using var host = AscomHostProcess.Spawn(exe, TimeSpan.FromSeconds(10));

        // create Scripting.Dictionary -> handle
        int handle;
        using (var r = host.Call("create", w =>
        {
            w.WriteStartArray();
            w.WriteStringValue("Scripting.Dictionary");
            w.WriteEndArray();
        }))
        {
            handle = r.RootElement.GetProperty("result").GetInt32();
        }
        handle.ShouldBeGreaterThan(0);

        // getInt Count -> 0 (a freshly created dictionary is empty)
        using (var r = host.Call("getInt", w => WriteHandleName(w, handle, "Count")))
        {
            r.RootElement.GetProperty("result").GetInt32().ShouldBe(0);
        }

        // setInt CompareMode = 1 then getInt CompareMode -> 1 (settable while the dictionary is empty)
        using (host.Call("setInt", w =>
        {
            w.WriteStartArray();
            w.WriteNumberValue(handle);
            w.WriteStringValue("CompareMode");
            w.WriteNumberValue(1);
            w.WriteEndArray();
        })) { }

        using (var r = host.Call("getInt", w => WriteHandleName(w, handle, "CompareMode")))
        {
            r.RootElement.GetProperty("result").GetInt32().ShouldBe(1);
        }

        // release -> void (result:null)
        using (var r = host.Call("release", w =>
        {
            w.WriteStartArray();
            w.WriteNumberValue(handle);
            w.WriteEndArray();
        }))
        {
            r.RootElement.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    private static void WriteHandleName(Utf8JsonWriter w, int handle, string name)
    {
        w.WriteStartArray();
        w.WriteNumberValue(handle);
        w.WriteStringValue(name);
        w.WriteEndArray();
    }
}

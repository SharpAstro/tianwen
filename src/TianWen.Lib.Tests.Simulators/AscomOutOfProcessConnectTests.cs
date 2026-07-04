using Shouldly;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TianWen.Lib.Devices.Ascom.ComInterop;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// "No plan survives contact with the enemy." Drives the out-of-process host against the REAL ASCOM COM
/// drivers registered on this machine -- most importantly <c>ASCOM.GeminiFPLite.CoverCalibrator</c>, the
/// in-proc .NET Framework driver whose <c>Connected=true</c> DoEvents busy-spin fastfails our CET-on
/// process (0xC0000409). The DoEvents spin runs during connect regardless of whether the panel hardware
/// answers, so this reproduces the crash trigger without the physical panel.
/// <para>All tests auto-skip when the relevant driver / the helper exe is absent, so they never break a
/// bare <c>dotnet test</c> on a box without these drivers.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class AscomOutOfProcessConnectTests(ITestOutputHelper output)
{
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);

    private static bool Registered(string progId) => NativeMethods.CLSIDFromProgID(progId, out _) == 0;

    [Theory]
    [InlineData("ASCOM.GeminiFPLite.CoverCalibrator", true)]   // mscoree v4 -> out-of-process (the crasher)
    [InlineData("ASCOM.GeminiFocuserPro.Focuser", true)]        // mscoree v4 -> out-of-process
    [InlineData("ASCOM.DeepSkyDad.FP.CoverCalibrator1", true)]  // mscoree v4 -> out-of-process
    [InlineData("ASCOM.iOptron2017.Telescope", true)]           // mscoree v4 -> out-of-process
    [InlineData("ASCOM.Simulator.Telescope", false)]            // LocalServer32 (OmniSim proxy) -> in-proc
    [InlineData("ASCOM.GS.Sky.Telescope", false)]               // LocalServer32 (GS Server) -> in-proc
    [InlineData("CCDSimulator.Camera", false)]                  // native in-proc (CCDSimulator.dll) -> in-proc
    public void GivenARealRegisteredDriverWhenClassifyingThenRoutesCorrectly(string progId, bool expectedOutOfProcess)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Registry classification is Windows-only.");
        Assert.SkipUnless(Registered(progId), $"Skipped unless {progId} is registered on this machine.");

        var isOop = AscomComServerClassifier.RequiresOutOfProcessHost(progId, out var reason);
        output.WriteLine($"{progId} -> out-of-process={isOop} ({reason})");

        isOop.ShouldBe(expectedOutOfProcess);
    }

    [Fact(Timeout = 60_000)]
    public void GivenTheRealGeminiFlatPanelWhenConnectedThroughTheHelperThenTheProcessSurvives()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ASCOM COM is Windows-only.");
        const string progId = "ASCOM.GeminiFPLite.CoverCalibrator";
        Assert.SkipUnless(Registered(progId), $"Skipped unless {progId} is registered.");
        Assert.SkipUnless(DispatchTransportFactory.TryLocateHelper(out _), "Skipped unless tianwen-ascomhost.exe has been built.");

        // Sanity: this driver must route out-of-process, else the connect below would fastfail us in-proc.
        AscomComServerClassifier.RequiresOutOfProcessHost(progId, out _).ShouldBeTrue();

        // Ctor routes through DispatchTransportFactory -> spawns the CET-off helper and creates the COM
        // object there. (sp=null: routing works without a logger; we just don't emit the routing line.)
        using var device = new AscomDispatchDevice(progId, serviceProvider: null);

        bool connected = false;
        Exception? caught = null;
        try
        {
            // The enemy: the Connected setter's ~1.1 s Application.DoEvents() busy-spin -- the exact CET
            // shadow-stack tripwire. In-proc this fastfailed the process (0xC0000409); in the CET-off
            // helper it must run to completion and return (or fail with a catchable COM/serial error,
            // since the panel isn't attached).
            try
            {
                device.Connect(); // Platform 7
            }
            catch (COMException ex) when (ex.HResult == DISP_E_UNKNOWNNAME)
            {
                device.Connected = true; // Platform 6 legacy path (this is what busy-spins DoEvents)
            }

            connected = device.Connected;
        }
        catch (Exception ex)
        {
            caught = ex; // a catchable failure (no hardware) is a PASS -- the point is we weren't fastfailed
        }
        finally
        {
            try { device.Connected = false; } catch { /* best effort */ }
        }

        output.WriteLine($"Gemini FlatPanel via helper: connected={connected}, caught={caught?.GetType().Name}: {caught?.Message}");

        // Reaching here at all is the proof: the DoEvents busy-spin ran in the CET-off helper without
        // fastfailing this process. A COMException (no panel) is expected and fine; a process crash would
        // never have let this assertion run.
        (connected || caught is COMException || caught is InvalidOperationException || caught is null)
            .ShouldBeTrue($"unexpected failure mode: {caught}");
    }
}

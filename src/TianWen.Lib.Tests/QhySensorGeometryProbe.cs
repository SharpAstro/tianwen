using System;
using System.Runtime.InteropServices;
using System.Text;
using QHYCCD.SDK;
using Xunit;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Tests;

/// <summary>
/// P1 of <c>docs/plans/sensor-active-area.md</c>: ask an ATTACHED QHY body for the three geometry
/// facts the codebase has had bindings for since the SDK was wrapped and has never once called.
/// <b>Measurement only.</b>
/// </summary>
/// <remarks>
/// <para>Gated on <c>TIANWEN_QHY_PROBE=1</c> AND a body being present, so a bare <c>dotnet test</c>
/// skips it. Opt in with the camera plugged in and nothing else holding it: the QHY SDK is exclusive,
/// so SharpCap or N.I.N.A. sitting on the camera makes this report no devices rather than fail.</para>
/// <para><b>It never SETS <c>CAM_IGNOREOVERSCAN_INTERFACE</c>, only asks whether it exists.</b>
/// Enabling it makes the driver hand back the cropped picture, which throws away the shielded
/// reference that P5 wants for a per-frame black level. Knowing the control is available is the
/// whole of what P1 needs; using it would decide P5 by accident.</para>
/// <para>Why this is worth a probe rather than a line in the driver: <c>QueryCapabilities</c> takes
/// <c>GetQHYCCDChipInfo</c>'s <c>imageW/imageH</c> as the sensor size, which is the FULL readout
/// including whatever margin the body ships, and <c>SetQHYCCDResolution(0, 0, w, h)</c> then asks
/// for all of it. If the effective area is a proper sub-rectangle of that, every frame this driver
/// has ever taken carries the margin, which is exactly what the archive's 2024 QHY set shows.</para>
/// </remarks>
public class QhySensorGeometryProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_QHY_PROBE";
    /// <summary>The SDK's own success code, which it declares without an access modifier.</summary>
    private const uint Success = 0;

    [Fact]
    public void ReportTheSensorGeometryTheDriverNeverAsksFor()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (needs a QHY body attached and free)");
        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            var count = ScanQHYCCD();
            output.WriteLine($"SDK v{GetSDKVersion()}; ScanQHYCCD reports {count} device(s)");
            Assert.SkipWhen(count is 0 or uint.MaxValue,
                "no QHY device found (is another application holding it? the SDK is exclusive)");

            for (var i = 0u; i < count; i++)
            {
                var id = new StringBuilder(64);
                if (GetQHYCCDId(i, id) is not Success)
                {
                    output.WriteLine($"[{i}] GetQHYCCDId failed");
                    continue;
                }

                var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
                try
                {
                    Probe(i, id.ToString(), idPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(idPtr);
                }
            }
        }
        finally
        {
            ReleaseQHYCCDResource();
        }
    }

    private void Probe(uint index, string id, IntPtr idPtr)
    {
        var handle = OpenQHYCCD(idPtr);
        if (handle == IntPtr.Zero)
        {
            output.WriteLine($"[{index}] {id}: OpenQHYCCD returned null");
            return;
        }

        try
        {
            if (InitQHYCCD(handle) is not Success)
            {
                output.WriteLine($"[{index}] {id}: InitQHYCCD failed");
                return;
            }

            output.WriteLine("");
            output.WriteLine($"[{index}] {id}");

            // The full readout, which is what the driver currently treats as the sensor.
            if (GetQHYCCDChipInfo(handle, out var chipW, out var chipH, out var imageW, out var imageH,
                    out var pixelW, out var pixelH, out var bpp) is Success)
            {
                output.WriteLine($"  chip        {chipW:F3} x {chipH:F3} mm, readout {imageW} x {imageH} px, "
                    + $"pixel {pixelW:F3} x {pixelH:F3} um, {bpp} bpp   <- what QueryCapabilities calls MaxWidth/MaxHeight");
            }
            else
            {
                output.WriteLine("  chip        GetQHYCCDChipInfo failed");
                imageW = imageH = 0;
            }

            // The picture: where the lit pixels start and how many there are.
            if (GetQHYCCDEffectiveArea(handle, out var ex, out var ey, out var ew, out var eh) is Success)
            {
                var proper = imageW > 0 && (ex > 0 || ey > 0 || ew < imageW || eh < imageH);
                output.WriteLine($"  effective   origin ({ex}, {ey}) size {ew} x {eh}"
                    + (proper
                        ? "   <- A PROPER SUB-RECTANGLE: every frame this driver takes carries a margin"
                        : "   (the whole readout; this body ships no margin, so P2 to P5 buy nothing here)"));
            }
            else
            {
                output.WriteLine("  effective   GetQHYCCDEffectiveArea REFUSED (not supported on this body)");
            }

            // The shielded strip, if the body exposes one separately.
            if (GetQHYCCDOverScanArea(handle, out var ox, out var oy, out var ow, out var oh) is Success)
            {
                output.WriteLine($"  overscan    origin ({ox}, {oy}) size {ow} x {oh}"
                    + (ow == 0 || oh == 0 ? "   (empty)" : ""));
            }
            else
            {
                output.WriteLine("  overscan    GetQHYCCDOverScanArea REFUSED (not supported on this body)");
            }

            // ASKED, never set. See the remarks.
            var ignore = IsQHYCCDControlAvailable(handle, CONTROL_ID.CAM_IGNOREOVERSCAN_INTERFACE);
            output.WriteLine($"  ignore-overscan control: {(ignore is Success ? "AVAILABLE" : "not available")}"
                + " (asked only, never set: setting it discards the black reference P5 wants)");
        }
        finally
        {
            CloseQHYCCD(handle);
        }
    }
}

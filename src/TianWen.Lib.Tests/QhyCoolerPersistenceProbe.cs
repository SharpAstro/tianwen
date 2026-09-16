using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xunit;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Tests;

/// <summary>
/// Does a cooler setpoint survive a close and re-open, on a body whose TEC has no power?
/// </summary>
/// <remarks>
/// <para>The DDR buffer does NOT survive: it reads 0 on every freshly opened handle, which is why
/// the driver sets it per connect. Whether the cooler behaves the same way matters for the opposite
/// reason. If a setpoint persists, then anything that connects to the camera is inheriting a target
/// somebody else chose, and a diagnostic path that writes cooling would be disturbing a session's
/// state rather than merely configuring its own.</para>
/// <para>The reading that prompted this: <c>CONTROL_COOLER</c> reports <b>-100</b> while declaring
/// its own range as -50 to 100, so the control carries an out-of-band sentinel (almost certainly
/// "off" or "manual") on top of a range that claims to be degrees Celsius. <c>DALCameraDriver</c>
/// maps BOTH <c>CoolerOn</c> and <c>TargetTemperature</c> onto that one control and models no
/// sentinel, so what -100 means is load-bearing and currently unstated.</para>
/// <para><b>This WRITES a cooler setpoint</b>, which is why it is gated separately on
/// <c>TIANWEN_QHY_COOLER_PROBE=1</c> rather than riding the general probe gate. Safe on an unpowered
/// body, where the TEC cannot engage; it restores whatever it found on the way out either way.</para>
/// </remarks>
public class QhyCoolerPersistenceProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_QHY_COOLER_PROBE";
    private const uint Success = 0;

    [Fact]
    public void ReportWhetherACoolerSetpointSurvivesAReopen()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (this one WRITES a cooler setpoint)");
        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            Assert.SkipWhen(ScanQHYCCD() is 0 or uint.MaxValue, "no QHY device found");
            var id = new StringBuilder(64);
            Assert.SkipUnless(GetQHYCCDId(0, id) is Success, "GetQHYCCDId failed");
            var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
            try
            {
                var before = Read(idPtr, "open 1, as found");
                Write(idPtr, -10, out var readBackImmediately);
                output.WriteLine($"  wrote -10, the same handle reads back {readBackImmediately:F2}");
                var after = Read(idPtr, "open 2, a FRESH handle after a close");
                output.WriteLine("");
                output.WriteLine(Math.Abs(after - (-10)) < 0.01
                    ? "PERSISTS: the setpoint survived the close, so anything that connects inherits it."
                    : $"DOES NOT PERSIST: a fresh handle reads {after:F2}, not the -10 that was written. "
                      + "Cooling has to be set per connect, exactly as the DDR buffer does.");
                Write(idPtr, before, out _);
                output.WriteLine($"restored the setpoint to what it was found at ({before:F2})");
            }
            finally
            {
                Marshal.FreeHGlobal(idPtr);
            }
        }
        finally
        {
            ReleaseQHYCCDResource();
        }
    }

    /// <summary>
    /// Warms the sensor back to ambient GRADUALLY and then releases the cooler. Run this ONCE, when
    /// the testing is finished.
    /// </summary>
    /// <remarks>
    /// <para><b>Never just disable the cooler on a cold sensor.</b> Cutting the TEC lets the chip
    /// snap back toward ambient as fast as its thermal mass allows, and that stress is what damages a
    /// sensor. So the setpoint is walked UP instead, and only released once the sensor is already
    /// near ambient and there is nothing left to snap.</para>
    /// <para>The rate mirrors what the session already does rather than inventing one:
    /// <c>Session.Cooling.cs</c> steps about 1 C on a fixed 15 second interval, the same as NINA.</para>
    /// <para>Deliberately NOT done at the end of every capture run. A setpoint survives a close on
    /// this body, so leaving it cold between runs is both safe and cheap, where warming and re-cooling
    /// each time would put the sensor through the cycle repeatedly for nothing.</para>
    /// </remarks>
    [Fact]
    public void WarmUpToAmbient()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (this one WRITES a cooler setpoint)");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_QHY_WARMUP") == "1",
            "TIANWEN_QHY_WARMUP is not 1 (run this ONCE, when the testing is done)");
        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            Assert.SkipWhen(ScanQHYCCD() is 0 or uint.MaxValue, "no QHY device found");
            var id = new StringBuilder(64);
            Assert.SkipUnless(GetQHYCCDId(0, id) is Success, "GetQHYCCDId failed");
            var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
            try
            {
                Warm(idPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(idPtr);
            }
        }
        finally
        {
            ReleaseQHYCCDResource();
        }
    }

    private void Warm(IntPtr idPtr)
    {
        var target = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_AMBIENT_C"), out var a) ? a : 16.0;
        var progress = Environment.GetEnvironmentVariable("TIANWEN_QHY_PROGRESS_LOG");
        var handle = OpenQHYCCD(idPtr);
        Assert.SkipWhen(handle == IntPtr.Zero, "OpenQHYCCD returned null");
        try
        {
            InitQHYCCD(handle);
            var temperature = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP);
            Log($"warming: sensor at {temperature:F1} C, setpoint {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER):F1}, "
                + $"walking up to {target:F1} C at 1 C per 15 s", progress);

            // Start from where the SENSOR is, not from the setpoint: if the setpoint was never
            // reached, stepping up from it would jump the sensor instead of walking it.
            var step = Math.Round(temperature);
            while (step < target)
            {
                step = Math.Min(step + 1, target);
                SetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER, step);
                Thread.Sleep(15000);
                temperature = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP);
                Log($"  setpoint {step,5:F1} C   sensor {temperature,6:F1} C   "
                    + $"pwm {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURPWM),5:F0}", progress);
            }

            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER, -100);
            Thread.Sleep(5000);
            Log($"cooler released; sensor {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP):F1} C, "
                + $"pwm {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURPWM):F0}. Safe to unplug.", progress);
        }
        finally
        {
            CloseQHYCCD(handle);
        }
    }

    private void Log(string line, string? progress)
    {
        output.WriteLine(line);
        if (progress is not null)
        {
            File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
    }

    private double Read(IntPtr idPtr, string label)
    {
        var handle = OpenQHYCCD(idPtr);
        try
        {
            InitQHYCCD(handle);
            var cooler = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER);
            var temperature = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP);
            var pwm = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURPWM);
            output.WriteLine($"{label,-40} COOLER {cooler,8:F2}   CURTEMP {temperature,7:F2}   CURPWM {pwm,7:F2}");
            return cooler;
        }
        finally
        {
            CloseQHYCCD(handle);
        }
    }

    private void Write(IntPtr idPtr, double setpoint, out double readBack)
    {
        var handle = OpenQHYCCD(idPtr);
        try
        {
            InitQHYCCD(handle);
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER, setpoint);
            readBack = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER);
        }
        finally
        {
            CloseQHYCCD(handle);
        }
    }
}

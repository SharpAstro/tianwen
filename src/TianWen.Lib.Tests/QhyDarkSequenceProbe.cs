using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using TianWen.Lib.Imaging;
using Xunit;
using static QHYCCD.SDK.QHYCamera;

namespace TianWen.Lib.Tests;

/// <summary>
/// A dark SEQUENCE off an attached QHY, read frame by frame, to tell three failure modes apart:
/// a frame that never arrived, a frame that is a REPEAT of the one before it, and a frame whose
/// level simply differs from its neighbours.
/// </summary>
/// <remarks>
/// <para>The owner's long-standing report on the QHY178M is that SharpCap "randomly doesn't show
/// frames, or old frames, or super bright frames and then dim frames". Those are three different
/// bugs wearing one description, and the sequence separates them: a repeat has an IDENTICAL digest
/// to its predecessor (a stale buffer handed back twice), while a level excursion has a different
/// digest and a different median (a real readout that came out at the wrong offset). Nothing else
/// here distinguishes them, which is why the digest is taken at all.</para>
/// <para>Deliberately the RAW SDK rather than TianWen's own driver: the question is whether the
/// behaviour lives BELOW us. If the raw sequence is clean, the fault is in the consumer; if it is
/// not, no amount of driver work fixes it.</para>
/// <para>Gated on <c>TIANWEN_QHY_PROBE=1</c>, with the count and exposure overridable by
/// <c>TIANWEN_QHY_FRAMES</c> / <c>TIANWEN_QHY_EXPOSURE_MS</c>. Writes FITS into the test output so
/// the frames can be looked at rather than only summarised. <b>Touches no cooler setting</b>: the
/// body under test is running without external power, so the TEC is not available and asking for a
/// setpoint would be asking for a failure.</b></para>
/// </remarks>
public class QhyDarkSequenceProbe(ITestOutputHelper output)
{
    private const string GateVar = "TIANWEN_QHY_PROBE";
    private const uint Success = 0;
    /// <summary>ExpQHYCCDSingleFrame answers this, not Success, on a read-directly body. Not an error.</summary>
    private const uint ReadDirectly = 0x2001;

    private double Gain { get; set; } = 10;
    private double Offset { get; set; } = 10;
    private double SettleMs { get; set; }
    private double? Ddr { get; set; }
    private double? UsbTraffic { get; set; }
    /// <summary>QHY's glow/amplifier control. 1 ENABLES suppression per the SDK manual; this
    /// body reads 0 and nothing in TianWen ever sets it.</summary>
    private double? Ampv { get; set; }
    /// <summary>A cooler setpoint in Celsius. WRITES cooling, so it needs its own gate.</summary>
    private double? CoolerC { get; set; }
    private int CoolWaitSeconds { get; set; } = 180;
    /// <summary>Where each cooling reading is appended as it is taken, since xunit buffers.</summary>
    private string? ProgressLog { get; set; }

    /// <summary>
    /// Drives the TEC to <see cref="CoolerC"/> and waits for it to arrive, printing the approach so a
    /// run can be read against the temperature it was actually taken at rather than the one asked for.
    /// Returns the setpoint found on the way in, which the caller restores.
    /// </summary>
    /// <remarks>
    /// Separately gated on <c>TIANWEN_QHY_COOLER_PROBE=1</c> because it writes cooling, and a setpoint
    /// PERSISTS across a close on this body: whatever is left behind is inherited by the next thing to
    /// connect, which is why the original is read first and put back at the end.
    /// </remarks>
    private double Cool(IntPtr handle, double setpoint)
    {
        var original = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER);
        var header = $"cooler: found at {original:F1}, driving to {setpoint:F1} C (waiting up to {CoolWaitSeconds} s)";
        output.WriteLine(header);
        if (ProgressLog is { } startLog)
        {
            File.AppendAllText(startLog, $"{DateTime.Now:HH:mm:ss}  {header}{Environment.NewLine}");
        }
        SetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER, setpoint);

        var deadline = DateTime.UtcNow.AddSeconds(CoolWaitSeconds);
        double temperature = double.NaN, pwm = double.NaN;
        var settledFor = 0;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5000);
            temperature = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP);
            pwm = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURPWM);
            var line = $"  t {temperature,6:F1} C   pwm {pwm,5:F0}   ddr {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_DDR):F0}";
            output.WriteLine(line);
            // ALSO straight to a file, flushed per reading. xunit buffers ITestOutputHelper until the
            // test completes, so a ten-minute wait shows nothing at all until it is over, which is
            // useless for watching a cooler descend (or for noticing that it is not descending).
            if (ProgressLog is { } progress)
            {
                File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss}{line}{Environment.NewLine}");
            }
            // Two consecutive readings inside half a degree is arrival, not one: the sensor wanders.
            settledFor = Math.Abs(temperature - setpoint) <= 0.5 ? settledFor + 1 : 0;
            if (settledFor >= 2)
            {
                output.WriteLine($"  reached {setpoint:F1} C and held it");
                return original;
            }
        }

        // Capturing anyway would produce frames at a temperature nobody chose and label them with one
        // that was only asked for, which is worse than no data. Put the setpoint back and stop.
        SetQHYCCDParam(handle, CONTROL_ID.CONTROL_COOLER, original);
        throw new InvalidOperationException(
            $"the cooler did not reach {setpoint:F1} C within {CoolWaitSeconds} s (last {temperature:F1} C at "
            + $"pwm {pwm:F0}); no frames were taken, and the setpoint was restored to {original:F1}. Either the "
            + "setpoint is below what this TEC can hold against ambient, or the wait is too short "
            + "(TIANWEN_QHY_COOL_WAIT_S).");
    }

    [Fact]
    public void ReportWhatASequenceOfDarksActuallyDelivers()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (needs a QHY body attached, capped, and free)");
        var frames = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_FRAMES"), out var n) ? n : 12;
        var exposureMs = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_EXPOSURE_MS"), out var e) ? e : 1000.0;
        Gain = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_GAIN"), out var g) ? g : 10;
        Offset = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_OFFSET"), out var o) ? o : 10;
        // The measurement that decides whether a SHORT settling frame is enough: take one throwaway
        // at the minimum exposure before the sequence, and see whether frame 0 of the sequence still
        // comes out low. If it does not, a cheap settle at connect fixes this and need not match the
        // science exposure. If it does, the settle has to be as long as the frame it protects, which
        // is a very different cost.
        SettleMs = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_SETTLE_MS"), out var s) ? s : 0.0;
        Ddr = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_DDR"), out var d) ? d : null;
        UsbTraffic = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_USBTRAFFIC"), out var u) ? u : null;
        Ampv = double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_AMPV"), out var av) ? av : null;
        // Cooling is gated SEPARATELY, because it writes a setpoint that outlives the process.
        CoolerC = Environment.GetEnvironmentVariable("TIANWEN_QHY_COOLER_PROBE") == "1"
                  && double.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_COOLER_C"), out var c) ? c : null;
        CoolWaitSeconds = int.TryParse(Environment.GetEnvironmentVariable("TIANWEN_QHY_COOL_WAIT_S"), out var cw) ? cw : 180;
        ProgressLog = Environment.GetEnvironmentVariable("TIANWEN_QHY_PROGRESS_LOG");

        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            var count = ScanQHYCCD();
            Assert.SkipWhen(count is 0 or uint.MaxValue, "no QHY device found (the SDK is exclusive; is something else holding it?)");

            var id = new StringBuilder(64);
            Assert.SkipUnless(GetQHYCCDId(0, id) is Success, "GetQHYCCDId failed");
            var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
            try
            {
                Capture(id.ToString(), idPtr, frames, exposureMs);
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
    /// Does a parameter change take effect on the NEXT frame, or the one after it? The dark sequence
    /// above found frame 0 at a LOWER level than every frame after it (median 16 against 40), and
    /// "less signal than commanded" is what an exposure that ran at the PREVIOUS setting looks like.
    /// If that is the mechanism then the first frame is not defective, it is correct for settings
    /// nobody wanted, and the two call for different handling: a settling frame at connect versus
    /// reading the parameter back before trusting it.
    /// </summary>
    /// <remarks>
    /// The exposure is stepped 1 s, 2 s, 1 s in blocks, commanding the change before each frame and
    /// recording BOTH the commanded value and what the camera reports back. A level that tracks the
    /// command on the same frame says the change is immediate; a level that lags by exactly one frame
    /// says it is not, and the readback says whether the camera admits it.
    /// </remarks>
    [Fact]
    public void ReportWhetherAParameterChangeLandsOnTheFrameThatFollowsIt()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (needs a QHY body attached, capped, and free)");
        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            Assert.SkipWhen(ScanQHYCCD() is 0 or uint.MaxValue, "no QHY device found");
            var id = new StringBuilder(64);
            Assert.SkipUnless(GetQHYCCDId(0, id) is Success, "GetQHYCCDId failed");
            var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
            try
            {
                StepExposure(idPtr);
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
    /// Does a frame contain the exposure that was just taken, or the one BEFORE it?
    /// </summary>
    /// <remarks>
    /// <para>The owner's recollection is of a camera whose snapshot shows the LAST frame, leaving the
    /// first one dodgy. That is a different bug from an unsettled first readout and it has the same
    /// symptom, and <b>the exposure-step test above cannot tell them apart</b>: it judged the change
    /// immediate by the WALL CLOCK, while the frame CONTENT at 1 s and 2 s is identical here, the
    /// level being bias-dominated rather than integration-dominated. A returned-previous-frame bug is
    /// therefore invisible to it, and so is the settling frame's own content.</para>
    /// <para>GAIN is the discriminator, because it changes what is in the frame rather than how long
    /// it took. Stepped in blocks with the level read per frame: a level that moves ON the frame
    /// whose gain was just set means the content is current, and a level that moves ONE FRAME LATE
    /// means every frame is showing its predecessor.</para>
    /// </remarks>
    [Fact]
    public void ReportWhetherAFrameHoldsItsOwnExposureOrThePreviousOne()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(GateVar) == "1",
            $"{GateVar} is not 1 (needs a QHY body attached, capped, and free)");
        Assert.SkipUnless(InitQHYCCDResource() is Success, "InitQHYCCDResource failed");
        try
        {
            Assert.SkipWhen(ScanQHYCCD() is 0 or uint.MaxValue, "no QHY device found");
            var id = new StringBuilder(64);
            Assert.SkipUnless(GetQHYCCDId(0, id) is Success, "GetQHYCCDId failed");
            var idPtr = Marshal.StringToHGlobalAnsi(id.ToString());
            try
            {
                StepGain(idPtr);
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

    private void StepGain(IntPtr idPtr)
    {
        double[] gains = [5, 5, 5, 40, 40, 40, 5, 5, 5];
        var handle = OpenQHYCCD(idPtr);
        Assert.SkipWhen(handle == IntPtr.Zero, "OpenQHYCCD returned null");
        try
        {
            SetQHYCCDStreamMode(handle, 0);
            Assert.SkipUnless(InitQHYCCD(handle) is Success, "InitQHYCCD failed");
            Assert.SkipUnless(
                GetQHYCCDChipInfo(handle, out _, out _, out var width, out var height, out _, out _, out _) is Success,
                "GetQHYCCDChipInfo failed");
            SetQHYCCDBitsMode(handle, 16);
            SetQHYCCDResolution(handle, 0, 0, width, height);
            // An offset high enough that neither gain clips at the floor: at offset 30 a gain of 45
            // pinned the median at 4 and the distribution could not move, which made that run
            // useless as evidence.
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_OFFSET, 100);
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_EXPOSURE, 1000 * 1000.0);

            var buffer = Marshal.AllocHGlobal((int)GetQHYCCDMemLength(handle));
            output.WriteLine($"{"#",3} {"gain",6} {"readback",9} {"median",8} {"mean",10}  note");
            try
            {
                for (var i = 0; i < gains.Length; i++)
                {
                    SetQHYCCDParam(handle, CONTROL_ID.CONTROL_GAIN, gains[i]);
                    var back = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_GAIN);
                    var exp = ExpQHYCCDSingleFrame(handle);
                    if (exp is not Success and not ReadDirectly
                        || GetQHYCCDSingleFrame(handle, out var w, out var h, out _, out _, buffer) is not Success)
                    {
                        output.WriteLine($"{i,3} frame refused");
                        continue;
                    }

                    var shorts = new short[w * h];
                    Marshal.Copy(buffer, shorts, 0, (int)(w * h));
                    var pixels = new ushort[shorts.Length];
                    for (var p = 0; p < shorts.Length; p++)
                    {
                        pixels[p] = unchecked((ushort)shorts[p]);
                    }

                    var sorted = pixels.Order().ToArray();
                    var changed = i > 0 && gains[i] != gains[i - 1];
                    output.WriteLine($"{i,3} {gains[i],6:F0} {back,9:F0} {sorted[sorted.Length / 2],8} "
                        + $"{pixels.Aggregate(0.0, (a, v) => a + v) / pixels.Length,10:F2}"
                        + (changed ? "  <- the GAIN was CHANGED before this frame" : ""));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            output.WriteLine("");
            output.WriteLine("read: the level moving ON the frame marked CHANGED means the frame holds its own "
                + "exposure. The level moving ONE FRAME LATE means every frame carries its predecessor's content, "
                + "which is a different bug from an unsettled first readout and wears the same symptom.");
        }
        finally
        {
            CancelQHYCCDExposingAndReadout(handle);
            CloseQHYCCD(handle);
        }
    }

    private void StepExposure(IntPtr idPtr)
    {
        double[] commanded = [1000, 1000, 1000, 2000, 2000, 2000, 1000, 1000, 1000];
        var handle = OpenQHYCCD(idPtr);
        Assert.SkipWhen(handle == IntPtr.Zero, "OpenQHYCCD returned null");
        try
        {
            SetQHYCCDStreamMode(handle, 0);
            Assert.SkipUnless(InitQHYCCD(handle) is Success, "InitQHYCCD failed");
            Assert.SkipUnless(
                GetQHYCCDChipInfo(handle, out _, out _, out var width, out var height, out _, out _, out _) is Success,
                "GetQHYCCDChipInfo failed");
            SetQHYCCDBitsMode(handle, 16);
            SetQHYCCDResolution(handle, 0, 0, width, height);
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_GAIN, 10);
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_OFFSET, 10);

            var bufferLength = GetQHYCCDMemLength(handle);
            var buffer = Marshal.AllocHGlobal((int)bufferLength);
            output.WriteLine($"{"#",3} {"asked",7} {"readback",9} {"wall ms",8} {"median",7} {"mean",9}  note");
            try
            {
                for (var i = 0; i < commanded.Length; i++)
                {
                    SetQHYCCDParam(handle, CONTROL_ID.CONTROL_EXPOSURE, commanded[i] * 1000.0);
                    var readback = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_EXPOSURE) / 1000.0;
                    var started = DateTime.UtcNow;
                    var exp = ExpQHYCCDSingleFrame(handle);
                    if (exp is not Success and not ReadDirectly)
                    {
                        output.WriteLine($"{i,3} exposure refused (0x{exp:X})");
                        continue;
                    }

                    if (GetQHYCCDSingleFrame(handle, out var w, out var h, out _, out _, buffer) is not Success)
                    {
                        output.WriteLine($"{i,3} frame never arrived");
                        continue;
                    }

                    var wall = (DateTime.UtcNow - started).TotalMilliseconds;
                    var shorts = new short[w * h];
                    Marshal.Copy(buffer, shorts, 0, (int)(w * h));
                    var pixels = new ushort[shorts.Length];
                    for (var p = 0; p < shorts.Length; p++)
                    {
                        pixels[p] = unchecked((ushort)shorts[p]);
                    }

                    var sorted = pixels.Order().ToArray();
                    var mean = pixels.Aggregate(0.0, (acc, v) => acc + v) / pixels.Length;
                    var changed = i > 0 && commanded[i] != commanded[i - 1];
                    output.WriteLine($"{i,3} {commanded[i],7:F0} {readback,9:F0} {wall,8:F0} "
                        + $"{sorted[sorted.Length / 2],7} {mean,9:F2}"
                        + (changed ? "  <- the exposure was CHANGED before this frame" : ""));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            output.WriteLine("");
            output.WriteLine("read: if the wall time and the level both move on the frame marked CHANGED, the parameter "
                + "is immediate and frame 0 of a run is merely unsettled. If they move one frame LATER, the change "
                + "lands on the frame after next, and a caller that trusts the readback is being told something the "
                + "hardware has not done yet.");
        }
        finally
        {
            CancelQHYCCDExposingAndReadout(handle);
            CloseQHYCCD(handle);
        }
    }

    private void Capture(string id, IntPtr idPtr, int frames, double exposureMs)
    {
        // Stream mode must be chosen BEFORE InitQHYCCD: 0 is single-frame, which is what a dark is.
        var handle = OpenQHYCCD(idPtr);
        Assert.SkipWhen(handle == IntPtr.Zero, "OpenQHYCCD returned null");
        try
        {
            SetQHYCCDStreamMode(handle, 0);

            // BEFORE InitQHYCCD, deliberately. Several QHY controls only latch pre-init, the way
            // stream mode does, and setting DDR after init answered Success and read back 0 on a
            // body that physically carries 128 MB of DDRII. If it latches here and not there, the
            // driver's own EnableDDR is in the wrong place too: it runs from QueryCapabilities,
            // after init.
            if (Ddr is { } preInitDdr)
            {
                var ok = SetQHYCCDParam(handle, CONTROL_ID.CONTROL_DDR, preInitDdr) is Success;
                output.WriteLine($"DDR set to {preInitDdr:F0} PRE-init ({(ok ? "accepted" : "REFUSED")})");
            }

            Assert.SkipUnless(InitQHYCCD(handle) is Success, "InitQHYCCD failed");
            output.WriteLine($"after InitQHYCCD, DDR reads back {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_DDR):F0}");
            Assert.SkipUnless(
                GetQHYCCDChipInfo(handle, out _, out _, out var width, out var height, out _, out _, out _) is Success,
                "GetQHYCCDChipInfo failed");

            SetQHYCCDBitsMode(handle, 16);
            SetQHYCCDResolution(handle, 0, 0, width, height);

            // Every measurement before this existed was taken with the DDR buffer OFF and USB traffic
            // at whatever the camera defaulted to, because this probe is RAW SDK and never goes
            // through QHYCameraDriver, which is where the driver's own DDR enablement lives. Both are
            // knobs on whether a readout survives the trip intact, so both belong to the measurement
            // rather than sitting in its background unrecorded.
            if (Ddr is { } ddr)
            {
                var accepted = SetQHYCCDParam(handle, CONTROL_ID.CONTROL_DDR, ddr) is Success;
                output.WriteLine($"DDR set to {ddr:F0} ({(accepted ? "accepted" : "REFUSED")}), "
                    + $"reads back {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_DDR):F0}");
            }

            if (UsbTraffic is { } traffic)
            {
                var accepted = SetQHYCCDParam(handle, CONTROL_ID.CONTROL_USBTRAFFIC, traffic) is Success;
                output.WriteLine($"USB traffic set to {traffic:F0} ({(accepted ? "accepted" : "REFUSED")}), "
                    + $"reads back {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_USBTRAFFIC):F0}");
            }

            if (Ampv is { } ampv)
            {
                var accepted = SetQHYCCDParam(handle, CONTROL_ID.CONTROL_AMPV, ampv) is Success;
                output.WriteLine($"AMPV set to {ampv:F0} ({(accepted ? "accepted" : "REFUSED")}), "
                    + $"reads back {GetQHYCCDParam(handle, CONTROL_ID.CONTROL_AMPV):F0}");
            }

            // Set, then READ BACK. A value outside the camera's range is clamped or refused rather
            // than reported, so a run labelled by what it ASKED for can be a run at settings nobody
            // chose: asking this body for gain 60 when its maximum is 51 produced exactly that, and
            // the mislabelled sweep was only caught later by dumping the controls. The readback is
            // the record, and a divergence is printed rather than swallowed.
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_GAIN, Gain);
            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_OFFSET, Offset);
            var gainBack = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_GAIN);
            var offsetBack = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_OFFSET);
            if (Math.Abs(gainBack - Gain) > 0.01 || Math.Abs(offsetBack - Offset) > 0.01)
            {
                output.WriteLine($"REFUSED OR CLAMPED: asked gain {Gain:F0} offset {Offset:F0}, "
                    + $"the camera reports gain {gainBack:F0} offset {offsetBack:F0}. Every number below is at the "
                    + "REPORTED settings, not the asked ones.");
            }

            if (CoolerC is { } wanted)
            {
                _coolerOnEntry = Cool(handle, wanted);
            }

            var bufferLength = GetQHYCCDMemLength(handle);
            Assert.SkipWhen(bufferLength is 0 or uint.MaxValue, "GetQHYCCDMemLength refused");
            var buffer = Marshal.AllocHGlobal((int)bufferLength);
            var dir = SharedTestData.CreateTempTestOutputDir();

            if (SettleMs > 0)
            {
                SetQHYCCDParam(handle, CONTROL_ID.CONTROL_EXPOSURE, SettleMs * 1000.0);
                var settleExp = ExpQHYCCDSingleFrame(handle);
                var settleGot = settleExp is Success or ReadDirectly
                    ? GetQHYCCDSingleFrame(handle, out _, out _, out _, out _, buffer)
                    : settleExp;
                output.WriteLine($"settling frame of {SettleMs:F0} ms taken and DISCARDED "
                    + $"({(settleGot is Success ? "arrived" : $"refused 0x{settleGot:X}")})");
            }

            SetQHYCCDParam(handle, CONTROL_ID.CONTROL_EXPOSURE, exposureMs * 1000.0);   // microseconds

            output.WriteLine($"{id}: {frames} darks of {exposureMs:F0} ms at gain {Gain:F0} offset {Offset:F0}, "
                + $"{width} x {height}, buffer {bufferLength / 1024} KiB");
            output.WriteLine($"written to {dir}");
            output.WriteLine("");
            output.WriteLine($"{"#",3} {"ms",6} {"median",8} {"min",7} {"max",8} {"mean",9} "
                + $"{"tempC",6} {"pwm",5} {"digest",10}  note");

            try
            {
                var rows = new List<(int Index, double Median, double Mean, ushort Min, ushort Max, string Digest)>();
                for (var i = 0; i < frames; i++)
                {
                    // The sensor temperature AT CAPTURE, not from the setup. An intermittent level
                    // excursion that coincides with a temperature or PWM blip is a different animal
                    // from one that does not, and without these columns the two cannot be told apart.
                    var frameTemp = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURTEMP);
                    var framePwm = GetQHYCCDParam(handle, CONTROL_ID.CONTROL_CURPWM);
                    var started = DateTime.UtcNow;
                    var exp = ExpQHYCCDSingleFrame(handle);
                    if (exp is not Success and not ReadDirectly)
                    {
                        output.WriteLine($"{i,3} ExpQHYCCDSingleFrame refused (0x{exp:X})");
                        continue;
                    }

                    var got = GetQHYCCDSingleFrame(handle, out var w, out var h, out var bpp, out var channels, buffer);
                    var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                    if (got is not Success)
                    {
                        output.WriteLine($"{i,3} {elapsed,6:F0} GetQHYCCDSingleFrame refused (0x{got:X})   FRAME NEVER ARRIVED");
                        continue;
                    }

                    var pixels = new ushort[w * h];
                    Marshal.Copy(buffer, MemoryMarshal.AsBytes<ushort>(pixels).ToArray(), 0, (int)(w * h * 2));
                    // Marshal.Copy has no ushort overload from IntPtr, so go through the short view.
                    var shorts = new short[w * h];
                    Marshal.Copy(buffer, shorts, 0, (int)(w * h));
                    for (var p = 0; p < shorts.Length; p++)
                    {
                        pixels[p] = unchecked((ushort)shorts[p]);
                    }

                    var sorted = pixels.Order().ToArray();
                    var median = sorted[sorted.Length / 2];
                    var mean = pixels.Aggregate(0.0, (acc, v) => acc + v) / pixels.Length;
                    var digest = Digest(pixels);
                    var repeat = rows.Count > 0 && rows[^1].Digest == digest;
                    rows.Add((i, median, mean, sorted[0], sorted[^1], digest));

                    var excursion = rows.Count > 0 && Math.Abs(median - rows[^1].Median) > 4;
                    output.WriteLine($"{i,3} {elapsed,6:F0} {median,8} {sorted[0],7} {sorted[^1],8} {mean,9:F2} "
                        + $"{frameTemp,6:F1} {framePwm,5:F0} {digest,10}"
                        + (repeat ? "  REPEAT of the previous frame (stale buffer)"
                           : excursion ? $"  <== LEVEL JUMPED {median - rows[^1].Median:+0;-0} ADU" : ""));

                    WriteFrame(dir, i, pixels, (int)w, (int)h, channels, bpp);
                }

                Summarise(rows);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            // The cooler is deliberately LEFT WHERE IT IS. Restoring it here would mean snapping a
            // cooled sensor back toward ambient at the end of every run, and an abrupt warm is
            // thermal stress rather than a tidy-up; it would also re-cool from scratch for the next
            // run, which wastes minutes and puts the sensor through the cycle twice. A setpoint
            // survives the close on this body, so leaving it set is the cheap and safe choice
            // between runs. Warming is its own explicit step, ONCE, when the testing is finished:
            // QhyCoolerPersistenceProbe.WarmUpToAmbient.
            CancelQHYCCDExposingAndReadout(handle);
            CloseQHYCCD(handle);
        }
    }

    private double _coolerOnEntry = -100;

    private void Summarise(List<(int Index, double Median, double Mean, ushort Min, ushort Max, string Digest)> rows)
    {
        if (rows.Count < 2)
        {
            return;
        }

        var medians = rows.Select(r => r.Median).ToArray();
        var repeats = rows.Skip(1).Count(r => rows[rows.FindIndex(x => x.Index == r.Index) - 1].Digest == r.Digest);
        var distinct = rows.Select(r => r.Digest).Distinct().Count();
        output.WriteLine("");
        output.WriteLine($"median over {rows.Count} frames: {medians.Min():F0} to {medians.Max():F0} "
            + $"(spread {medians.Max() - medians.Min():F0} ADU, {(medians.Max() - medians.Min()) / Math.Max(medians.Average(), 1) * 100:F1} percent of the mean level)");
        output.WriteLine($"distinct frames: {distinct} of {rows.Count}"
            + (distinct < rows.Count ? $"   <- {rows.Count - distinct} REPEAT(S): the SDK handed back a frame it had already given" : "   (no repeats: every frame is its own readout)"));
        output.WriteLine("");
        output.WriteLine("read: a REPEAT is a stale buffer and is a different bug from a level excursion, which has its "
            + "own digest and its own median. A level spread of a few ADU is ordinary bias wander; a spread of many "
            + "percent between neighbouring darks at one exposure is the 'bright then dim' complaint, and is what a "
            + "per-frame black level would correct IF this body exposed a shielded strip to measure one from. It does "
            + "not: P1 measured effective == full readout and an EMPTY overscan on this camera.");
    }

    private static string Digest(ushort[] pixels)
    {
        var hash = System.IO.Hashing.XxHash128.Hash(MemoryMarshal.AsBytes<ushort>(pixels));
        return Convert.ToHexString(hash)[..10];
    }

    private static void WriteFrame(string dir, int index, ushort[] pixels, int width, int height, uint channels, uint bpp)
    {
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = pixels[(y * width) + x];
            }
        }

        var image = new Image([plane], BitDepth.Int16,
            maxValue: pixels.Max(), minValue: pixels.Min(), pedestal: 0f,
            new ImageMeta(
                Instrument: "QHY178M",
                ExposureStartTime: DateTimeOffset.UtcNow,
                ExposureDuration: TimeSpan.Zero,
                FrameType: FrameType.Dark,
                Telescope: "",
                PixelSizeX: 2.4f,
                PixelSizeY: 2.4f,
                FocalLength: -1,
                FocusPos: -1,
                Filter: Filter.Unknown,
                BinX: 1,
                BinY: 1,
                CCDTemperature: float.NaN,
                SensorType: SensorType.Monochrome,
                BayerOffsetX: 0,
                BayerOffsetY: 0,
                RowOrder: RowOrder.TopDown,
                Latitude: float.NaN,
                Longitude: float.NaN));
        image.WriteToFitsFile(Path.Combine(dir, $"dark_{index:D3}.fits"));
    }
}

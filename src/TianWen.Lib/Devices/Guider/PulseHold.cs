using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// The hold of a host-timed guide pulse: the driver has started the motion, waits the pulse's length here, and ends the
/// motion when this returns or throws.
/// </summary>
/// <remarks>
/// <para><b>The pulse's timer is armed before the caller hears that the pulse is commanded</b> (#2330). The motion has
/// started, so the clock that ends it has to be running before anything else can run against that clock. A driver that
/// told its caller first and slept second left a gap: the caller, released, began its own wait for the pulse, and where
/// the driver's thread had lost the processor in between, the pulse was held from wherever the clock had got to by
/// then.</para>
/// <para>On the machine's clock that gap is the time a thread waits for a processor, and nobody sees it. On a clock a
/// test advances it is whatever the test advanced: the caller's wait is something parked, the pump moves the clock for
/// it, and the driver then holds the pulse a whole pulse length from there. A calibration's Dec pulse of 750 ms ran
/// for several times that, the star moved 7 px where 1 px was meant, and it left the frame
/// (<c>GuiderCalibrationOnFakeGuideCameraTests</c>, once on CI; reproduced by holding the driver's thread 40 ms in the
/// gap).</para>
/// </remarks>
internal static class PulseHold
{
    /// <summary>
    /// Arms the pulse's timer, then tells the caller the pulse is commanded, then waits the timer out.
    /// </summary>
    /// <param name="commanded">Completed here, never left for the caller of this method to complete: the order is the point.</param>
    public static async ValueTask RunAsync(
        ITimeProvider timeProvider, TimeSpan duration, TaskCompletionSource commanded, CancellationToken cancellationToken)
    {
        ValueTask held;
        try
        {
            held = timeProvider.SleepAsync(duration, cancellationToken);
        }
        finally
        {
            // Told whether or not the timer could be armed (a cancelled token refuses it): the board HAS been commanded,
            // and ending the motion is the driver's finally around this call either way.
            commanded.TrySetResult();
        }

        await held;
    }
}

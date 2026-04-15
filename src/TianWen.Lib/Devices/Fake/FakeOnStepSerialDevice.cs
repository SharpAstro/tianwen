using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Fake serial device that replays OnStep / OnStepX firmware behaviour on top of
/// the LX200 base. Adds <c>:GU#</c> (bundled status word), <c>:Gm#</c> (hardware
/// pier-side), <c>:hP#</c>/<c>:hR#</c>/<c>:hQ#</c> (park / unpark / set-park with
/// 0/1 response codes and the realistic <c>p</c>→<c>I</c>→<c>P</c> state
/// transition), <c>:Te#</c>/<c>:Td#</c> (tracking on/off with response), and
/// <c>:TK#</c>/<c>:TS#</c> (King / Solar tracking rates).
/// </summary>
internal sealed class FakeOnStepSerialDevice : FakeMeadeLX200SerialDevice
{
    private readonly ITimeProvider _onStepTimeProvider;

    private enum ParkState { NotParked, Parking, Parked, Failed }

    // Park state lives in the subclass — the LX200 base has no concept of parked.
    // Volatile.Read/Write provide the fence; the field itself doesn't carry the
    // volatile keyword (passing a volatile field by ref is what triggers CS0420).
    private int _parkStateRaw = (int)ParkState.NotParked;
    private ITimer? _parkTimer;

    /// <summary>
    /// Fake time it takes the mount to "complete" parking. Short enough that the
    /// driver's 250 ms poll loop catches one or two <c>I</c> ticks before <c>P</c>.
    /// </summary>
    private static readonly TimeSpan ParkDuration = TimeSpan.FromMilliseconds(300);

    public FakeOnStepSerialDevice(ILogger logger, Encoding encoding, ITimeProvider timeProvider, double siteLatitude, double siteLongitude, bool isOpen)
        : base(logger, encoding, timeProvider, siteLatitude, siteLongitude, isOpen)
    {
        _onStepTimeProvider = timeProvider;
    }

    private ParkState CurrentParkState => (ParkState)Volatile.Read(ref _parkStateRaw);

    private void SetParkState(ParkState state) => Volatile.Write(ref _parkStateRaw, (int)state);

    protected override bool TryHandleExtensionCommand(string dataStr)
    {
        switch (dataStr)
        {
            case ":GU#":
                AppendStatusResponse();
                return true;

            case ":Gm#":
                _responseBuffer.Append(GetPierSideChar()).Append('#');
                return true;

            case ":hP#":
                BeginPark();
                _responseBuffer.Append('1');
                return true;

            case ":hR#":
                Unpark();
                _responseBuffer.Append('1');
                return true;

            case ":hQ#":
                // Set-park stores the current position as the new park position.
                // The fake doesn't model a configurable park position — accept and ack.
                _responseBuffer.Append('1');
                return true;

            case ":Te#":
                _isTracking = true;
                _responseBuffer.Append('1');
                return true;

            case ":Td#":
                _isTracking = false;
                _responseBuffer.Append('1');
                return true;

            case ":TS#":
                _trackingFrequency = 600; // 60.0 Hz solar rate
                return true;

            case ":TK#":
                _trackingFrequency = 601; // 60.136 Hz king rate (close enough to sidereal)
                return true;

            case ":GX44#":
                // Axis 1 (HA / RA) encoder counts at the output shaft.
                // HA axis in hours → degrees × StepsPerDegree.
                AppendEncoderCounts((long)Math.Round(_haAxisAngle * 15.0 * StepsPerDegree));
                return true;

            case ":GX45#":
                // Axis 2 (Dec) encoder counts.
                AppendEncoderCounts((long)Math.Round(_decAxisAngle * StepsPerDegree));
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Simulated drive ratio at the output (sky) axis — 11,378 steps per degree, matching
    /// a typical TeeSeek-class harmonic mount:
    /// <list type="bullet">
    ///   <item><description>NEMA 17 stepper @ 200 full-steps/rev × 256 microsteps = 51,200 microsteps/motor-rev</description></item>
    ///   <item><description>80:1 harmonic strain-wave reducer → 4,096,000 microsteps per 360° sky rotation</description></item>
    ///   <item><description>4,096,000 ÷ 360 ≈ <b>11,378 steps/°</b></description></item>
    /// </list>
    /// That gives ~0.32" per step at the sky — consistent with what real OnStep harmonic
    /// mounts report in their <c>:GXE6#</c> configuration query. Downstream users of
    /// <see cref="GetAxisPositionAsync"/> (e.g. neural-guider PE feature engineering)
    /// see realistic, monotonically changing encoder values.
    /// </summary>
    private const double StepsPerDegree = 11378.0;

    private void AppendEncoderCounts(long counts)
    {
        _responseBuffer.Append(counts.ToString(CultureInfo.InvariantCulture)).Append('#');
    }

    /// <summary>
    /// Appends the OnStep <c>:GU#</c> status string. Only the flags that the driver
    /// actually queries are emitted; this keeps the fake small while still
    /// exercising the case-sensitive Contains checks (e.g. <c>'n'</c> vs <c>'N'</c>).
    /// </summary>
    private void AppendStatusResponse()
    {
        // Tracking: 'n' = not tracking, omit otherwise.
        if (!_isTracking)
        {
            _responseBuffer.Append('n');
        }

        // Goto: 'N' = no goto in progress, omit while slewing.
        if (!_isSlewing)
        {
            _responseBuffer.Append('N');
        }

        // Park state: exactly one of p / I / P / F.
        _responseBuffer.Append(CurrentParkState switch
        {
            ParkState.NotParked => 'p',
            ParkState.Parking => 'I',
            ParkState.Parked => 'P',
            ParkState.Failed => 'F',
            _ => 'p'
        });

        _responseBuffer.Append('#');
    }

    private char GetPierSideChar()
    {
        if (CurrentParkState is ParkState.Parked or ParkState.Parking)
        {
            return 'N'; // none / unaligned / parked
        }

        return IsFlippedPierSide ? 'W' : 'E';
    }

    private void BeginPark()
    {
        // Stop any in-flight slew immediately (real OnStep does this too)
        _isSlewing = false;
        SetParkState(ParkState.Parking);

        // Schedule the transition to Parked. Fake-time-aware via _onStepTimeProvider:
        // when the driver calls TimeProvider.SleepAsync(250ms) in its poll loop,
        // FakeTimeProviderWrapper advances time and fires this timer.
        Interlocked.Exchange(ref _parkTimer, _onStepTimeProvider.CreateTimer(
            _ =>
            {
                lock (_lockObj)
                {
                    if (CurrentParkState is ParkState.Parking)
                    {
                        SetParkState(ParkState.Parked);
                        _isTracking = false; // parked mounts stop tracking
                    }
                }
            },
            state: null,
            dueTime: ParkDuration,
            period: Timeout.InfiniteTimeSpan))?.Dispose();
    }

    private void Unpark()
    {
        Interlocked.Exchange(ref _parkTimer, null)?.Dispose();
        SetParkState(ParkState.NotParked);
    }
}

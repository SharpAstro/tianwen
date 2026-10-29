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

    // Park state lives in the subclass: the LX200 base has no concept of parked.
    // Volatile.Read/Write provide the fence; the field itself doesn't carry the
    // volatile keyword (passing a volatile field by ref is what triggers CS0420).
    private int _parkStateRaw = (int)ParkState.NotParked;
    private ITimer? _parkTimer;

    /// <summary>
    /// Fake time it takes the mount to "complete" parking. Short enough that the
    /// driver's 250 ms poll loop catches one or two <c>I</c> ticks before <c>P</c>.
    /// </summary>
    private static readonly TimeSpan ParkDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>The sky directions a move-axis is running in (<c>e</c>, <c>w</c>, <c>n</c>, <c>s</c>), from <c>:Mx#</c> and <c>:Qx#</c>.</summary>
    internal System.Collections.Immutable.ImmutableHashSet<char> Moving { get; private set; } = [];

    /// <summary>The last <c>:RA</c> / <c>:RE</c> move rates, in degrees a second; NaN before any.</summary>
    internal double MoveRateRa { get; private set; } = double.NaN;

    internal double MoveRateDec { get; private set; } = double.NaN;

    /// <summary>See <see cref="DeviceQueryKey.StaleCommandBuffer"/>. Only ever read and written under <c>_lockObj</c>.</summary>
    private bool _staleCommandBuffer;

    public FakeOnStepSerialDevice(ILogger logger, Encoding encoding, ITimeProvider timeProvider, double siteLatitude, double siteLongitude, bool isOpen, bool staleCommandBuffer = false)
        : base(logger, encoding, timeProvider, siteLatitude, siteLongitude, isOpen)
    {
        _onStepTimeProvider = timeProvider;
        _staleCommandBuffer = staleCommandBuffer;
    }

    /// <summary>
    /// OnStepX parses <c>:Sd</c> and <c>:St</c> with <c>signPresent = true</c>, and <c>Convert::dmsToDouble</c>
    /// then refuses anything whose first character is not <c>+</c> or <c>-</c>: an unsigned northern value is
    /// answered <c>0</c>. See <c>docs/architecture/onstep-protocol.md</c>.
    /// </summary>
    protected override bool RequiresExplicitSign => true;

    /// <summary>
    /// OnStep answers <c>:GD#</c> as <c>sDD*MM:SS</c> (or <c>sDD*MM</c>), never with Meade's <c>\xdf</c> (#1898).
    /// </summary>
    protected override char DegreeSign => '*';

    private ParkState CurrentParkState => (ParkState)Volatile.Read(ref _parkStateRaw);

    private void SetParkState(ParkState state) => Volatile.Write(ref _parkStateRaw, (int)state);

    protected override bool TryHandleExtensionCommand(string dataStr)
    {
        if (_staleCommandBuffer)
        {
            // Whatever arrives is read as the tail of the stale command, and its '#' ends both. A real command swallowed
            // this way is answered with OnStep's '0' for an unknown command, which carries no '#'.
            _staleCommandBuffer = false;
            if (dataStr is not "#")
            {
                _responseBuffer.Append('0');
            }
            return true;
        }

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
                // The fake doesn't model a configurable park position; accept and ack.
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

            case ":Me#" or ":Mw#" or ":Mn#" or ":Ms#":
                // Recorded, not modelled: the fake's axes do not move under a move-axis.
                Moving = Moving.Add(dataStr[2]);
                return true;

            case ":Qe#" or ":Qw#" or ":Qn#" or ":Qs#":
                Moving = Moving.Remove(dataStr[2]);
                return true;

            // The clock, acknowledged as the Meade document has it (1 valid, 0 invalid) and with no "Updating Planetary
            // Data" lines after :SC. The fake's clock is the time provider's, so what is set does not change what it reads back.
            case [':', 'S', 'C', .., '#']:
                _responseBuffer.Append(DateTime.TryParseExact(dataStr[3..^1], "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? '1' : '0');
                return true;

            case [':', 'S', 'L', .., '#']:
                _responseBuffer.Append(TimeSpan.TryParseExact(dataStr[3..^1], "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out _) ? '1' : '0');
                return true;

            case [':', 'R', 'A' or 'E', .., '#'] when double.TryParse(dataStr[3..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var degPerSec):
                if (dataStr[2] is 'A')
                {
                    MoveRateRa = degPerSec;
                }
                else
                {
                    MoveRateDec = degPerSec;
                }
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
    /// Simulated drive ratio at the output (sky) axis: 11,378 steps per degree, matching
    /// a typical TeeSeek-class harmonic mount:
    /// <list type="bullet">
    ///   <item><description>NEMA 17 stepper @ 200 full-steps/rev × 256 microsteps = 51,200 microsteps/motor-rev</description></item>
    ///   <item><description>80:1 harmonic strain-wave reducer → 4,096,000 microsteps per 360° sky rotation</description></item>
    ///   <item><description>4,096,000 ÷ 360 ≈ <b>11,378 steps/°</b></description></item>
    /// </list>
    /// That gives ~0.32" per step at the sky: consistent with what real OnStep harmonic
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

using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Connections;
using static TianWen.Lib.Astrometry.Constants;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200SerialDevice: ISerialConnection
{
    private readonly AlignmentMode _alignmentMode = AlignmentMode.GermanPolar;
    private readonly Transform _transform;
    private readonly TimeProvider _timeProvider;
    private readonly int _alignmentStars = 0;
    private double _slewRate = 1.5d; // degrees per second
    private volatile bool _isTracking = false;
    private volatile bool _isSlewing = false;
    private volatile bool _highPrecision = false;
    private volatile int _trackingFrequency = 601; // 60.1 Hz * 10, sidereal tracking rate

    // Mount axis angles (German Equatorial Mount)
    // HA axis: 0 = pointing at meridian (home position), positive = west of meridian
    // DEC axis: angle from equator, at home position = site latitude (pointing at pole)
    private double _haAxisAngle;  // Hour angle axis position in hours
    private double _decAxisAngle; // Declination axis position in degrees

    private double _targetRa;
    private double _targetDec;
    private ITimer? _slewTimer;
    private ITimer? _trackingTimer;
    private long _lastTrackingTicks;
    private readonly ILogger _logger;

    // I/O properties
    private readonly StringBuilder _responseBuffer = new StringBuilder();
    private int _responsePointer = 0;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly Lock _lockObj = new Lock();

    public FakeMeadeLX200SerialDevice(ILogger logger, Encoding encoding, TimeProvider timeProvider, double siteLatitude, double siteLongitude, bool isOpen)
    {
        _timeProvider = timeProvider;
        _transform = new Transform(timeProvider)
        {
            SiteElevation = 100,
            SiteLatitude = siteLatitude,
            SiteLongitude = siteLongitude
        };

        // Home position: mount pointing at celestial pole
        // HA axis at 0 (meridian), DEC axis at pole (90 or -90 depending on hemisphere)
        _haAxisAngle = 0;
        _decAxisAngle = siteLatitude >= 0 ? 90 : -90;

        // Initialize target to current position (pole)
        UpdateTransformFromAxisAngles();
        _targetRa = CurrentRA;
        _targetDec = CurrentDec;

        _lastTrackingTicks = timeProvider.GetTimestamp();

        _logger = logger;
        IsOpen = isOpen;
        Encoding = encoding;

        // Start tracking timer (updates position based on sidereal rate)
        StartTrackingTimer();
    }

    public bool IsOpen { get; private set; }

    public Encoding Encoding { get; private set; }

    /// <summary>
    /// Current Right Ascension calculated from hour angle axis and local sidereal time.
    /// RA = LST - HA (accounting for GEM pier side)
    /// </summary>
    private double CurrentRA
    {
        get
        {
            // For GEM, when DEC > 90 or < -90, we're on the "other side" of the pier
            var effectiveHA = _haAxisAngle;
            var effectiveDec = _decAxisAngle;

            // Normalize DEC axis to actual declination (handle pole crossing)
            if (_transform.SiteLatitude >= 0)
            {
                // Northern hemisphere
                if (_decAxisAngle > 90)
                {
                    effectiveDec = 180 - _decAxisAngle;
                    effectiveHA = _haAxisAngle + 12; // Flip HA by 12 hours
                }
            }
            else
            {
                // Southern hemisphere
                if (_decAxisAngle < -90)
                {
                    effectiveDec = -180 - _decAxisAngle;
                    effectiveHA = _haAxisAngle + 12;
                }
            }

            return ConditionRA(SiderealTime - effectiveHA);
        }
    }

    /// <summary>
    /// Current Declination from the DEC axis angle.
    /// </summary>
    private double CurrentDec
    {
        get
        {
            // Normalize DEC axis to actual declination
            if (_transform.SiteLatitude >= 0)
            {
                if (_decAxisAngle > 90)
                    return 180 - _decAxisAngle;
            }
            else
            {
                if (_decAxisAngle < -90)
                    return -180 - _decAxisAngle;
            }
            return _decAxisAngle;
        }
    }

    /// <summary>
    /// Updates the transform with current RA/DEC calculated from axis angles.
    /// </summary>
    private void UpdateTransformFromAxisAngles()
    {
        _transform.RefreshDateTimeFromTimeProvider();
        _transform.SetTopocentric(CurrentRA, CurrentDec);
    }

    private void StartTrackingTimer()
    {
        var period = TimeSpan.FromMilliseconds(50);
        _trackingTimer = _timeProvider.CreateTimer(TrackingTimerCallback, null, period, period);
    }

    /// <summary>
    /// Tracking timer callback - advances HA axis at sidereal rate when tracking is enabled.
    /// </summary>
    private void TrackingTimerCallback(object? state)
    {
        if (!IsOpen) return;

        var currentTicks = _timeProvider.GetTimestamp();
        var elapsedTicks = currentTicks - _lastTrackingTicks;
        _lastTrackingTicks = currentTicks;

        if (_isTracking && !_isSlewing)
        {
            var elapsedSeconds = (double)elapsedTicks / _timeProvider.TimestampFrequency;
            // Sidereal rate: Earth rotates 360° in ~23h56m = 15.041°/hour = 0.004178°/s
            // Tracking frequency 60.1 Hz is the standard sidereal rate
            // 1 sidereal day = 86164.0905 seconds, so rate = 24h / 86164.0905s = 0.0002778 h/s
            var siderealRateHoursPerSecond = 24.0 / 86164.0905;

            lock (_lockObj)
            {
                // Advance HA axis to compensate for Earth's rotation (track the object)
                // Note: HA increases as Earth rotates, so we add to keep pointing at same RA
                _haAxisAngle += siderealRateHoursPerSecond * elapsedSeconds;

                // Keep HA in reasonable range (-12 to +12 hours)
                if (_haAxisAngle > 12) _haAxisAngle -= 24;
                if (_haAxisAngle < -12) _haAxisAngle += 24;
            }
        }
    }

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    public void Dispose() => TryClose();

    public bool TryClose()
    {
        IsOpen = false;
        Interlocked.Exchange(ref _trackingTimer, null)?.Dispose();
        Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
        _responseBuffer.Clear();
        _responsePointer = 0;

        return true;
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        var messageStr = await TryReadExactlyAsync(message.Length, cancellationToken);
        if (messageStr is null)
        {
            return false;
        }

        return Encoding.GetBytes(messageStr, message.Span) == message.Length;
    }

    public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        if (_responsePointer + count <= _responseBuffer.Length)
        {
            var chars = new char[count];
            _responseBuffer.CopyTo(_responsePointer, chars, count);
            _responsePointer += count;
            ClearBufferIfEmpty();

            var message = new string(chars);

#if DEBUG
            _logger.LogTrace("<-- {Response} ({Length})", message.ReplaceNonPrintableWithHex(), message.Length);
#endif

            return ValueTask.FromResult<string?>(message);
        }

        return ValueTask.FromResult<string?>(null);
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var messageStr = await TryReadTerminatedAsync(terminators, cancellationToken);
        if (messageStr is null)
        {
            return -1;
        }

        return Encoding.GetBytes(messageStr, message.Span);
    }

    public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var chars = new char[_responseBuffer.Length - _responsePointer];
        var terminatorChars = Encoding.GetString(terminators.Span);

        int i = 0;
        while (_responsePointer < _responseBuffer.Length)
        {
            var @char = _responseBuffer[_responsePointer++];

            if (terminatorChars.Contains(@char))
            {
                ClearBufferIfEmpty();

                var message = new string(chars, 0, i);

#if DEBUG
                _logger.LogTrace("<-- {Response}", (message + @char).ReplaceNonPrintableWithHex());
#endif
                return ValueTask.FromResult<string?>(new string(chars, 0, i));
            }
            else
            {
                chars[i++] = @char;
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    private void ClearBufferIfEmpty()
    {
        if (_responsePointer == _responseBuffer.Length)
        {
            _responseBuffer.Clear();
            _responsePointer = 0;
        }
    }

    public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var dataStr = Encoding.GetString(data.Span);

#if DEBUG
        _logger.LogTrace("--> {Message}", dataStr.ReplaceNonPrintableWithHex());
#endif
        lock (_lockObj)
        {
            switch (dataStr)
            {
                case ":GVP#":
                    _responseBuffer.Append("Fake LX200 Mount#");
                    break;

                case ":GW#":
                    _responseBuffer.AppendFormat("{0}{1}{2:0}",
                        _alignmentMode switch { AlignmentMode.GermanPolar => 'G', _ => '?' },
                        _isTracking ? 'T' : 'N',
                        _alignmentStars
                    );
                    break;

                case ":AL#":
                    _isTracking = false;
                    break;

                case ":AP#":
                    _isTracking = true;
                    break;

                case ":GVN#":
                    _responseBuffer.Append("A4s4#");
                    break;

                case ":GR#":
                    RespondHMS(CurrentRA);
                    break;

                case ":Gr#":
                    RespondHMS(_targetRa);
                    break;

                case ":GD#":
                    RespondDMS(CurrentDec);
                    break;

                case ":Gd#":
                    RespondDMS(_targetDec);
                    break;

                case ":GS#":
                    _responseBuffer.AppendFormat("{0}#", HoursToHMS(SiderealTime, withFrac: false));
                    break;

                case ":Gt#":
                    _responseBuffer.AppendFormat("{0}#", DegreesToDM(_transform.SiteLatitude));
                    break;

                case ":GT#":
                    var (trackingHz, tracking10thHz) = Math.DivRem(_trackingFrequency, 10);
                    _responseBuffer.AppendFormat("{0:00}.{1:0}#", trackingHz, tracking10thHz);
                    break;

                case ":U#":
                    _highPrecision = !_highPrecision;
                    break;

                case ":MS#":
                    _responseBuffer.Append(SlewToTarget());
                    break;

                case ":D#":
                    _responseBuffer.Append(_isSlewing ? "\x7f#" : "#");
                    break;

                default:
                    if (dataStr.StartsWith(":Sr", StringComparison.Ordinal))
                    {
                        _responseBuffer.Append(ParseTargetRa(dataStr) ? '1' : '0');
                    }
                    else if (dataStr.StartsWith(":Sd", StringComparison.Ordinal))
                    {
                        _responseBuffer.Append(ParseTargetDec(dataStr) ? '1' : '0');
                    }
                    else
                    {
                        return ValueTask.FromResult(false);
                    }
                    break;
            }
        }
        return ValueTask.FromResult(true);

        void RespondHMS(double ra) => _responseBuffer.AppendFormat("{0}#",
            _highPrecision ? HoursToHMS(ra, withFrac: false) : HoursToHMT(ra));

        void RespondDMS(double dec) => _responseBuffer.AppendFormat("{0}#",
            _highPrecision ? DegreesToDMS(dec, withPlus: false, degreeSign: '\xdf', withFrac: false) : DegreesToDM(dec));
    }

    private double SiderealTime
    {
        get
        {
            _transform.RefreshDateTimeFromTimeProvider();
            return _transform.LocalSiderealTime;
        }
    }

    private static readonly Regex HMTParser = new Regex(@"^(\d{2}):(\d{2})[.](\d)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HMSParser = new Regex(@"^(\d{2}):(\d{2}):(\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private bool ParseTargetRa(string dataStr)
    {
        if (dataStr[^1] != '#')
        {
            return false;
        }

        var regex = _highPrecision ? HMSParser : HMTParser;
        var match = regex.Match(dataStr[3..^1]);
        if (match.Success
            && int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out var min)
            && int.TryParse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture, out var tenthMinOrSec)
            && hours is >= 0 and < 24
            && min is >= 0 and < 60
        )
        {
            if (_highPrecision)
            {
                if (tenthMinOrSec is >= 0 and < 60)
                {
                    var ra = HMSToHours($"{hours}:{min}:{tenthMinOrSec}");
                    if (!double.IsNaN(ra))
                    {
                        _targetRa = ra;
                        return true;
                    }
                }
            }
            else
            {
                if (tenthMinOrSec is >= 0 and < 10)
                {
                    var ra = HMSToHours($"{hours}:{min}:{tenthMinOrSec * 6}");
                    if (!double.IsNaN(ra))
                    {
                        _targetRa = ra;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static readonly Regex DMParser = new Regex(@"^([-]?\d{2})[\xdf*\xb0](\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DMSParser = new Regex(@"^([-]?\d{2})[\xdf*\xb0](\d{2}):(\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private bool ParseTargetDec(string dataStr)
    {
        if (dataStr[^1] != '#')
        {
            return false;
        }

        var regex = _highPrecision ? DMSParser : DMParser;
        var match = regex.Match(dataStr[3..^1]);
        if (match.Success
            && int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var deg)
            && int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out var min)
            && Math.Abs(deg) <= 90
            && min is >= 0 and < 60
        )
        {
            if (_highPrecision)
            {
                if (match.Groups.Count == 4 && int.TryParse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture, out var sec)
                    && sec is >= 0 and < 60)
                {
                    var dec = DMSToDegree($"{deg}:{min}:{sec}");
                    if (!double.IsNaN(dec))
                    {
                        _targetDec = dec;
                        return true;
                    }
                }
            }
            else
            {
                var dec = DMSToDegree($"{deg}:{min}");
                if (!double.IsNaN(dec))
                {
                    _targetDec = dec;
                    return true;
                }
            }
        }

        return false;
    }

    private char SlewToTarget()
    {
        _transform.RefreshDateTimeFromTimeProvider();

        var targetTransform = new Transform(_timeProvider)
        {
            DateTime = _transform.DateTime,
            SiteElevation = _transform.SiteElevation,
            SiteLatitude = _transform.SiteLatitude,
            SiteLongitude = _transform.SiteLongitude
        };
        targetTransform.SetTopocentric(_targetRa, _targetDec);

        if (targetTransform.ElevationTopocentric <= 0)
        {
            return '1';
        }

        _isTracking = true; // LX85 seems to start tracking on first slew, replicate this here
        _isSlewing = true;

        // Calculate target axis positions
        // Target HA = LST - Target RA
        var targetHA = ConditionHA(SiderealTime - _targetRa);
        var targetDecAxis = _targetDec;

        // For GEM, determine pier side and adjust DEC axis if needed
        // If target is west of meridian (HA > 0), mount points normally
        // If target is east of meridian (HA < 0), may need to flip
        var isNorthernHemisphere = _transform.SiteLatitude >= 0;
        if (isNorthernHemisphere && _targetDec > 0 && targetHA < -6)
        {
            // Flip: DEC axis goes past pole
            targetDecAxis = 180 - _targetDec;
            targetHA += 12;
        }
        else if (!isNorthernHemisphere && _targetDec < 0 && targetHA < -6)
        {
            targetDecAxis = -180 - _targetDec;
            targetHA += 12;
        }

        var period = TimeSpan.FromMilliseconds(100);

        var state = new SlewState(targetHA, targetDecAxis, _slewRate, period);

        var slewTimer = _timeProvider.CreateTimer(SlewTimerCallback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Interlocked.Exchange(ref _slewTimer, slewTimer)?.Dispose();

        slewTimer.Change(period, period);

        return '0';
    }

    /// <summary>
    /// Conditions hour angle to range -12 to +12 hours.
    /// </summary>
    private static double ConditionHA(double ha)
    {
        while (ha > 12) ha -= 24;
        while (ha < -12) ha += 24;
        return ha;
    }

    /// <summary>
    /// Callback from slew timer. Moves axis angles toward target positions.
    /// </summary>
    /// <param name="state">state is of type <see cref="SlewState"/></param>
    private void SlewTimerCallback(object? state)
    {
        if (state is SlewState slewState && IsOpen && _isSlewing)
        {
            var slewRateHoursPerPeriod = slewState.SlewRate * DEG2HOURS * slewState.Period.TotalSeconds;
            var slewRateDegreesPerPeriod = slewState.SlewRate * slewState.Period.TotalSeconds;
            bool isHAReached;
            bool isDecReached;

            lock (_lockObj)
            {
                var targetHA = slewState.TargetHAAxis;
                var targetDec = slewState.TargetDecAxis;

                // Determine slew direction
                var haDiff = targetHA - _haAxisAngle;
                var decDiff = targetDec - _decAxisAngle;

                // Take shortest path for HA (accounting for wrap-around)
                if (haDiff > 12) haDiff -= 24;
                if (haDiff < -12) haDiff += 24;

                var haStep = Math.Sign(haDiff) * Math.Min(Math.Abs(haDiff), slewRateHoursPerPeriod);
                var decStep = Math.Sign(decDiff) * Math.Min(Math.Abs(decDiff), slewRateDegreesPerPeriod);

                _haAxisAngle += haStep;
                _decAxisAngle += decStep;

                // Normalize HA axis
                if (_haAxisAngle > 12) _haAxisAngle -= 24;
                if (_haAxisAngle < -12) _haAxisAngle += 24;

                // Check if we've reached target
                isHAReached = Math.Abs(_haAxisAngle - targetHA) < 0.0001;
                isDecReached = Math.Abs(_decAxisAngle - targetDec) < 0.0001;

                if (isHAReached)
                {
                    _haAxisAngle = targetHA;
                }
                if (isDecReached)
                {
                    _decAxisAngle = targetDec;
                }

                if (isHAReached && isDecReached)
                {
                    _isSlewing = false;
                }

                // Update transform with new position
                UpdateTransformFromAxisAngles();
            }

            if (isHAReached && isDecReached)
            {
                Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
            }
        }
    }

    private static string DegreesToDM(double degrees)
    {
        var dms = DegreesToDMS(degrees, degreeSign: '\xdf', withPlus: false);

        return dms[..dms.LastIndexOf(':')];
    }

    private record SlewState(double TargetHAAxis, double TargetDecAxis, double SlewRate, TimeSpan Period);
}

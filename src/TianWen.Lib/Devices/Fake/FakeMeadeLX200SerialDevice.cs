using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Devices.Fake;

internal class FakeMeadeLX200SerialDevice: ISerialDevice
{
    private readonly AlignmentMode _alignmentMode = AlignmentMode.GermanPolar;
    private readonly Transform _transform;
    private readonly int _alignmentStars = 0;
    private double _slewRate = 1.5d; // degrees per second
    private bool _isTracking = false;
    private bool _isSlewing = false;
    private bool _highPrecision = false;
    private int _trackingFrequency = 601; // TODO simulate tracking and tracking rate
    private double _raAngle;
    private double _targetRa;
    private double _targetDec;
    private ITimer? _slewTimer;

    // I/O properties
    private readonly StringBuilder _responseBuffer = new StringBuilder();
    private int _responsePointer = 0;
    private readonly object _lockObj = new(); // FIXME: Change to lock when using C# 13

    public FakeMeadeLX200SerialDevice(bool isOpen, Encoding encoding, TimeProvider timeProvider, double siteLatitude, double siteLongitude)
    {
        _transform = new Transform(timeProvider)
        {
            SiteElevation = 100,
            SiteLatitude = siteLatitude,
            SiteLongitude = siteLongitude
        };
        _transform.SetTopocentric(SiderealTime, _transform.SiteLatitude is < 0 ? -90 : 90);

        _targetRa = _transform.RATopocentric;
        _targetDec = _transform.DECTopocentric;
        // should be 0
        _raAngle = CalcAngle24h(_transform.RATopocentric);

        IsOpen = isOpen;
        Encoding = encoding;
    }

    public bool IsOpen { get; private set; }

    public Encoding Encoding { get; private set; }

    public void Dispose() => TryClose();

    public bool TryClose()
    {
        lock (_lockObj)
        {
            IsOpen = false;
            _responseBuffer.Clear();
            _responsePointer = 0;

            return true;
        }
    }

    public bool TryReadExactly(int count, [NotNullWhen(true)] out ReadOnlySpan<byte> message)
    {
        lock (_lockObj)
        {
            if (_responsePointer + count <= _responseBuffer.Length)
            {
                var chars = new char[count];
                _responseBuffer.CopyTo(_responsePointer, chars, count);
                _responsePointer += count;

                ClearBufferIfEmpty();

                message = Encoding.GetBytes(chars);
                return true;
            }

            message = null;
            return false;
        }
    }

    public bool TryReadTerminated([NotNullWhen(true)] out ReadOnlySpan<byte> message, ReadOnlySpan<byte> terminators)
    {
        lock (_lockObj)
        {
            var chars = new char[_responseBuffer.Length - _responsePointer];
            var terminatorChars = Encoding.GetString(terminators);

            int i = 0;
            while (_responsePointer < _responseBuffer.Length)
            {
                var @char = _responseBuffer[_responsePointer++];

                if (terminatorChars.Contains(@char))
                {
                    ClearBufferIfEmpty();

                    message = Encoding.GetBytes(chars[0..i]);
                    return true;
                }
                else
                {
                    chars[i++] = @char;
                }
            }

            message = null;
            return false;
        }
    }

    private void ClearBufferIfEmpty()
    {
        if (_responsePointer == _responseBuffer.Length)
        {
            _responseBuffer.Clear();
            _responsePointer = 0;
        }
    }

    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        var dataStr = Encoding.GetString(data);

        lock (_lockObj)
        {
            switch (dataStr)
            {
                case ":GVP#":
                    _responseBuffer.Append("Fake LX200 Mount#");
                    return true;

                case ":GW#":
                    _responseBuffer.AppendFormat("{0}{1}{2:0}",
                        _alignmentMode switch { AlignmentMode.GermanPolar => 'G', _ => '?' },
                        _isTracking ? 'T' : 'N',
                        _alignmentStars
                    );
                    return true;

                case ":AL#":
                    _isTracking = false;
                    return true;

                case ":AP#":
                    _isTracking = true;
                    return true;

                case ":GVN#":
                    _responseBuffer.Append("A4s4#");
                    return true;

                case ":GR#":
                    RespondHMS(_transform.RATopocentric);
                    return true;

                case ":Gr#":
                    RespondHMS(_targetRa);
                    return true;

                case ":GD#":
                    RespondDMS(_transform.DECTopocentric);
                    return true;

                case ":Gd#":
                    RespondDMS(_targetDec);
                    return true;

                case ":GS#":
                    _responseBuffer.AppendFormat("{0}#", HoursToHMS(SiderealTime, withFrac: false));
                    return true;

                case ":Gt#":
                    _responseBuffer.AppendFormat("{0}#", DegreesToDM(_transform.SiteLatitude));
                    return true;

                case ":U#":
                    _highPrecision = !_highPrecision;
                    return true;

                case ":MS#":
                    _responseBuffer.Append(SlewToTarget());
                    return true;

                case ":D#":
                    _responseBuffer.Append(_isSlewing ? "\x7f#" : "#");
                    return true;

                default:
                    if (dataStr.StartsWith(":Sr", StringComparison.Ordinal))
                    {
                        _responseBuffer.Append(ParseTargetRa(dataStr) ? '1' : '0');
                        return true;
                    }
                    else if (dataStr.StartsWith(":Sd", StringComparison.Ordinal))
                    {
                        _responseBuffer.Append(ParseTargetDec(dataStr) ? '1' : '0');
                        return true;
                    }
                    return false;
            }
        }

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

    /// <summary>
    /// Calculates hour angle (24h format) given RA, or vice-versa.
    /// </summary>
    /// <param name="angle24h"></param>
    /// <returns></returns>
    private double CalcAngle24h(double angle24h) => ConditionRA(SiderealTime - angle24h);

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
        var timeProvider = _transform.TimeProvider;

        var targetTransform = new Transform(timeProvider)
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

        var hourAngleAtSlewTime = ConditionRA(_raAngle);
        var period = TimeSpan.FromMilliseconds(100);
        var state = new SlewSate(_transform.RATopocentric, _transform.DECTopocentric, _slewRate, hourAngleAtSlewTime, period);

        var slewTimer = timeProvider.CreateTimer(SlewTimerCallback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Interlocked.Exchange(ref _slewTimer, slewTimer)?.Dispose();

        slewTimer.Change(period, period);
        
        return '0';
    }

    /// <summary>
    /// Callback from slew timer.
    /// </summary>
    /// <param name="state">state is of type <see cref="SlewSate"/></param>
    private void SlewTimerCallback(object? state)
    {
        if (state is SlewSate slewState)
        {
            var slewRatePerPeriod = slewState.SlewRate * slewState.Period.TotalSeconds;

            lock (_lockObj)
            {
                _transform.RefreshDateTimeFromTimeProvider();
                // this is too simplistic, i.e. it does not respect the meridian

                var targetHourAngle = CalcAngle24h(_targetRa);
                var raDirPositive = targetHourAngle > slewState.HourAngleAtSlewTime;
                var decDirPositive = _targetDec > slewState.DecAtSlewTime;
                var raSlewRate = (raDirPositive ? DEG2HOURS : -DEG2HOURS) * slewRatePerPeriod;
                var decSlewRate = (decDirPositive ? 1 : -1) * slewRatePerPeriod;
                var ha24h = ConditionRA(_raAngle);
                var haNext = ha24h + raSlewRate;
                var decNext = _transform.DECTopocentric + decSlewRate;

                double haDiff = haNext - targetHourAngle;
                bool isRaReached = raDirPositive switch
                {
                    true => haNext >= targetHourAngle,
                    false => haNext <= targetHourAngle
                };

                var isDecReached = decDirPositive switch
                {
                    true => decNext >= _targetDec,
                    false => decNext <= _targetDec
                };

                var ra = CalcAngle24h(ConditionRA(haNext));
                var dec = Math.Min(90, Math.Max(decNext, -90));
                if (isRaReached && isDecReached)
                {
                    _transform.SetTopocentric(_targetRa, _targetDec);
                    _isSlewing = false;

                    Interlocked.Exchange(ref _slewTimer, null)?.Dispose();
                }
                else if (isRaReached)
                {
                    _transform.SetTopocentric(_targetRa, decNext);
                }
                else if (isDecReached)
                {
                    _transform.SetTopocentric(ra, _targetDec);
                }
                else
                {
                    _transform.SetTopocentric(ra, decNext);
                }

                _raAngle += raSlewRate - (isRaReached ? haDiff : 0);
            }
        }
    }

    private static string DegreesToDM(double degrees)
    {
        var dms = DegreesToDMS(degrees, degreeSign: '\xdf', withPlus: false);

        return dms[..dms.LastIndexOf(':')];
    }

    private record SlewSate(double RaAtSlewTime, double DecAtSlewTime, double SlewRate, double HourAngleAtSlewTime, TimeSpan Period);
}

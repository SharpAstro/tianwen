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
    private readonly int _alignmentStars = 0;
    private double _slewRate = 1.5d; // degrees per second
    private volatile bool _isTracking = false;
    private volatile bool _isSlewing = false;
    private volatile bool _highPrecision = false;
    private volatile int _trackingFrequency = 601; // TODO simulate tracking and tracking rate
    private double _raAngle;
    private double _targetRa;
    private double _targetDec;
    private ITimer? _slewTimer;
    private readonly ILogger _logger;

    // I/O properties
    private readonly StringBuilder _responseBuffer = new StringBuilder();
    private int _responsePointer = 0;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly Lock _lockObj = new Lock();

    public FakeMeadeLX200SerialDevice(ILogger logger, Encoding encoding, TimeProvider timeProvider, double siteLatitude, double siteLongitude, bool isOpen)
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

        _logger = logger;
        IsOpen = isOpen;
        Encoding = encoding;
    }

    public bool IsOpen { get; private set; }

    public Encoding Encoding { get; private set; }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public int Release() => _semaphore.Release();

    public void Dispose() => TryClose();

    public bool TryClose()
    {
        IsOpen = false;
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
                lock (_lockObj)
                {
                    RespondHMS(_transform.RATopocentric);
                }
                break;

            case ":Gr#":
                lock (_lockObj)
                {
                    RespondHMS(_targetRa);
                }
                break;

            case ":GD#":
                lock (_lockObj)
                {
                    RespondDMS(_transform.DECTopocentric);
                }
                break;

            case ":Gd#":
                lock (_lockObj)
                {
                    RespondDMS(_targetDec);
                }
                break;

            case ":GS#":
                lock (_lockObj)
                {
                    _responseBuffer.AppendFormat("{0}#", HoursToHMS(SiderealTime, withFrac: false));
                }
                break;

            case ":Gt#":
                lock (_lockObj)
                {
                    _responseBuffer.AppendFormat("{0}#", DegreesToDM(_transform.SiteLatitude));
                }
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
                lock (_lockObj)
                {
                    _responseBuffer.Append(_isSlewing ? "\x7f#" : "#");
                }
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
        if (state is SlewSate slewState && IsOpen && _isSlewing)
        {
            var slewRatePerPeriod = slewState.SlewRate * slewState.Period.TotalSeconds;
            bool isRaReached;
            bool isDecReached;

            lock (_lockObj)
            {
                _transform.RefreshDateTimeFromTimeProvider();
                var targetDec = _targetDec;
                var targetRa = _targetRa;
                var decTopo = _transform.DECTopocentric;
                var ha24h = ConditionRA(_raAngle);
                // this is too simplistic, i.e. it does not respect the meridian
                var targetHourAngle = CalcAngle24h(targetRa);
                var raDirPositive = targetHourAngle > slewState.HourAngleAtSlewTime;
                var decDirPositive = targetDec > slewState.DecAtSlewTime;
                var raSlewRate = (raDirPositive ? DEG2HOURS : -DEG2HOURS) * slewRatePerPeriod;
                var decSlewRate = (decDirPositive ? 1 : -1) * slewRatePerPeriod;
                var haNext = ha24h + raSlewRate;
                var decNext = decTopo + decSlewRate;

                double haDiff = haNext - targetHourAngle;
                isRaReached = raDirPositive switch
                {
                    true => haNext >= targetHourAngle,
                    false => haNext <= targetHourAngle
                };

                isDecReached = decDirPositive switch
                {
                    true => decNext >= targetDec,
                    false => decNext <= targetDec
                };

                var ra = CalcAngle24h(ConditionRA(haNext));
                var dec = Math.Min(90, Math.Max(decNext, -90));
                if (isRaReached && isDecReached)
                {
                    _transform.SetTopocentric(targetRa, targetDec);
                    _isSlewing = false;

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

            if (isRaReached && isDecReached)
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

    private record SlewSate(double RaAtSlewTime, double DecAtSlewTime, double SlewRate, double HourAngleAtSlewTime, TimeSpan Period);
}

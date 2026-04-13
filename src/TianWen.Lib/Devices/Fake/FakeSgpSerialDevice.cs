using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Fake serial device simulating the iOptron SkyGuider Pro protocol.
/// Responds to SGP commands with appropriate responses for testing.
/// </summary>
internal class FakeSgpSerialDevice : ISerialConnection
{
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly StringBuilder _responseBuffer = new();
    private int _responsePointer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _lockObj = new();

    // Mount state
    private bool _isNorthernHemisphere;
    private int _trackingRate = 3; // 0=solar, 1=lunar, 2=half-sidereal, 3=sidereal
    private int _speed = 0;
    private int _moveDirection = 2; // 0=east, 1=west, 2=stop
    private int _slewSpeed = 1;
    private int _guideRateRA = 50;
    private int _guideRateDec = 50;
    private int _eyepieceLight = 0;
    private string _firmwareVersion = "170518";

    // Camera snap state (units TBD — possibly seconds)
    private bool _cameraActive;
    private int _cameraShutter = 30;
    private int _cameraInterval = 5;
    private int _cameraShotCount = 2;

    // Tracking simulation
    private long _lastTrackingTicks;
    private double _haOffset; // accumulated RA offset in hours from tracking/movement
    private volatile bool _isTracking;
    private ITimer? _trackingTimer;

    public FakeSgpSerialDevice(ILogger logger, Encoding encoding, ITimeProvider timeProvider, bool isNorthernHemisphere, bool isOpen)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _isNorthernHemisphere = isNorthernHemisphere;
        _lastTrackingTicks = timeProvider.GetTimestamp();
        IsOpen = isOpen;
        Encoding = encoding;
        _isTracking = true;

        // Start tracking timer
        var period = TimeSpan.FromMilliseconds(50);
        _trackingTimer = _timeProvider.CreateTimer(TrackingTimerCallback, null, period, period);
    }

    public bool IsOpen { get; private set; }
    public Encoding Encoding { get; }

    private void TrackingTimerCallback(object? state)
    {
        if (!IsOpen) return;

        var currentTicks = _timeProvider.GetTimestamp();
        var elapsedTicks = currentTicks - _lastTrackingTicks;
        _lastTrackingTicks = currentTicks;

        if (_isTracking)
        {
            var elapsedSeconds = (double)elapsedTicks / _timeProvider.TimestampFrequency;

            lock (_lockObj)
            {
                if (_moveDirection is 0 or 1)
                {
                    // Moving: apply slew speed at sidereal rate × multiplier
                    const double sgpGuideRateDegPerSec = 15.0417 / 3600.0;
                    var speedMultiplier = _slewSpeed switch
                    {
                        1 => 1, 2 => 2, 3 => 8, 4 => 16, 5 => 64, 6 => 128, 7 => 144, _ => 1
                    };

                    var direction = _moveDirection == 1 ? 1.0 : -1.0;
                    // Convert deg/sec to hours/sec for HA offset
                    _haOffset += direction * speedMultiplier * sgpGuideRateDegPerSec / 15.0 * elapsedSeconds;
                }
            }
        }
    }

    public ValueTask<ResourceLock> WaitAsync(CancellationToken cancellationToken) => _semaphore.AcquireLockAsync(cancellationToken);

    public void Dispose() => TryClose();

    public bool TryClose()
    {
        IsOpen = false;
        Interlocked.Exchange(ref _trackingTimer, null)?.Dispose();
        _responseBuffer.Clear();
        _responsePointer = 0;
        return true;
    }

    public ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var dataStr = Encoding.GetString(data.Span);

#if DEBUG
        _logger.LogTrace("--> {Message}", dataStr);
#endif
        lock (_lockObj)
        {
            switch (dataStr)
            {
                case ":MRSVE#":
                    _responseBuffer.AppendFormat(":RMRVE12{0}#", _firmwareVersion);
                    break;

                case ":MGAS#":
                    _responseBuffer.AppendFormat(":HRAS01{0}{1}0{2}105#",
                        _trackingRate,
                        _speed,
                        _isNorthernHemisphere ? 1 : 0);
                    break;

                case ":MSHE0#":
                    _isNorthernHemisphere = false;
                    _responseBuffer.Append(":HRHE0#");
                    break;

                case ":MSHE1#":
                    _isNorthernHemisphere = true;
                    _responseBuffer.Append(":HRHE0#");
                    break;

                case ":MSTR0#":
                    _trackingRate = 0;
                    _responseBuffer.Append(":HRTR0#");
                    _isTracking = true;
                    break;

                case ":MSTR1#":
                    _trackingRate = 1;
                    _responseBuffer.Append(":HRTR0#");
                    _isTracking = true;
                    break;

                case ":MSTR2#":
                    _trackingRate = 2;
                    _responseBuffer.Append(":HRTR0#");
                    _isTracking = true;
                    break;

                case ":MSTR3#":
                    _trackingRate = 3;
                    _responseBuffer.Append(":HRTR0#");
                    _isTracking = true;
                    break;

                case ":MSMR0#":
                    _moveDirection = 0; // east
                    _responseBuffer.Append(":HRMR0#");
                    break;

                case ":MSMR1#":
                    _moveDirection = 1; // west
                    _responseBuffer.Append(":HRMR1#");
                    break;

                case ":MSMR2#":
                    _moveDirection = 2; // stop
                    _responseBuffer.Append(":HRMR2#");
                    break;

                default:
                    if (dataStr.StartsWith(":MSMS", StringComparison.Ordinal) && dataStr.Length >= 7)
                    {
                        // Set slew speed :MSMS{1-7}#
                        if (int.TryParse(dataStr.AsSpan(5, 1), CultureInfo.InvariantCulture, out var speed) && speed is >= 1 and <= 7)
                        {
                            _slewSpeed = speed;
                        }
                        _responseBuffer.Append(":HRMS0#");
                    }
                    else if (dataStr.StartsWith(":MSGR", StringComparison.Ordinal) && dataStr.Length >= 10)
                    {
                        // Set guiding rate :MSGR{nn}{nn}#
                        if (int.TryParse(dataStr.AsSpan(5, 2), CultureInfo.InvariantCulture, out var raRate))
                        {
                            _guideRateRA = raRate;
                        }
                        if (int.TryParse(dataStr.AsSpan(7, 2), CultureInfo.InvariantCulture, out var decRate))
                        {
                            _guideRateDec = decRate;
                        }
                        _responseBuffer.Append(":HRGR0#");
                    }
                    else if (dataStr == ":MGGR")
                    {
                        _responseBuffer.AppendFormat(":HRGR{0:D2}{1:D2}#", _guideRateRA, _guideRateDec);
                    }
                    else if (dataStr == ":MGCS#")
                    {
                        // :HRCSxyaaaabbbcccdddppkkkkk#
                        // x=0, y=flag, aaaa=shutter, bbb=interval, ccc=shots, rest=zeros
                        _responseBuffer.AppendFormat(":HRCS0{0}{1:D4}{2:D3}{3:D3}0000000000#",
                            _cameraActive ? 1 : 0, _cameraShutter, _cameraInterval, _cameraShotCount);
                    }
                    else if (dataStr.StartsWith(":MSCA", StringComparison.Ordinal) && dataStr.EndsWith('#'))
                    {
                        // Camera snap: :MSCA{y}{aaaa}{bbb}{ccc}#
                        // y=start flag (1), aaaa=shutter, bbb=interval, ccc=shot count
                        var payload = dataStr[5..^1];
                        if (payload.Length >= 11
                            && int.TryParse(payload.AsSpan(1, 4), CultureInfo.InvariantCulture, out var shutter)
                            && int.TryParse(payload.AsSpan(5, 3), CultureInfo.InvariantCulture, out var interval)
                            && int.TryParse(payload.AsSpan(8, 3), CultureInfo.InvariantCulture, out var shots))
                        {
                            _cameraActive = payload[0] == '1';
                            _cameraShutter = shutter;
                            _cameraInterval = interval;
                            _cameraShotCount = shots;
                        }
                        _responseBuffer.Append(":HRCA0#");
                    }
                    else if (dataStr == ":MGEL")
                    {
                        _responseBuffer.AppendFormat(":HREL{0}#", _eyepieceLight);
                    }
                    else if (dataStr.StartsWith(":MSEL", StringComparison.Ordinal) && dataStr.Length >= 7)
                    {
                        if (int.TryParse(dataStr.AsSpan(5, 1), CultureInfo.InvariantCulture, out var intensity) && intensity is >= 0 and <= 9)
                        {
                            _eyepieceLight = intensity;
                        }
                        _responseBuffer.AppendFormat(":HREL{0}#", _eyepieceLight);
                    }
                    else
                    {
                        return ValueTask.FromResult(false);
                    }
                    break;
            }
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask<string?> TryReadTerminatedAsync(ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var terminatorChars = Encoding.GetString(terminators.Span);
        var chars = new char[_responseBuffer.Length - _responsePointer];

        int i = 0;
        while (_responsePointer < _responseBuffer.Length)
        {
            var @char = _responseBuffer[_responsePointer++];

            if (terminatorChars.Contains(@char))
            {
                ClearBufferIfEmpty();
                var message = new string(chars, 0, i);
#if DEBUG
                _logger.LogTrace("<-- {Response}", message + @char);
#endif
                return ValueTask.FromResult<string?>(message);
            }
            else
            {
                chars[i++] = @char;
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask<string?> TryReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        if (_responsePointer + count <= _responseBuffer.Length)
        {
            var chars = new char[count];
            _responseBuffer.CopyTo(_responsePointer, chars, count);
            _responsePointer += count;
            ClearBufferIfEmpty();
            return ValueTask.FromResult<string?>(new string(chars));
        }

        return ValueTask.FromResult<string?>(null);
    }

    public async ValueTask<int> TryReadTerminatedRawAsync(Memory<byte> message, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken)
    {
        var messageStr = await TryReadTerminatedAsync(terminators, cancellationToken);
        if (messageStr is null) return -1;
        return Encoding.GetBytes(messageStr, message.Span);
    }

    public async ValueTask<bool> TryReadExactlyRawAsync(Memory<byte> message, CancellationToken cancellationToken)
    {
        var messageStr = await TryReadExactlyAsync(message.Length, cancellationToken);
        if (messageStr is null) return false;
        return Encoding.GetBytes(messageStr, message.Span) == message.Length;
    }

    private void ClearBufferIfEmpty()
    {
        if (_responsePointer == _responseBuffer.Length)
        {
            _responseBuffer.Clear();
            _responsePointer = 0;
        }
    }
}

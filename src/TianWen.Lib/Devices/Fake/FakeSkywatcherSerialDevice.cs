using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using TianWen.Lib.Devices.Skywatcher;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Fake serial device simulating a Skywatcher motor controller (EQ6-R model).
/// Responds to Skywatcher protocol commands for testing.
/// </summary>
internal class FakeSkywatcherSerialDevice : ISerialConnection
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly StringBuilder _responseBuffer = new();
    private int _responsePointer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _lockObj = new();

    // Mount parameters (EQ6-R defaults)
    private const uint DEFAULT_CPR = 9024000; // counts per revolution
    private const uint DEFAULT_TMR_FREQ = 1500000; // timer frequency
    private const uint DEFAULT_HIGH_SPEED_RATIO = 16;
    private const uint DEFAULT_WORM_PERIOD = 50133; // steps per worm revolution (CPR / 180 teeth)
    private const SkywatcherMountModel MOUNT_MODEL = SkywatcherMountModel.Eq6;

    // Encoder positions (steps relative to home)
    private int _posRa;
    private int _posDec;

    // Axis status
    private bool _raRunning;
    private bool _raTracking; // true=tracking rate, false=slewing rate
    private bool _decRunning;
    private bool _raInitDone;
    private bool _decInitDone;

    // Slew state
    private int _targetRaSteps;
    private int _targetDecSteps;
    private int _raDirection; // 0=forward, 1=reverse
    private int _decDirection;
    private int _raMode; // 0=tracking, 1=slew (constant speed); from G command payload[0]
    private int _decMode;

    // Guide speed
    private int _guideSpeedIndex = 2; // 0-4

    // Snap port
    private bool _snapActive;

    // Tracking simulation
    private long _lastTrackingTicks;
    private ITimer? _trackingTimer;

    // Firmware: EQ6-R, version 3.39
    private const int FIRMWARE_MAJOR = 3;
    private const int FIRMWARE_MINOR = 39;

    public FakeSkywatcherSerialDevice(ILogger logger, Encoding encoding, TimeProvider timeProvider, bool isOpen)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _lastTrackingTicks = timeProvider.GetTimestamp();
        IsOpen = isOpen;
        Encoding = encoding;

        // Start simulation timer
        var period = TimeSpan.FromMilliseconds(50);
        _trackingTimer = _timeProvider.CreateTimer(SimulationTimerCallback, null, period, period);
    }

    public bool IsOpen { get; private set; }
    public Encoding Encoding { get; }

    private void SimulationTimerCallback(object? state)
    {
        if (!IsOpen) return;

        var currentTicks = _timeProvider.GetTimestamp();
        var elapsedTicks = currentTicks - _lastTrackingTicks;
        _lastTrackingTicks = currentTicks;

        lock (_lockObj)
        {
            var elapsedSeconds = (double)elapsedTicks / _timeProvider.TimestampFrequency;

            // Simulate tracking: advance RA at sidereal rate
            if (_raRunning && _raTracking)
            {
                // Sidereal rate in steps/sec = CPR / 86164.0905 (sidereal day seconds)
                var stepsPerSec = (double)DEFAULT_CPR / 86164.0905;
                _posRa += (int)(stepsPerSec * elapsedSeconds);
            }

            // Simulate slew: move toward target
            if (_raRunning && !_raTracking)
            {
                var slewRate = (double)DEFAULT_CPR / 360.0 * 3.0; // 3 deg/s slew speed in steps
                var delta = _targetRaSteps - _posRa;
                if (Math.Abs(delta) < slewRate * elapsedSeconds)
                {
                    _posRa = _targetRaSteps;
                    _raRunning = false;
                }
                else
                {
                    _posRa += (int)(Math.Sign(delta) * slewRate * elapsedSeconds);
                }
            }

            if (_decRunning)
            {
                var slewRate = (double)DEFAULT_CPR / 360.0 * 3.0;
                var delta = _targetDecSteps - _posDec;
                if (Math.Abs(delta) < slewRate * elapsedSeconds)
                {
                    _posDec = _targetDecSteps;
                    _decRunning = false;
                }
                else
                {
                    _posDec += (int)(Math.Sign(delta) * slewRate * elapsedSeconds);
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
            if (dataStr.Length < 3 || dataStr[0] != ':')
            {
                return ValueTask.FromResult(false);
            }

            var cmd = dataStr[1];
            var axis = dataStr[2];
            // Data is everything between axis char and \r terminator
            var payloadEnd = dataStr.IndexOf('\r');
            var payload = payloadEnd > 3 ? dataStr[3..payloadEnd] : (payloadEnd == 3 ? "" : dataStr[3..]);
            if (payload.EndsWith('\r'))
            {
                payload = payload[..^1];
            }

            switch (cmd)
            {
                case 'e': // Firmware query
                {
                    // Response: board version byte, minor, major (LE encoded)
                    // boardVersion=MODEL, minor=FIRMWARE_MINOR, major=FIRMWARE_MAJOR
                    var fwBytes = (uint)((int)MOUNT_MODEL | (FIRMWARE_MINOR << 8) | (FIRMWARE_MAJOR << 16));
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodeUInt24(fwBytes));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'a': // Counts per revolution
                {
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodeUInt24(DEFAULT_CPR));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'b': // Timer frequency
                {
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodeUInt24(DEFAULT_TMR_FREQ));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 's': // Steps per worm revolution (PEC period)
                {
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodeUInt24(DEFAULT_WORM_PERIOD));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'g': // High-speed ratio
                {
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(DEFAULT_HIGH_SPEED_RATIO.ToString("X2"));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'j': // Current position
                {
                    var pos = axis == '1' ? _posRa : _posDec;
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodePosition(pos));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'f': // Axis status
                {
                    // 3 bytes: byte0 (running|blocked), byte1 (initDone), byte2 (level: 0=tracking, 1=slew)
                    var isRunning = axis == '1' ? _raRunning : _decRunning;
                    var isInitDone = axis == '1' ? _raInitDone : _decInitDone;
                    var isTracking = axis == '1' ? _raTracking : false;

                    var byte0 = isRunning ? 1 : 0;
                    var byte1 = isInitDone ? 1 : 0;
                    var byte2 = (isRunning && !isTracking) ? 1 : 0; // 1=slewing speed, 0=tracking speed

                    var statusVal = (uint)(byte0 | (byte1 << 8) | (byte2 << 16));
                    _responseBuffer.Append('=');
                    _responseBuffer.Append(SkywatcherProtocol.EncodeUInt24(statusVal));
                    _responseBuffer.Append('\r');
                    break;
                }

                case 'E': // Set position (sync)
                {
                    if (payload.Length >= 6)
                    {
                        var pos = SkywatcherProtocol.DecodePosition(payload.AsSpan(0, 6));
                        if (axis == '1') _posRa = pos;
                        else _posDec = pos;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'F': // Init done
                {
                    if (axis == '3' || axis == '1') _raInitDone = true;
                    if (axis == '3' || axis == '2') _decInitDone = true;
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'G': // Motion mode
                {
                    // payload: 3 chars: mode, speed, direction
                    if (payload.Length >= 3)
                    {
                        var mode = payload[0] - '0';
                        var dir = payload[2] - '0';
                        if (axis == '1') { _raMode = mode; _raDirection = dir; }
                        else { _decMode = mode; _decDirection = dir; }
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'H': // Goto increment (relative goto step count)
                {
                    if (payload.Length >= 6)
                    {
                        var steps = (int)SkywatcherProtocol.DecodeUInt24(payload.AsSpan(0, 6));
                        if (axis == '1')
                        {
                            _targetRaSteps = _raDirection == 0 ? _posRa + steps : _posRa - steps;
                        }
                        else
                        {
                            _targetDecSteps = _decDirection == 0 ? _posDec + steps : _posDec - steps;
                        }
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'S': // Absolute goto position
                {
                    if (payload.Length >= 6)
                    {
                        var targetPos = SkywatcherProtocol.DecodePosition(payload.AsSpan(0, 6));
                        if (axis == '1') _targetRaSteps = targetPos;
                        else _targetDecSteps = targetPos;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'I': // Step period (T1 preset / speed)
                {
                    // Just acknowledge — speed is implied by timer simulation
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'J': // Start motion
                {
                    if (axis == '1' || axis == '3')
                    {
                        _raRunning = true;
                        // Mode 0 = tracking (sidereal rate), mode 1 = slew (constant speed)
                        _raTracking = _raMode == 0;
                    }
                    if (axis == '2' || axis == '3')
                    {
                        _decRunning = true;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'K': // Decelerate stop
                {
                    if (axis == '1' || axis == '3')
                    {
                        _raRunning = false;
                        _raTracking = false;
                    }
                    if (axis == '2' || axis == '3')
                    {
                        _decRunning = false;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'L': // Instant stop
                {
                    if (axis == '1' || axis == '3')
                    {
                        _raRunning = false;
                        _raTracking = false;
                    }
                    if (axis == '2' || axis == '3')
                    {
                        _decRunning = false;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'P': // Guide speed
                {
                    if (payload.Length >= 1 && int.TryParse(payload.AsSpan(0, 1), CultureInfo.InvariantCulture, out var speedIdx) && speedIdx is >= 0 and <= 4)
                    {
                        _guideSpeedIndex = speedIdx;
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'O': // Snap port (aux on/off)
                {
                    _snapActive = payload.Length >= 1 && payload[0] == '1';
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'D': // Sidereal period
                {
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'q': // Extended inquiry
                {
                    // Return default capabilities: PPec, HomeSensors
                    // Nibble 0: no PEC training/on
                    // Nibble 1: CanPPec (0x2) | CanHomeSensors (0x4) = 0x6
                    // Nibble 2: none (0x0)
                    _responseBuffer.Append("=060000\r");
                    break;
                }

                case 'W': // Extended setting
                {
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'M': // Brake point
                {
                    _responseBuffer.Append("=\r");
                    break;
                }

                default:
                    return ValueTask.FromResult(false);
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

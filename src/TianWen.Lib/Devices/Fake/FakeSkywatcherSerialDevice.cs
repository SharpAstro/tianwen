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
    private readonly ITimeProvider _timeProvider;
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
    private bool _raGotoMode; // true=goto (move to H/S target), false=tracking/guide (constant speed)
    private bool _decGotoMode;
    // High-speed-mode flag, captured from the latest G command's mode byte (bit 1
    // cleared = high-speed, set = low-speed). The driver inflates the I T1 preset
    // by highSpeedRatio (16x) when high-speed is selected; the fake must reverse
    // that scaling when decoding T1 -> deg/sec, otherwise MoveAxisAsync at any
    // rate above 2x sidereal arrives as 1/16 of the requested speed (polar-align
    // 60deg rotation at 3deg/s should take ~20s but ran for ~5min before the fix).
    private bool _raHighSpeed;
    private bool _decHighSpeed;

    // Constant-speed slew rate captured from the most recent ':I' (T1 preset)
    // command, in degrees-per-second. The simulation integrates at this rate
    // when the axis is running in non-goto, non-tracking mode -- without it
    // MoveAxisAsync(rate=4deg/s) was a no-op in the fake (the axis stayed put,
    // breaking polar-alignment Phase A which relies on a 60deg RA delta to
    // recover the mount axis). Zero means "no slew rate captured yet".
    private double _raSlewDegPerSec;
    private double _decSlewDegPerSec;

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

    public FakeSkywatcherSerialDevice(ILogger logger, Encoding encoding, ITimeProvider timeProvider, bool isOpen)
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

            // Simulate tracking: advance RA at sidereal rate when in tracking
            // mode (G mode bit cleared by tracking-rate path). For constant-
            // speed MoveAxis slews, _raTracking is set true by the J command's
            // current logic but the requested rate from the I command exceeds
            // sidereal, so we prefer _raSlewDegPerSec when set: the user's
            // MoveAxisAsync(rate) actually moves the axis at that rate.
            if (_raRunning && _raTracking)
            {
                double stepsPerSec;
                if (_raSlewDegPerSec > 0)
                {
                    stepsPerSec = _raSlewDegPerSec * (double)DEFAULT_CPR / 360.0;
                }
                else
                {
                    // Sidereal rate in steps/sec = CPR / 86164.0905 (sidereal day seconds)
                    stepsPerSec = (double)DEFAULT_CPR / 86164.0905;
                }
                var direction = _raDirection == 0 ? 1.0 : -1.0;
                _posRa += (int)(direction * stepsPerSec * elapsedSeconds);
            }

            // Simulate goto slew: move toward target, auto-start tracking when done
            if (_raRunning && _raGotoMode)
            {
                var slewRate = (double)DEFAULT_CPR / 360.0 * 3.0; // 3 deg/s slew speed in steps
                var delta = _targetRaSteps - _posRa;
                if (Math.Abs(delta) < slewRate * elapsedSeconds)
                {
                    _posRa = _targetRaSteps;
                    // Real SW mounts auto-start sidereal tracking after goto completes
                    _raGotoMode = false;
                    _raTracking = true;
                    // _raRunning stays true — now tracking at sidereal rate
                }
                else
                {
                    _posRa += (int)(Math.Sign(delta) * slewRate * elapsedSeconds);
                }
            }

            if (_decRunning)
            {
                if (_decGotoMode)
                {
                    // Goto: move toward target, stop when reached.
                    // On arrival we both stop the axis and clear _decGotoMode — leaving
                    // the goto flag set after the slew completes makes the next status
                    // query report Dec as "not tracking" (same convention as RA), and
                    // matches the post-arrival state the RA branch maintains.
                    var slewRate = (double)DEFAULT_CPR / 360.0 * 3.0;
                    var delta = _targetDecSteps - _posDec;
                    if (Math.Abs(delta) < slewRate * elapsedSeconds)
                    {
                        _posDec = _targetDecSteps;
                        _decRunning = false;
                        _decGotoMode = false;
                    }
                    else
                    {
                        _posDec += (int)(Math.Sign(delta) * slewRate * elapsedSeconds);
                    }
                }
                else
                {
                    // Constant-speed guide/slew: move Dec at guide rate
                    var guideRate = (double)DEFAULT_CPR / 86164.0905 * 0.5; // 0.5x sidereal
                    var direction = _decDirection == 0 ? 1.0 : -1.0;
                    _posDec += (int)(direction * guideRate * elapsedSeconds);
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
                    var isTracking = axis == '1' ? _raTracking : !_decGotoMode;

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
                    // payload: 2-char hex mode byte + 1-char direction
                    // mode bit 0: 0=goto, 1=constant-speed (tracking/guide)
                    // mode bit 1: 0=high-speed, 1=low-speed
                    // direction: 0=forward, 1=reverse
                    if (payload.Length >= 3)
                    {
                        var modeByte = byte.Parse(payload.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var isGoto = (modeByte & 0x01) == 0;
                        var isHighSpeed = (modeByte & 0x02) == 0;
                        var dir = payload[2] - '0';
                        if (axis == '1') { _raGotoMode = isGoto; _raDirection = dir; _raHighSpeed = isHighSpeed; }
                        else { _decGotoMode = isGoto; _decDirection = dir; _decHighSpeed = isHighSpeed; }
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
                    // T1 = tmrFreq * 360 / speed / cpr  (* highSpeedRatio if high-speed)
                    // Inverting: speed = tmrFreq * 360 / (cpr * T1) [* highSpeedRatio
                    // when the most recent G command set high-speed mode]. Without the
                    // high-speed factor, MoveAxisAsync at slew rates (>2x sidereal)
                    // arrives at the simulation 16x too slow -- the polar-alignment
                    // 60deg rotation took ~5min instead of the expected ~20s.
                    if (payload.Length >= 6)
                    {
                        var t1 = (uint)SkywatcherProtocol.DecodeUInt24(payload.AsSpan(0, 6));
                        if (t1 > 0)
                        {
                            var degPerSec = (double)DEFAULT_TMR_FREQ * 360.0 / (DEFAULT_CPR * (double)t1);
                            var isHighSpeed = axis == '1' ? _raHighSpeed : _decHighSpeed;
                            if (isHighSpeed)
                            {
                                degPerSec *= DEFAULT_HIGH_SPEED_RATIO;
                            }
                            if (axis == '1') _raSlewDegPerSec = degPerSec;
                            else if (axis == '2') _decSlewDegPerSec = degPerSec;
                        }
                    }
                    _responseBuffer.Append("=\r");
                    break;
                }

                case 'J': // Start motion
                {
                    if (axis == '1' || axis == '3')
                    {
                        _raRunning = true;
                        // Goto mode moves to H/S target; constant-speed mode is tracking/guide
                        _raTracking = !_raGotoMode;
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
                        _raSlewDegPerSec = 0;
                        _raHighSpeed = false;
                    }
                    if (axis == '2' || axis == '3')
                    {
                        _decRunning = false;
                        _decSlewDegPerSec = 0;
                        _decHighSpeed = false;
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
                        _raSlewDegPerSec = 0;
                        _raHighSpeed = false;
                    }
                    if (axis == '2' || axis == '3')
                    {
                        _decRunning = false;
                        _decSlewDegPerSec = 0;
                        _decHighSpeed = false;
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

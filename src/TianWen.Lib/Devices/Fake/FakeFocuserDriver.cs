using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeFocuserDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakePositionBasedDriver(fakeDevice, serviceProvider), IFocuserDriver
{
    // Temperature model
    private readonly double _baseTemperature = 15.0;
    private double _tempDriftRate = -0.5; // °C per hour (cooling overnight)
    private DateTimeOffset? _startTime;

    // Focus model — read from device URI settings, with sensible defaults
    private readonly int _baseBestFocus = int.TryParse(fakeDevice.Query.QueryValue(DeviceQueryKey.FocuserBestFocus), out var bf) ? bf : 1000;
    private readonly int _initialPosition = int.TryParse(fakeDevice.Query.QueryValue(DeviceQueryKey.FocuserInitialPosition), out var ip) ? ip : 980;
    private double _tempCoefficient = 5.0; // steps per °C of focus shift

    // Backlash model
    private int _lastDirection; // +1 or -1
    private int _backlashSteps; // consumed backlash pending
    private int _trueBacklashIn = int.TryParse(fakeDevice.Query.QueryValue(DeviceQueryKey.FocuserBacklashIn), out var bi) ? bi : 20;
    private int _trueBacklashOut = int.TryParse(fakeDevice.Query.QueryValue(DeviceQueryKey.FocuserBacklashOut), out var bo) ? bo : 15;

    protected override void OnConnected()
    {
        _position = _initialPosition;
        _startTime = TimeProvider.GetUtcNow();
    }

    /// <summary>Current true best focus position accounting for temperature drift.</summary>
    public int TrueBestFocus
    {
        get
        {
            var currentTemp = GetCurrentTemperature();
            return _baseBestFocus + (int)(_tempCoefficient * (currentTemp - _baseTemperature));
        }
    }

    /// <summary>Settable mechanical backlash for inward moves (for test setup).</summary>
    public int TrueBacklashIn
    {
        get => _trueBacklashIn;
        set => _trueBacklashIn = value;
    }

    /// <summary>Settable mechanical backlash for outward moves (for test setup).</summary>
    public int TrueBacklashOut
    {
        get => _trueBacklashOut;
        set => _trueBacklashOut = value;
    }

    public int BacklashStepsIn { get; set; } = 20;

    public int BacklashStepsOut { get; set; } = 15;

    /// <summary>Temperature drift rate in °C per hour. Negative = cooling.</summary>
    public double TempDriftRate
    {
        get => _tempDriftRate;
        set => _tempDriftRate = value;
    }

    /// <summary>Focus shift steps per °C of temperature change.</summary>
    public double TempCoefficient
    {
        get => _tempCoefficient;
        set => _tempCoefficient = value;
    }

    public ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_position);

    public bool Absolute => true;

    public ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_isMoving);

    public int MaxIncrement => -1;

    public int MaxStep => 2000;

    public bool CanGetStepSize => false;

    public double StepSize => double.NaN;

    public ValueTask<bool> GetTempCompAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask SetTempCompAsync(bool value, CancellationToken cancellationToken = default)
        => throw new FakeDeviceException($"TempComp is not supported (trying to set to {value})");

    public bool TempCompAvailable => false;

    public ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetCurrentTemperature());

    public Task BeginHaltAsync(CancellationToken cancellationToken = default) => BeginStopMovingAsync(cancellationToken);

    public Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser is not connected");
        }
        else if (position < 0 || position > MaxStep)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Position out of range (0..{MaxStep})");
        }

        // Track direction for backlash
        var currentPos = _position;
        var direction = Math.Sign(position - currentPos);
        if (direction != 0)
        {
            if (_lastDirection != 0 && direction != _lastDirection)
            {
                // Direction reversed — backlash engages, amount depends on new direction
                _backlashSteps = direction > 0 ? _trueBacklashOut : _trueBacklashIn;
            }
            _lastDirection = direction;
        }

        // Consume backlash during movement
        if (_backlashSteps > 0)
        {
            var stepsToMove = Math.Abs(position - currentPos);
            _backlashSteps = Math.Max(0, _backlashSteps - stepsToMove);
        }

        return BeginSetPositionAsync(position, cancellationToken);
    }

    /// <summary>
    /// Returns the effective position accounting for backlash. When backlash steps remain,
    /// physical movement doesn't translate to actual optical position change.
    /// </summary>
    public int EffectivePosition
    {
        get
        {
            if (_backlashSteps > 0)
            {
                return _position - (_lastDirection * _backlashSteps);
            }
            return _position;
        }
    }

    private double GetCurrentTemperature()
    {
        _startTime ??= TimeProvider.GetUtcNow();
        var elapsed = TimeProvider.GetUtcNow() - _startTime.Value;
        return _baseTemperature + _tempDriftRate * elapsed.TotalHours;
    }
}

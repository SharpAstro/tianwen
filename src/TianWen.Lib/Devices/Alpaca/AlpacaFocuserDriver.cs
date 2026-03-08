using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaFocuserDriver(AlpacaDevice device, IExternal external)
    : AlpacaDeviceDriverBase(device, external), IFocuserDriver
{
    // Cached static properties
    private bool _absolute;
    private int _maxIncrement, _maxStep;

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        _absolute = await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "absolute", cancellationToken);
        _maxIncrement = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxincrement", cancellationToken);
        _maxStep = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxstep", cancellationToken);
        TempCompAvailable = await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "tempcompavailable", cancellationToken);

        try
        {
            StepSize = await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "stepsize", cancellationToken);
            CanGetStepSize = !double.IsNaN(StepSize);
        }
        catch
        {
            StepSize = double.NaN;
            CanGetStepSize = false;
        }

        return true;
    }

    // Static properties
    public bool Absolute => _absolute;
    public int MaxIncrement => _maxIncrement;
    public int MaxStep => _maxStep;
    public double StepSize { get; private set; } = double.NaN;
    public bool CanGetStepSize { get; private set; }
    public bool TempCompAvailable { get; private set; }

    // Dynamic properties — sync versions throw, callers should use async alternatives
    public int Position => throw new NotSupportedException("Use GetPositionAsync instead");
    public bool IsMoving => throw new NotSupportedException("Use GetIsMovingAsync instead");
    public double Temperature => throw new NotSupportedException("Use GetTemperatureAsync instead");

    // Async alternatives — native async HTTP calls
    public async ValueTask<int> GetPositionAsync(CancellationToken cancellationToken = default)
        => Connected && _absolute
            ? await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "position", cancellationToken)
            : int.MinValue;

    public async ValueTask<bool> GetIsMovingAsync(CancellationToken cancellationToken = default)
        => Connected && await Client.GetBoolAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "ismoving", cancellationToken);

    public async ValueTask<double> GetTemperatureAsync(CancellationToken cancellationToken = default)
    {
        try { return await Client.GetDoubleAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "temperature", cancellationToken); }
        catch { return double.NaN; }
    }

    public bool TempComp
    {
        get => throw new NotSupportedException("Use async polling for TempComp on Alpaca");
        set => _ = Client.PutAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "tempcomp", [new("TempComp", value.ToString())]);
    }

    public async Task BeginMoveAsync(int position, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }
        else if (_absolute && (position is < 0 || position > _maxStep))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Absolute position must be between 0 and {_maxStep}");
        }
        else if (!_absolute && (position < -_maxIncrement || position > _maxIncrement))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Relative position must be between -{_maxIncrement} and {_maxIncrement}");
        }

        await PutMethodAsync("move", [new("Position", position.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async Task BeginHaltAsync(CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Focuser not connected");
        }

        await PutMethodAsync("halt", cancellationToken: cancellationToken);
    }
}

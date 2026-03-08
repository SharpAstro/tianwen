using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaCoverCalibratorDriver(AlpacaDevice device, IExternal external)
    : AlpacaDeviceDriverBase(device, external), ICoverDriver
{
    // Cached static property
    private int _maxBrightness = -1;

    protected override async ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken)
    {
        try { _maxBrightness = await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "maxbrightness", cancellationToken); }
        catch { _maxBrightness = -1; }

        return true;
    }

    // Static property — cached at init
    public int MaxBrightness => _maxBrightness;

    public async ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "brightness", cancellationToken);

    public async ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => (CoverStatus)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "coverstate", cancellationToken);

    public async ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => (CalibratorStatus)await Client.GetIntAsync(BaseUrl, AlpacaDeviceType, AlpacaDeviceNumber, "calibratorstate", cancellationToken);

    public async Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Cover is not connected");
        }
        else if (brightness < 0 || brightness > _maxBrightness)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness), brightness, $"Brightness out of range (0..{_maxBrightness})");
        }

        var coverState = await GetCoverStateAsync(cancellationToken);
        var calState = await GetCalibratorStateAsync(cancellationToken);
        if (coverState is CoverStatus.Error or CoverStatus.Moving
            || calState is CalibratorStatus.NotReady or CalibratorStatus.NotPresent or CalibratorStatus.Error)
        {
            throw new InvalidOperationException("Cover is not ready");
        }

        await PutMethodAsync("calibratoron", [new("Brightness", brightness.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public async Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        await PutMethodAsync("calibratoroff", cancellationToken: cancellationToken);
    }

    public async Task BeginClose(CancellationToken cancellationToken = default)
    {
        await PutMethodAsync("closecover", cancellationToken: cancellationToken);
    }

    public async Task BeginOpen(CancellationToken cancellationToken = default)
    {
        await PutMethodAsync("opencover", cancellationToken: cancellationToken);
    }
}

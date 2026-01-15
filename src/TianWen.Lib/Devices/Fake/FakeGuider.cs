using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Devices.Fake;

internal class FakeGuider(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IGuider
{
    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;

    public ValueTask<(int Width, int Height)?> CameraFrameSizeAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask ConnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisconnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<TimeSpan> ExposureTimeAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<IReadOnlyList<string>> GetEquipmentProfilesAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<SettleProgress?> GetSettleProgressAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<GuideStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<(string? AppState, double AvgDist)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
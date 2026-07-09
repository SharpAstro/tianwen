using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Cover/calibrator driver for a manual (dumb) flat light panel — a hand-switched light source with no
/// hardware interface (e.g. an analog LED panel with a physical brightness knob). It reports NO cover flap
/// (<see cref="CoverStatus.NotPresent"/>) and treats the calibrator as user-operated: <see cref="BeginCalibratorOn"/>
/// simply reports the panel <see cref="CalibratorStatus.Ready"/> (the user has switched it on) and
/// <see cref="BeginOpen"/>/<see cref="BeginClose"/> are no-ops. Software cannot set the analog brightness,
/// so the flat routine converges the <em>exposure</em> instead. Mirrors <see cref="ManualFilterWheelDriver"/>
/// (a degenerate driver over a dumb device) so the session drives it through the normal calibrator path with
/// no special-casing.
/// </summary>
internal sealed class ManualCoverDriver(ManualCoverDevice device, IServiceProvider serviceProvider) : ICoverDriver
{
    private bool _connected;
    private volatile CalibratorStatus _calibratorState = CalibratorStatus.Off;
    private int _brightness;

    // A hand-set analog panel exposes no software brightness range; report a nominal full scale so the
    // calibrator-brightness percentage math stays well-defined (the requested value is otherwise ignored —
    // see BeginCalibratorOn).
    public int MaxBrightness => 255;

    // The panel is hand-switched: BeginCalibratorOn only records the requested value; a human sets the
    // actual light. The flat routine reads this to prompt the user to switch the panel on before capturing.
    public bool CanControlBrightness => false;

    // No motorised flap — a bare light panel, like the Gemini FlatPanel Lite.
    public ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CoverStatus.NotPresent);

    public ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_calibratorState);

    public ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_brightness);

    // No flap to move — no-op (the equivalent of ManualFilterWheelDriver's no-op BeginMoveAsync).
    public Task BeginOpen(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task BeginClose(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // Software can't drive an analog panel; record the requested level and report the panel Ready, trusting
    // the user switched it on. The exposure solver does the real work of hitting the target ADU.
    public Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        _brightness = Math.Clamp(brightness, 0, MaxBrightness);
        _calibratorState = CalibratorStatus.Ready;
        return Task.CompletedTask;
    }

    public Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        _brightness = 0;
        _calibratorState = CalibratorStatus.Off;
        return Task.CompletedTask;
    }

    public string Name => device.DisplayName;

    public string? Description => "Manual (hand-switched) flat light panel with no cover flap";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => _connected;

    public DeviceType DriverType => DeviceType.CoverCalibrator;

    public IExternal External { get; } = serviceProvider.GetRequiredService<IExternal>();

    public ILogger Logger { get; } = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ManualCoverDriver));

    public ITimeProvider TimeProvider { get; } = serviceProvider.GetRequiredService<ITimeProvider>();

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        return ValueTask.CompletedTask;
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => DisconnectAsync();
}

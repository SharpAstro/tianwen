using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal bool Catch(Action action) => _logger.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => _logger.Catch(func, @default);
    internal ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
        => _logger.CatchAsync(asyncFunc, cancellationToken);
    internal Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
        => _logger.CatchAsync(asyncFunc, cancellationToken);


    internal ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => _logger.CatchAsync(asyncFunc, cancellationToken, @default);

    /// <summary>
    /// Session-scoped <see cref="ResilientCall.InvokeAsync"/> that auto-wires
    /// <see cref="OnDriverReconnect"/> as the reconnect callback. Hot-path callers
    /// should use this overload rather than <see cref="ResilientCall"/> directly
    /// so every transparent reconnect is counted against escalation.
    /// </summary>
    internal ValueTask<T> ResilientInvokeAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask<T>> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken)
        => ResilientCall.InvokeAsync(driver, op, options, cancellationToken, onReconnect: OnDriverReconnect);

    /// <summary>Void <see cref="ValueTask"/> variant. See <see cref="ResilientInvokeAsync{T}"/>.</summary>
    internal ValueTask ResilientInvokeAsync(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken)
        => ResilientCall.InvokeAsync(driver, op, options, cancellationToken, onReconnect: OnDriverReconnect);

    /// <summary><see cref="Task{T}"/> variant for drivers that expose Task-returning methods.</summary>
    internal ValueTask<T> ResilientInvokeAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, Task<T>> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken)
        => ResilientCall.InvokeAsync(driver, op, options, cancellationToken, onReconnect: OnDriverReconnect);

    /// <summary>Void <see cref="Task"/> variant.</summary>
    internal ValueTask ResilientInvokeAsync(
        IDeviceDriver driver,
        Func<CancellationToken, Task> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken)
        => ResilientCall.InvokeAsync(driver, op, options, cancellationToken, onReconnect: OnDriverReconnect);

    /// <summary>
    /// Increments the per-driver fault counter. Wired into <see cref="ResilientCall.InvokeAsync"/>
    /// as the <c>onReconnect</c> callback so every transparent reconnect is visible to the
    /// session's escalation policy.
    /// </summary>
    internal void OnDriverReconnect(IDeviceDriver driver)
    {
        var count = _driverFaultCounts.AddOrUpdate(driver, 1, static (_, current) => current + 1);
        _logger.LogWarning("Driver {Device} fault count now {Count}/{Threshold}.",
            driver.Name, count, Configuration.DeviceFaultEscalationThreshold);
    }

    /// <summary>
    /// Current fault count for <paramref name="driver"/>. Returns <c>0</c> if the driver
    /// has never had a reconnect. Exposed internally for tests.
    /// </summary>
    internal int GetFaultCount(IDeviceDriver driver)
        => _driverFaultCounts.TryGetValue(driver, out var count) ? count : 0;

    /// <summary>
    /// Called once per successful frame write. Every
    /// <see cref="SessionConfiguration.DeviceFaultDecayFrames"/> frames, decays every
    /// non-zero counter by one. A bad hour shouldn't block the rest of the session, but
    /// we only forgive when the devices are actually working.
    /// </summary>
    internal void DecayFaultCountersOnFrameSuccess()
    {
        var decayEvery = Configuration.DeviceFaultDecayFrames;
        if (decayEvery <= 0)
        {
            return;
        }

        var frames = Interlocked.Increment(ref _framesSinceLastFaultDecay);
        if (frames < decayEvery)
        {
            return;
        }

        // Reset window; best-effort — a race that under-counts one frame just delays
        // the decay by one tick, which is fine.
        Interlocked.Exchange(ref _framesSinceLastFaultDecay, 0);

        foreach (var (driver, count) in _driverFaultCounts)
        {
            if (count > 0)
            {
                _driverFaultCounts.AddOrUpdate(driver, 0, static (_, c) => Math.Max(0, c - 1));
            }
        }
    }

    /// <summary>
    /// Returns the first driver whose fault counter has reached
    /// <see cref="SessionConfiguration.DeviceFaultEscalationThreshold"/>, or <c>null</c>
    /// if no driver has escalated. Callers that get a non-null result should return
    /// <see cref="ImageLoopNextAction.DeviceUnrecoverable"/> and let the outer loop
    /// finalise the session.
    /// </summary>
    internal IDeviceDriver? TryFindEscalatedDriver()
    {
        var threshold = Configuration.DeviceFaultEscalationThreshold;
        foreach (var (driver, count) in _driverFaultCounts)
        {
            if (count >= threshold)
            {
                return driver;
            }
        }
        return null;
    }
}

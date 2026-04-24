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

    /// <summary>
    /// Telemetry-poll read that tolerates transient faults AND triggers a proactive
    /// reconnect after <see cref="PROACTIVE_RECONNECT_THRESHOLD"/> consecutive
    /// failures. Returns <paramref name="fallback"/> on failure (the poll is always
    /// best-effort).
    /// <para>
    /// Why proactive: plain <c>CatchAsync</c> swallows failures forever, so a dropped
    /// USB cable silently freezes the telemetry strip until the next exposure. By
    /// counting consecutive failures here and firing <c>ConnectAsync</c> at the
    /// threshold, by the time the imaging loop issues the next exposure the reconnect
    /// is already in flight — and <see cref="ResilientCall"/>'s own pre-reconnect
    /// check sees a connected driver instead of racing a dead handle.
    /// </para>
    /// </summary>
    internal async ValueTask<T> PollDriverReadAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask<T>> op,
        T fallback,
        CancellationToken cancellationToken) where T : struct
    {
        try
        {
            var result = await op(cancellationToken);
            _consecutivePollFailures.TryRemove(driver, out _);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = _consecutivePollFailures.AddOrUpdate(driver, 1, static (_, c) => c + 1);
            _logger.LogDebug(ex, "Telemetry poll for {Device} failed (consecutive: {Count}): {Message}",
                driver.Name, failures, ex.Message);

            // Only fire on the exact threshold crossing, not every subsequent failure.
            // The counter stays above threshold until one poll succeeds, so a failed
            // reconnect won't cause a storm of retries on every poll tick.
            if (failures == PROACTIVE_RECONNECT_THRESHOLD)
            {
                _logger.LogWarning(
                    "Telemetry poll for {Device} failed {Count} consecutive times; triggering proactive reconnect.",
                    driver.Name, failures);
                OnDriverReconnect(driver);
                try
                {
                    await driver.ConnectAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception reconnectEx)
                {
                    _logger.LogWarning(reconnectEx,
                        "Proactive reconnect for {Device} threw {Type}: {Message}.",
                        driver.Name, reconnectEx.GetType().Name, reconnectEx.Message);
                }
            }

            return fallback;
        }
    }

    /// <summary>
    /// Capability-gated variant of <see cref="PollDriverReadAsync{T}"/>. When
    /// <paramref name="capable"/> is <see langword="false"/> (driver reports it
    /// doesn't support this read), returns <paramref name="fallback"/> without
    /// calling <paramref name="op"/> and without touching failure counters.
    /// </summary>
    internal ValueTask<T> PollDriverReadAsyncIf<T>(
        IDeviceDriver driver,
        bool capable,
        Func<CancellationToken, ValueTask<T>> op,
        T fallback,
        CancellationToken cancellationToken) where T : struct
        => capable
            ? PollDriverReadAsync(driver, op, fallback, cancellationToken)
            : ValueTask.FromResult(fallback);

    /// <summary>Current consecutive-poll-failure count for diagnostics / tests.</summary>
    internal int GetConsecutivePollFailures(IDeviceDriver driver)
        => _consecutivePollFailures.TryGetValue(driver, out var count) ? count : 0;
}

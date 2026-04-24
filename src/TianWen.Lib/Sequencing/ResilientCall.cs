using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Wraps a single driver call with transient-retry, between-retry reconnect, and
/// opt-in fault reporting. Drop-in replacement for a naked <c>await driver.Op(...)</c>
/// in the Session hot path.
/// <para>
/// Classification:
/// <list type="bullet">
/// <item>Idempotent reads retry up to <see cref="ResilientCallOptions.MaxAttempts"/>
/// with exponential backoff, reconnecting the driver between attempts when
/// <see cref="IDeviceDriver.Connected"/> is false.</item>
/// <item>Non-idempotent actions never retry but still pre-reconnect once before the
/// first attempt if the driver reports disconnected — a cheap guard that is
/// strictly safer than racing a dead handle.</item>
/// <item><see cref="OperationCanceledException"/> that matches the caller's
/// cancellation token is rethrown immediately, with no retry and no reconnect.</item>
/// <item>Non-transient exceptions (caller bugs: argument / invalid-operation /
/// not-supported) are rethrown immediately too.</item>
/// </list>
/// </para>
/// </summary>
internal static class ResilientCall
{
    public static ValueTask<T> InvokeAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask<T>> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken,
        Action<IDeviceDriver>? onReconnect = null)
        => InvokeCoreAsync(driver, op, options, cancellationToken, onReconnect);

    public static async ValueTask InvokeAsync(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken,
        Action<IDeviceDriver>? onReconnect = null)
    {
        await InvokeCoreAsync<object?>(driver, async ct =>
        {
            await op(ct).ConfigureAwait(false);
            return null;
        }, options, cancellationToken, onReconnect).ConfigureAwait(false);
    }

    private static async ValueTask<T> InvokeCoreAsync<T>(
        IDeviceDriver driver,
        Func<CancellationToken, ValueTask<T>> op,
        ResilientCallOptions options,
        CancellationToken cancellationToken,
        Action<IDeviceDriver>? onReconnect)
    {
        using var _ = driver.Logger.BeginScope(new Dictionary<string, object?>
        {
            ["Device"] = driver.Name,
            ["Op"] = options.OperationName,
        });

        // Pre-reconnect: cheap guard even for non-idempotent ops. A no-op if the
        // driver already reports connected; a single ConnectAsync before the
        // primary call otherwise — strictly safer than letting the op fail first.
        if (!driver.Connected)
        {
            await TryReconnectAsync(driver, cancellationToken, onReconnect).ConfigureAwait(false);
        }

        Exception? lastException = null;
        var backoff = options.InitialBackoff;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                return await op(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;

                if (!options.IsIdempotent || attempt >= options.MaxAttempts)
                {
                    break;
                }

                driver.Logger.LogWarning(ex,
                    "Resilient {Op} on {Device} failed (attempt {Attempt}/{Max}): {Message}. Backoff {Backoff} then retry.",
                    options.OperationName, driver.Name, attempt, options.MaxAttempts, ex.Message, backoff);

                if (backoff > TimeSpan.Zero)
                {
                    await driver.TimeProvider.SleepAsync(backoff, cancellationToken).ConfigureAwait(false);
                }

                if (!driver.Connected)
                {
                    await TryReconnectAsync(driver, cancellationToken, onReconnect).ConfigureAwait(false);
                }

                backoff = ScaleBackoff(backoff, options.BackoffMultiplier);
            }
        }

        // Exhausted attempts (idempotent) or single-shot non-idempotent failure.
        // Rethrow to the caller, whose policy sets the session-level response.
        throw lastException ?? new InvalidOperationException(
            $"ResilientCall.InvokeAsync for {options.OperationName} on {driver.Name} exhausted retries without capturing an exception.");
    }

    private static async ValueTask TryReconnectAsync(
        IDeviceDriver driver,
        CancellationToken cancellationToken,
        Action<IDeviceDriver>? onReconnect)
    {
        onReconnect?.Invoke(driver);
        try
        {
            await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (driver.Connected)
            {
                driver.Logger.LogInformation("Reconnect succeeded for {Device}.", driver.Name);
            }
            else
            {
                driver.Logger.LogWarning("Reconnect for {Device} returned but driver still reports disconnected.", driver.Name);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Plan: swallow ConnectAsync exceptions; a failed reconnect still counts
            // as a spent attempt for the outer retry loop. The next op() call will
            // either fail and increment again, or succeed after the device recovers.
            driver.Logger.LogWarning(ex, "Reconnect attempt for {Device} threw {Type}: {Message}.",
                driver.Name, ex.GetType().Name, ex.Message);
        }
    }

    private static TimeSpan ScaleBackoff(TimeSpan current, double multiplier)
    {
        if (multiplier <= 1.0)
        {
            return current;
        }
        return TimeSpan.FromMilliseconds(current.TotalMilliseconds * multiplier);
    }

    /// <summary>
    /// Transient-exception classifier. Conservative by design: false positives
    /// (treating a config error as transient) just log noise and spin through
    /// MaxAttempts; false negatives (not retrying a cable bump) defeat the whole
    /// point of this helper.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        // Serial / TCP / pipe I/O faults — the primary target.
        IOException => true,
        SocketException => true,
        // The driver's transport recreated its handle under us (common after a USB re-plug).
        ObjectDisposedException => true,
        // Explicit per-call timeout from the driver's own CTS (not ours).
        TimeoutException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        // Any COM throw — RPC_E_DISCONNECTED, CO_E_OBJNOTCONNECTED, driver HRESULTs.
        // ASCOM hub disconnects surface as COMException via SafeTask's faulted task.
        COMException => true,
        // AggregateException from a Task.WhenAll-style driver call — only transient
        // if every inner is transient.
        AggregateException agg when AllTransient(agg) => true,
        _ => false,
    };

    private static bool AllTransient(AggregateException agg)
    {
        foreach (var inner in agg.InnerExceptions)
        {
            if (!IsTransient(inner))
            {
                return false;
            }
        }
        return agg.InnerExceptions.Count > 0;
    }
}

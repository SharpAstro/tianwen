using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Connections;

internal static class SerialConnectionExtensions
{
    /// <summary>
    /// Starts closing without waiting, for a synchronous <c>Dispose</c> that cannot await: the close runs to its own
    /// (bounded) end on its own, and a fault is logged rather than lost. Every path that CAN await calls
    /// <see cref="ISerialConnection.TryCloseAsync"/> instead; this never blocks a thread on the close.
    /// </summary>
    public static void CloseInBackground(this ISerialConnection connection, ILogger? logger = null)
    {
        Task close;
        try
        {
            close = connection.TryCloseAsync().AsTask();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Closing a serial connection failed");
            return;
        }
        _ = close.ContinueWith(static (t, state) =>
        {
            if (t.Exception is { } ex)
            {
                ((ILogger?)state)?.LogDebug(ex.InnerException ?? ex, "Closing a serial connection failed");
            }
        }, logger, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

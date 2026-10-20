using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The mount moved while a button is held (the owner, 2026-10-20), as the node's leased move-axis: the press starts it, the
/// hold renews it well inside the lease, and the release stops it. A client that dies or loses the node mid-hold stops nothing,
/// and need not: the node stops the axis itself once the lease lapses (<c>NodeWire.MoveAxisLease</c>). One move at a time; a
/// new press ends the one before it.
/// </summary>
public sealed class MountMover(ITimeProvider timeProvider)
{
    /// <summary>How often a hold renews its move: four times inside the node's two-second lease.</summary>
    internal static readonly TimeSpan Renew = TimeSpan.FromMilliseconds(500);

    private CancellationTokenSource? _held;

    /// <summary>
    /// Moves <paramref name="axis"/> at <paramref name="rate"/> until <see cref="Release"/>, renewing the node's lease, then stops it.
    /// Ends with null once released, or with the node's refusal in words.
    /// </summary>
    public async Task<string?> HoldAsync(TianWenNodeClient client, Uri mount, TelescopeAxis axis, double rate, CancellationToken cancellationToken)
    {
        using var held = new CancellationTokenSource();
        Interlocked.Exchange(ref _held, held)?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, held.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var moved = await client.MoveAxisAsync(mount, axis, rate, cancellationToken);
                if (!moved.IsSuccess)
                {
                    return moved.Error ?? "The node did not move the mount";
                }
                await timeProvider.SleepAsync(Renew, linked.Token);
            }
        }
        catch (OperationCanceledException) when (held.IsCancellationRequested)
        {
            // Released: the stop below is the hold's end, not a failure.
        }
        finally
        {
            Interlocked.CompareExchange(ref _held, null, held);
            // On no token of the caller's: a stop a cancellation could skip leaves the axis running until the lease lapses.
            await client.MoveAxisAsync(mount, axis, 0, CancellationToken.None);
        }
        return null;
    }

    /// <summary>Ends the hold going on, if any: its move stops.</summary>
    public void Release() => Interlocked.Exchange(ref _held, null)?.Cancel();
}

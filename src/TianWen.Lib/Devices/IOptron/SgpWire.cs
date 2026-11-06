using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.IOptron;

/// <summary>
/// The one command/reply exchange with a SkyGuider Pro, for the driver and the discovery probe alike: a command is answered
/// by the reply that carries ITS prefix, never simply the next one on the wire.
/// </summary>
/// <remarks>
/// <para>Measured on the bench of 2026-11-05 (firmware 170518, over the mount's own USB socket; see
/// <c>docs/architecture/sgp-protocol.md</c>, "Replies out of step"): after a move or a stop the mount holds a reply back
/// until the NEXT command arrives and answers that command with the held one, so a reader taking one reply per command
/// falls one behind, and stays behind, across a port close too. A move start's acknowledgement also came twice. A one-reply
/// reader then parsed a stale status as the firmware (the discovery probe missed the mount) and a stop's acknowledgement as
/// a status.</para>
/// <para>The mount releases a held reply only when a command arrives, so waiting longer never brings the right one: the
/// command is SENT AGAIN. Every SGP command but the camera-snap trigger (<c>:MSCA</c>) can be repeated harmlessly (a
/// query, a setting, a move in the same direction, a stop), and the caller says which it is. A caller that knows the
/// mount has been out of step asks for each query to go twice and takes the reply to the second, so a status it reads is
/// the mount's state now, not at the previous poll.</para>
/// </remarks>
internal static class SgpWire
{
    internal static readonly ReadOnlyMemory<byte> HashTerminator = "#"u8.ToArray();

    /// <summary>How many times one exchange sends its command before it gives up on a reply with the right prefix.</summary>
    internal const int MaxSends = 3;

    /// <summary>
    /// The prefix of the reply to <paramref name="command"/>: <c>:RMRVE</c> for the firmware query, else <c>:HR</c> and the
    /// two letters after <c>:MS</c> or <c>:MG</c> (<c>:MGAS#</c> is answered by <c>:HRAS...</c>, <c>:MSMR0#</c> by
    /// <c>:HRMR...</c>).
    /// </summary>
    internal static string ReplyPrefixOf(string command)
        => command.StartsWith(":MRSVE", StringComparison.Ordinal)
            ? ":RMRVE"
            : command.Length >= 6 && command[0] == ':' && command[1] == 'M' && command[2] is 'S' or 'G'
                ? string.Concat(":HR", command.AsSpan(3, 2))
                : throw new ArgumentException($"Not an SGP command: {command}", nameof(command));

    /// <summary>
    /// Sends <paramref name="command"/> and returns the reply carrying its prefix, or null when the mount answers nothing
    /// (a read timeout) or, <paramref name="repeatable"/> being false, answers with another command's reply.
    /// <c>SawStale</c> says whether a reply for another command came first. The caller holds the port's lock.
    /// </summary>
    internal static async ValueTask<(string? Reply, bool SawStale)> ExchangeAsync(ISerialConnection port, string command, bool repeatable,
        bool sendTwice, ILogger? logger, CancellationToken cancellationToken)
    {
        var prefix = ReplyPrefixOf(command);

        // Anything already on the wire answers an earlier command (a move's second acknowledgement, say).
        port.DiscardInBuffer();

        if (sendTwice && repeatable)
        {
            if (!await port.TryWriteAsync(command, cancellationToken))
            {
                return (null, false);
            }

            var first = await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
            if (first is null)
            {
                return (null, false);
            }
        }

        var sawStale = false;
        for (var send = 0; send < MaxSends; send++)
        {
            if (!await port.TryWriteAsync(command, cancellationToken))
            {
                return (null, sawStale);
            }

            var reply = await port.TryReadTerminatedAsync(HashTerminator, cancellationToken);
            if (reply is null)
            {
                return (null, sawStale);
            }

            if (reply.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (reply, sawStale);
            }

            sawStale = true;
            logger?.LogDebug("SGP answered {Command} with {Reply}, a reply to an earlier command{Then}",
                command, reply, repeatable ? "; sending it again" : "; not repeating it");
            if (!repeatable)
            {
                break;
            }
        }

        return (null, sawStale);
    }
}

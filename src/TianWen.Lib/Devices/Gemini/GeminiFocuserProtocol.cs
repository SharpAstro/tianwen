using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Pure wire codec for the Gemini Focuser Pro serial protocol (see
/// <c>docs/architecture/gemini-focuser-pro-protocol.md</c>). The Gemini Focuser Pro is a rebadged
/// <c>myFocuserPro2</c> Arduino controller: commands are framed <c>':' + code + [arg] + '#'</c> and
/// responses are <c>&lt;response-char&gt; + payload + '#'</c> — a single leading status char (which the
/// decoder strips unconditionally, exactly as the vendor ASCOM driver's <c>Substring(1, len-2)</c> does)
/// followed by the value. <b>Get</b> commands reply; <b>set</b> commands (Move/Halt) are silent
/// fire-and-forget, so the driver never waits on them. Every method acquires the connection lock for its
/// own exchange, so the driver, the discovery probe, and unit tests all share one implementation over an
/// <see cref="ISerialConnection"/>.
/// </summary>
internal static class GeminiFocuserProtocol
{
    public const int Baud = 9600;

    // Payload returned by the ':02#' controller-present handshake on a myFocuserPro2 board.
    public const string PresentReply = "OK";

    // '#'-terminated framing (the LX200 family — see ProbeFraming.HashTerminated).
    private static readonly ReadOnlyMemory<byte> Terminator = new([(byte)'#']);

    // Bounded read for the reply-bearing set command (:23x# temp-comp toggle acks "OK"); a firmware that
    // stays silent just times out cleanly. The get commands are bounded by the caller's own token/budget.
    private static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Sends <c>:02#</c> (controller-present handshake) and returns the payload — <see cref="PresentReply"/> on a live myFocuserPro2 board, else null/other.</summary>
    public static ValueTask<string?> IdentifyAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => QueryAsync(conn, ":02#", cancellationToken);

    /// <summary>
    /// Sends <c>:04#</c> and parses the <c>&lt;name&gt;\r\n&lt;version&gt;</c> firmware reply. The Gemini
    /// Focuser Pro ships whatever myFocuserPro2 firmware name was flashed (there is no distinctive "Gemini"
    /// token on the wire — the vendor driver stores this name but never validates it), so we surface it as
    /// metadata rather than gating discovery on it.
    /// </summary>
    public static async ValueTask<(string Name, int? Version)?> GetFirmwareAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, ":04#", cancellationToken).ConfigureAwait(false);
        if (payload is not { Length: > 0 })
        {
            return null;
        }

        // Reply body (leading status char already stripped) is "<name>\r\n<version>". Split on the first CR;
        // the version is the digits that follow (skipping CR/LF). Tolerant of a name-only reply.
        var cr = payload.IndexOf('\r');
        if (cr < 0)
        {
            return (payload.Trim(), null);
        }

        var name = payload[..cr];
        var rest = payload[cr..].Trim();
        return (name, int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null);
    }

    /// <summary>Sends <c>:00#</c> and returns the current absolute position, or null when unavailable/unparseable.</summary>
    public static ValueTask<int?> GetPositionAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => QueryIntAsync(conn, ":00#", cancellationToken);

    /// <summary>Sends <c>:01#</c> and returns whether the focuser is moving (payload <c>1</c> = moving).</summary>
    public static async ValueTask<bool> GetIsMovingAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => await QueryIntAsync(conn, ":01#", cancellationToken).ConfigureAwait(false) is { } m && m != 0;

    /// <summary>Sends <c>:08#</c> and returns the maximum step position, or null when unavailable/unparseable.</summary>
    public static ValueTask<int?> GetMaxStepAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => QueryIntAsync(conn, ":08#", cancellationToken);

    /// <summary>Sends <c>:06#</c> and returns the temperature in °C, or NaN when unavailable/unparseable.</summary>
    public static async ValueTask<double> GetTemperatureAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, ":06#", cancellationToken).ConfigureAwait(false);
        return double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN;
    }

    /// <summary>Sends <c>:33#</c> and returns the step size in microns, or null when unavailable/unparseable.</summary>
    public static async ValueTask<double?> GetStepSizeAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, ":33#", cancellationToken).ConfigureAwait(false);
        return double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>Sends <c>:24#</c> and returns whether temperature compensation is currently enabled.</summary>
    public static async ValueTask<bool> GetTempCompAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => await QueryIntAsync(conn, ":24#", cancellationToken).ConfigureAwait(false) is { } t && t != 0;

    /// <summary>Sends <c>:25#</c> and returns whether the controller supports temperature compensation.</summary>
    public static async ValueTask<bool> GetTempCompAvailableAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => await QueryIntAsync(conn, ":25#", cancellationToken).ConfigureAwait(false) is { } t && t != 0;

    /// <summary>Enables (<c>:231#</c>) or disables (<c>:230#</c>) temperature compensation; this set command acks with <c>OK</c>.</summary>
    public static async ValueTask SetTempCompAsync(ISerialConnection conn, bool enabled, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!await conn.TryWriteAsync(enabled ? ":231#" : ":230#", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // The temp-comp toggle is the one set command the controller acks ("OK"); drain it (bounded) so it
        // can't offset the next query's read. A silent firmware just times out cleanly.
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ackCts.CancelAfter(AckTimeout);
        try
        {
            _ = await conn.TryReadTerminatedAsync(Terminator, ackCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No ack within AckTimeout — fine.
        }
    }

    /// <summary>Commands an absolute move to <paramref name="position"/> (<c>:05&lt;pos&gt;#</c>). Silent fire-and-forget — poll <see cref="GetIsMovingAsync"/>.</summary>
    public static ValueTask MoveAsync(ISerialConnection conn, int position, CancellationToken cancellationToken)
        => SendAsync(conn, $":05{Math.Max(0, position).ToString(CultureInfo.InvariantCulture)}#", cancellationToken);

    /// <summary>Halts any motion (<c>:27#</c>). Silent fire-and-forget.</summary>
    public static ValueTask HaltAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => SendAsync(conn, ":27#", cancellationToken);

    private static async ValueTask<int?> QueryIntAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, command, cancellationToken).ConfigureAwait(false);
        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Writes a '#'-terminated command and returns the framed reply's payload (leading status char and trailing '#' stripped).</summary>
    private static async ValueTask<string?> QueryAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!await conn.TryWriteAsync(command, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var raw = await conn.TryReadTerminatedAsync(Terminator, cancellationToken).ConfigureAwait(false);
        return ParsePayload(raw);
    }

    /// <summary>
    /// Writes a '#'-terminated set command with no reply read. myFocuserPro2 set commands (Move/Halt) are
    /// silent, so — unlike the FlatPanel, which acks everything — there is nothing to drain here. Kept fast
    /// because the imaging/autofocus hot path issues moves frequently.
    /// </summary>
    private static async ValueTask SendAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        _ = await conn.TryWriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a framed reply by stripping the single leading status char and an optional trailing <c>'#'</c>
    /// (the read may or may not include the terminator). Mirrors the vendor driver's unconditional
    /// <c>Substring(1, len-2)</c>: the leading char is a status code, never part of the value.
    /// </summary>
    internal static string? ParsePayload(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var s = raw;
        if (s.EndsWith('#'))
        {
            s = s[..^1];
        }

        // Drop the leading status char. A zero-length body (bare status char) yields an empty payload.
        return s.Length >= 1 ? s[1..] : null;
    }
}

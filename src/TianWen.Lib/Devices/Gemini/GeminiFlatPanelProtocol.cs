using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Pure wire codec for the Gemini FlatPanel Lite serial protocol
/// (see <c>docs/architecture/gemini-flatpanel-lite-protocol.md</c>). Every message is framed
/// <c>'&gt;'</c> … <c>'#'</c>. Query commands (<c>H</c>/<c>V</c>/<c>S</c>/<c>J</c>) expect a
/// <c>&gt;</c>+letter+payload+<c>#</c> reply; action commands (<c>L</c>/<c>D</c>/<c>B</c>) are
/// fire-and-forget. Each method acquires the connection lock for its own exchange, so the driver,
/// the discovery probe, and unit tests all share one implementation over an
/// <see cref="ISerialConnection"/>.
/// </summary>
internal static class GeminiFlatPanelProtocol
{
    public const int Baud = 9600;
    public const int MaxBrightness = 255;
    public const int MinFirmwareVersion = 203;
    public const string Identity = "GeminiFlatPanelLite";

    // '#'-terminated framing (the LX200 family — see ProbeFraming.HashTerminated).
    private static readonly ReadOnlyMemory<byte> Terminator = new([(byte)'#']);

    /// <summary>Handshake: sends <c>&gt;H#</c> and returns the identity payload (should equal <see cref="Identity"/>), or null.</summary>
    public static ValueTask<string?> IdentifyAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => QueryAsync(conn, 'H', cancellationToken);

    /// <summary>Sends <c>&gt;V#</c> and returns the firmware version, or null when unavailable/unparseable.</summary>
    public static async ValueTask<int?> GetFirmwareVersionAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, 'V', cancellationToken).ConfigureAwait(false);
        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Sends <c>&gt;S#</c> and maps the light on/off status onto a <see cref="CalibratorStatus"/>.</summary>
    public static async ValueTask<CalibratorStatus> GetCalibratorStateAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, 'S', cancellationToken).ConfigureAwait(false);
        if (payload is not { Length: > 0 })
        {
            return CalibratorStatus.Unknown;
        }

        return payload[0] switch
        {
            '1' => CalibratorStatus.Ready,
            '0' => CalibratorStatus.Off,
            _ => CalibratorStatus.Unknown,
        };
    }

    /// <summary>Sends <c>&gt;J#</c> and returns brightness clamped to [0, <see cref="MaxBrightness"/>], or -1 on failure.</summary>
    public static async ValueTask<int> GetBrightnessAsync(ISerialConnection conn, CancellationToken cancellationToken)
    {
        var payload = await QueryAsync(conn, 'J', cancellationToken).ConfigureAwait(false);
        return int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            ? Math.Clamp(b, 0, MaxBrightness)
            : -1;
    }

    /// <summary>Turns the light on (<c>&gt;L#</c>) or off (<c>&gt;D#</c>).</summary>
    public static ValueTask SetLightAsync(ISerialConnection conn, bool on, CancellationToken cancellationToken)
        => SendAsync(conn, on ? ">L#" : ">D#", cancellationToken);

    /// <summary>Sets the panel brightness (<c>&gt;B&lt;n&gt;#</c>), clamped to [0, <see cref="MaxBrightness"/>].</summary>
    public static ValueTask SetBrightnessAsync(ISerialConnection conn, int brightness, CancellationToken cancellationToken)
        => SendAsync(conn, $">B{Math.Clamp(brightness, 0, MaxBrightness).ToString(CultureInfo.InvariantCulture)}#", cancellationToken);

    /// <summary>Writes a '#'-terminated query and reads the framed reply, returning the payload when the echoed letter matches.</summary>
    private static async ValueTask<string?> QueryAsync(ISerialConnection conn, char command, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!await conn.TryWriteAsync($">{command}#", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var raw = await conn.TryReadTerminatedAsync(Terminator, cancellationToken).ConfigureAwait(false);
        return ParsePayload(raw, command);
    }

    /// <summary>Writes a '#'-terminated fire-and-forget action command (no reply expected).</summary>
    private static async ValueTask SendAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        await conn.TryWriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a framed reply (<c>'&gt;'</c> + letter + payload + optional <c>'#'</c>) and returns the payload
    /// when the echoed letter matches <paramref name="expectedCommand"/>. Tolerant of the terminator being
    /// present or already stripped by the read, and of a leading <c>'&gt;'</c>.
    /// </summary>
    internal static string? ParsePayload(string? raw, char expectedCommand)
    {
        if (raw is null)
        {
            return null;
        }

        var s = raw.Trim();
        if (s.StartsWith('>'))
        {
            s = s[1..];
        }
        if (s.EndsWith('#'))
        {
            s = s[..^1];
        }

        return s.Length >= 1 && s[0] == expectedCommand ? s[1..] : null;
    }
}

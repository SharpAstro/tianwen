using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Gemini;

/// <summary>
/// Pure wire codec for the Gemini Power&amp;Data Hub Advanced 3 (see <c>docs/architecture/gemini-powerbox-adv3-protocol.md</c>):
/// commands are <c>'&gt;' + letter + [argument] + '#'</c> followed by a line feed, replies <c>'*' + letter + payload + '#'</c>.
/// Only the queries reply; a set is confirmed by nothing but the next status frame. Once asked for status (<c>&gt;G#</c>) the
/// box PUSHES a status frame every 3 s unasked, so a read can always meet a status frame where it expected another reply.
/// </summary>
internal static class GeminiPowerBoxProtocol
{
    public const int Baud = 19200;

    /// <summary>The identity reply's payload, after <c>*H</c>.</summary>
    public const string Identity = "GeminiPowerBoxPlusAdv3";

    /// <summary>The oldest firmware the vendor driver accepts (<c>&gt;V#</c> answers <c>*V309#</c> on the bench box, 2026-10-21).</summary>
    public const int MinimumFirmware = 308;

    /// <summary>How many frames a query reads past before giving up on its reply: status frames the box pushed in between.</summary>
    private const int FramesToSkip = 4;

    private static readonly ReadOnlyMemory<byte> Terminator = new([(byte)'#']);

    /// <summary>The letters a status frame's values are each FOLLOWED by, in the vendor driver's split set.</summary>
    private static readonly char[] StatusTags = ['D', 'U', 'A', 'T', 'M', 'B', 'C', 'S', 'H', 'V', 'P'];

    /// <summary>
    /// The output number <c>&gt;O</c>/<c>&gt;C</c> take for USB A..F (index 0..5): 6..11, two digits for E and F. DC2..DC5 are
    /// their own numbers, 2..5; DC1 is always on and is never sent.
    /// </summary>
    public static int UsbOutputNumber(int usbIndex) => 6 + usbIndex;

    /// <summary>Sends <c>&gt;H#</c> and returns the identity payload (<see cref="Identity"/> on the box), or null.</summary>
    public static ValueTask<string?> IdentifyAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => QueryAsync(conn, 'H', cancellationToken);

    /// <summary>Sends <c>&gt;V#</c> and returns the firmware as the box writes it (<c>309</c>), or null.</summary>
    public static async ValueTask<int?> GetFirmwareAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => int.TryParse(await QueryAsync(conn, 'V', cancellationToken).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Asks for status (<c>&gt;G#</c>), which also starts the box pushing a frame every 3 s. Nothing is read here.</summary>
    public static ValueTask<bool> RequestStatusAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => SendAsync(conn, ">G#", cancellationToken);

    /// <summary>Switches output <paramref name="output"/> on (<c>&gt;O&lt;n&gt;#</c>) or off (<c>&gt;C&lt;n&gt;#</c>). Silent.</summary>
    public static ValueTask<bool> SwitchAsync(ISerialConnection conn, int output, bool on, CancellationToken cancellationToken)
        => SendAsync(conn, $">{(on ? 'O' : 'C')}{output.ToString(CultureInfo.InvariantCulture)}#", cancellationToken);

    /// <summary>
    /// Sets a dew heater's MANUAL power, a whole percent (<c>&gt;X&lt;p&gt;#</c> for DEW6, <c>&gt;Y&lt;p&gt;#</c> for DEW7). Silent, and
    /// heeded only in the box's Manual mode, which this codec never sets.
    /// </summary>
    public static ValueTask<bool> SetDewPowerAsync(ISerialConnection conn, int dew, int percent, CancellationToken cancellationToken)
        => SendAsync(conn, $">{(dew == 6 ? 'X' : 'Y')}{Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture)}#", cancellationToken);

    /// <summary>
    /// Reads the next frame (<c>*</c>, a letter, a payload, <c>#</c>) and returns its letter and payload, or null when none
    /// came within the token.
    /// </summary>
    public static async ValueTask<(char Letter, string Payload)?> ReadFrameAsync(ISerialConnection conn, CancellationToken cancellationToken)
        => ParseFrame(await conn.TryReadTerminatedAsync(Terminator, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// A frame as read (with or without its <c>#</c>, after anything before its <c>*</c>, which a read joining the stream
    /// mid-frame leaves) as its letter and payload, or null when it holds no <c>*</c> and letter.
    /// </summary>
    internal static (char Letter, string Payload)? ParseFrame(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var start = raw.LastIndexOf('*');
        var end = raw.EndsWith('#') ? raw.Length - 1 : raw.Length;
        return start >= 0 && end - start >= 2 ? (raw[start + 1], raw[(start + 2)..end]) : null;
    }

    /// <summary>
    /// A status frame's payload (after <c>*G</c>) as its seventeen fields, each value FOLLOWED by its letter: measured on the
    /// bench box, <c>1111D111111U1A1T1A1M1B1M100C100C 24.81S 26.87T 56.00H 17.35D12.9V 0.16C  2.06P</c>. Null when the payload
    /// does not split into seventeen fields of the right shape.
    /// </summary>
    internal static GeminiPowerBoxStatus? ParseStatus(string payload)
    {
        var fields = payload.Split(StatusTags);
        // Seventeen values and the empty string after the last letter, exactly.
        if (fields.Length != 18 || fields[17].Length != 0 || fields[0].Length != 4 || fields[1].Length != 6)
        {
            return null;
        }

        return new GeminiPowerBoxStatus(
            DcOn: [.. fields[0].Select(c => c == '1')],
            UsbOn: [.. fields[1].Select(c => c == '1')],
            HasAmbientSensor: fields[2].Trim() == "1",
            HasLensProbe: fields[3].Trim() == "1",
            Dew6Enabled: fields[4].Trim() == "1",
            Dew6Mode: ParseMode(fields[5]),
            Dew7Enabled: fields[6].Trim() == "1",
            Dew7Mode: ParseMode(fields[7]),
            Dew6Power: Number(fields[8]),
            Dew7Power: Number(fields[9]),
            LensTemperature: Number(fields[10]),
            AmbientTemperature: Number(fields[11]),
            Humidity: Number(fields[12]),
            DewPoint: Number(fields[13]),
            Voltage: Number(fields[14]),
            Current: Number(fields[15]),
            Power: Number(fields[16]));

        static GeminiPowerBoxDewMode ParseMode(string field) => field.Trim() switch
        {
            "0" => GeminiPowerBoxDewMode.Auto,
            "1" => GeminiPowerBoxDewMode.Manual,
            "2" => GeminiPowerBoxDewMode.Switch,
            _ => GeminiPowerBoxDewMode.Unknown,
        };

        // The box pads numbers with spaces (" 24.81", "  2.06") and writes a point whatever the PC's locale: the vendor
        // driver parses with the CURRENT culture, so on a comma-decimal Windows it read 12.9 V as 129.
        static double Number(string field) => double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    /// <summary>
    /// Sends a query and returns the payload of the first reply with its letter, reading past the status frames the box
    /// pushes in between (at most <see cref="FramesToSkip"/>).
    /// </summary>
    private static async ValueTask<string?> QueryAsync(ISerialConnection conn, char letter, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!await WriteLineAsync(conn, $">{letter}#", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        for (var i = 0; i <= FramesToSkip; i++)
        {
            // Nothing read is the end; something read that is not a frame is the TAIL of a status frame the read joined
            // midway (Serial.Lib ends a read at the first '#', so " 0.16C  2.06P" arrives alone, as after the probe
            // service discards its input buffer), and is read past like a whole one.
            if (await conn.TryReadTerminatedAsync(Terminator, cancellationToken).ConfigureAwait(false) is not { } raw)
            {
                return null;
            }

            if (ParseFrame(raw) is { Letter: var l, Payload: var payload } && l == letter)
            {
                return payload;
            }
        }

        return null;
    }

    private static async ValueTask<bool> SendAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
    {
        using var @lock = await conn.WaitAsync(cancellationToken).ConfigureAwait(false);

        // A caller that was cancelled while it waited for the lock (a teardown cancels, then closes the port) must not
        // write on a port that is closing: the wait can finish with the token already cancelled.
        cancellationToken.ThrowIfCancellationRequested();
        return await WriteLineAsync(conn, command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every command goes out with a line feed after its <c>#</c>, as the vendor driver's <c>WriteLine</c> sends it.</summary>
    private static ValueTask<bool> WriteLineAsync(ISerialConnection conn, string command, CancellationToken cancellationToken)
        => conn.TryWriteAsync(command + "\n", cancellationToken);
}

/// <summary>A dew heater's mode, which the box keeps and this driver never sets.</summary>
public enum GeminiPowerBoxDewMode
{
    Auto,
    Manual,
    Switch,
    Unknown,
}

/// <summary>One status frame of a Gemini Power&amp;Data Hub Advanced 3, as the box reported it.</summary>
/// <param name="DcOn">DC2..DC5 (DC1 is always on and not reported).</param>
/// <param name="UsbOn">USB A..F.</param>
/// <param name="HasAmbientSensor">The ambient temperature and humidity sensor (AHT20) is attached.</param>
/// <param name="HasLensProbe">The lens temperature probe (DS18B20) is attached.</param>
/// <param name="Dew6Enabled">DEW6 is on, in Auto or Switch mode.</param>
/// <param name="Dew6Mode">DEW6's mode.</param>
/// <param name="Dew7Enabled">DEW7 is on, in Auto or Switch mode.</param>
/// <param name="Dew7Mode">DEW7's mode.</param>
/// <param name="Dew6Power">DEW6's manual power, percent.</param>
/// <param name="Dew7Power">DEW7's manual power, percent.</param>
/// <param name="LensTemperature">The lens probe, C.</param>
/// <param name="AmbientTemperature">Ambient, C.</param>
/// <param name="Humidity">Relative humidity, percent.</param>
/// <param name="DewPoint">Dew point, C, as the box computes it.</param>
/// <param name="Voltage">The input voltage, V.</param>
/// <param name="Current">The DC outputs' total current, A.</param>
/// <param name="Power">The DC outputs' total power, W.</param>
public sealed record GeminiPowerBoxStatus(
    ImmutableArray<bool> DcOn,
    ImmutableArray<bool> UsbOn,
    bool HasAmbientSensor,
    bool HasLensProbe,
    bool Dew6Enabled,
    GeminiPowerBoxDewMode Dew6Mode,
    bool Dew7Enabled,
    GeminiPowerBoxDewMode Dew7Mode,
    double Dew6Power,
    double Dew7Power,
    double LensTemperature,
    double AmbientTemperature,
    double Humidity,
    double DewPoint,
    double Voltage,
    double Current,
    double Power);

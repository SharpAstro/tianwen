using System.Text.Json.Serialization;

namespace TianWen.Lib.Devices.QHYCCD;

/// <summary>
/// QHY QFOC Focuser serial protocol — JSON-over-UART at 9600 baud (8N1).
///
/// <para><b>Transport:</b> USB-to-serial (CDC/ACM). The host sends a JSON object;
/// the focuser replies with a JSON object terminated by <c>}</c>.
/// A 50 ms inter-command delay is recommended; the ASCOM driver uses 50 ms.</para>
///
/// <para><b>Hardware variants:</b></para>
/// <list type="bullet">
///   <item><b>Standard</b> — board version (bv) typically "1.x"</item>
///   <item><b>High Precision</b> — board version "2.x", finer micro-stepping</item>
/// </list>
/// Both variants use the same protocol; the board version is reported in the
/// <see cref="QfocInitResponse.BoardVersion"/> field.
///
/// <para><b>Positioning:</b> absolute, signed 32-bit integer steps (range ±1 000 000).
/// Configurable software limits (min/max) stored in the ASCOM profile.
/// The firmware does <em>not</em> persist position across power cycles —
/// the host should save/restore it.</para>
///
/// <para><b>Motor driver:</b> TMC stepper (Trinamic). Supports StallGuard
/// (stall/collision detection, cmd_id 18 sgthrs), configurable hold current
/// (cmd_id 16 ihold), and power-down mode (cmd_id 19 pdn).
/// When 12 V external supply is present (<see cref="QfocTemperatureResponse.Is12vPresent"/>),
/// higher motor currents (irun=30, ihold configurable) are available;
/// on USB power alone defaults are irun=8, ihold=4.</para>
///
/// <para><b>Temperature:</b> dual sensors — external NTC probe (<c>o_t</c>)
/// and on-chip (<c>c_t</c>), both reported in milli-°C. The external NTC is
/// optional; when not connected <c>o_t</c> reads a rail value.</para>
///
/// <para><b>Command summary:</b></para>
/// <list type="table">
///   <listheader><term>cmd_id</term><description>Name / Purpose</description></listheader>
///   <item><term>1</term><description><c>init</c> — handshake; returns firmware version + board version</description></item>
///   <item><term>3</term><description><c>stop</c> — halt movement immediately</description></item>
///   <item><term>4</term><description><c>temp</c> — query temperature, StallGuard alarm, 12 V status</description></item>
///   <item><term>5</term><description><c>pos</c> — query current position</description></item>
///   <item><term>6</term><description><c>runto</c> — absolute move to target position (<c>tar</c>)</description></item>
///   <item><term>7</term><description>set reverse direction flag (<c>rev</c>: 0/1)</description></item>
///   <item><term>11</term><description><c>reset_fmc</c> — reset position counter to zero</description></item>
///   <item><term>12</term><description><c>keep_force</c> — hold motor current when idle (<c>force</c>: 0/1)</description></item>
///   <item><term>13</term><description><c>set_speed</c> — motor speed (0 = normal, −2 = high)</description></item>
///   <item><term>16</term><description><c>ihold</c> — set motor run/hold current (<c>irun</c>, <c>ihold</c>)</description></item>
///   <item><term>17</term><description><c>tcool</c> — StallGuard coolstep threshold</description></item>
///   <item><term>18</term><description><c>sgthrs</c> — StallGuard sensitivity</description></item>
///   <item><term>19</term><description><c>pdn</c> — power-down mode (<c>pdn_d</c>: 0/1)</description></item>
/// </list>
///
/// <para><b>Response routing:</b> the <c>idx</c> field identifies the response type:
/// 1 = position (move completed or pos query), 4 = temperature/status.</para>
///
/// <para><b>Connection sequence</b> (from ASCOM driver):</para>
/// <list type="number">
///   <item>Open serial port at 9600 baud</item>
///   <item>Drain stale data, wait 150 ms</item>
///   <item>Send <c>init</c> (cmd_id 1), wait 300 ms, verify "version" in response</item>
///   <item>Send <c>temp</c> (cmd_id 4) to read 12 V status</item>
///   <item>Optionally send <c>reset_fmc</c> (cmd_id 11) to zero the position counter</item>
///   <item>Send <c>set_speed</c> (cmd_id 13)</item>
///   <item>Send <c>keep_force</c> (cmd_id 12), configure ihold/irun if 12 V present</item>
///   <item>Send <c>sgthrs</c> (cmd_id 18) and <c>tcool</c> (cmd_id 17)</item>
///   <item>Send reverse direction (cmd_id 7)</item>
///   <item>Query initial position via <c>pos</c> (cmd_id 5)</item>
///   <item>Start periodic polling (alternating temp/pos every 1.5 s)</item>
/// </list>
///
/// <para><b>Reverse-engineered from:</b> ASCOM.qfoc.Focuser.dll v1.0.9006.24080 (QHY, .NET Framework 4.0).</para>
/// </summary>
static class QfocProtocolDocs { }

#region Commands

/// <summary>
/// Base for all QFOC commands. The firmware identifies commands by <see cref="CmdId"/>.
/// <para>Wire format example: <c>{"cmd_id":5,"cmd_name":"pos"}</c></para>
/// </summary>
internal record QfocCommand(
    [property: JsonPropertyName("cmd_id")] int CmdId,
    [property: JsonPropertyName("cmd_name")] string CmdName
);

/// <summary>
/// cmd_id 1 — initialise the focuser and return firmware/board version.
/// <para>Wire: <c>{"cmd_id":1,"cmd_name":"init"}</c></para>
/// <para>Response: <see cref="QfocInitResponse"/> — <c>{"version":"20240828","bv":"2.0"}</c></para>
/// <para>Must be the first command after opening the serial port. Wait ≥ 300 ms for the response.</para>
/// </summary>
internal record QfocInitCommand() : QfocCommand(1, "init");

/// <summary>
/// cmd_id 3 — halt movement immediately.
/// <para>Wire: <c>{"cmd_id":3,"cmd_name":"stop"}</c></para>
/// <para>No response. The focuser decelerates to a stop as fast as the motor driver allows.</para>
/// </summary>
internal record QfocStopCommand() : QfocCommand(3, "stop");

/// <summary>
/// cmd_id 4 — query temperature, StallGuard alarm, and 12 V status.
/// <para>Wire: <c>{"cmd_id":4,"cmd_name":"temp"}</c></para>
/// <para>Response: <see cref="QfocTemperatureResponse"/> —
/// <c>{"idx":4,"o_t":15200,"c_t":32100,"sg":0,"c_r":121}</c></para>
/// </summary>
internal record QfocTempCommand() : QfocCommand(4, "temp");

/// <summary>
/// cmd_id 5 — query current step position.
/// <para>Wire: <c>{"cmd_id":5,"cmd_name":"pos"}</c></para>
/// <para>Response: <see cref="QfocPositionResponse"/> — <c>{"idx":1,"pos":5000}</c></para>
/// </summary>
internal record QfocPosCommand() : QfocCommand(5, "pos");

/// <summary>
/// cmd_id 6 — absolute move to <see cref="Tar"/> position.
/// <para>Wire: <c>{"cmd_id":6,"cmd_name":"runto","tar":5000}</c></para>
/// <para>Response: <see cref="QfocPositionResponse"/> when the move completes —
/// <c>{"idx":1,"pos":5000}</c></para>
/// <para>The focuser reports <c>IsMoving</c> until the reported position matches the target.
/// Use cmd_id 3 (<see cref="QfocStopCommand"/>) to abort.</para>
/// </summary>
internal record QfocRuntoCommand(
    [property: JsonPropertyName("tar")] int Tar
) : QfocCommand(6, "runto");

/// <summary>
/// cmd_id 7 — set reverse direction flag.
/// <para>Wire: <c>{"cmd_id":7,"rev":1}</c></para>
/// <para>No named response. <c>rev</c> = 1 inverts the motor direction, 0 = normal.</para>
/// </summary>
internal record QfocReverseCommand(
    [property: JsonPropertyName("rev")] int Rev
) : QfocCommand(7, "rev");

/// <summary>
/// cmd_id 13 — set motor speed.
/// <para>Wire: <c>{"cmd_id":13,"cmd_name":"set_speed","speed":0}</c></para>
/// <para><c>speed</c> = 0 for normal, −2 for high speed. Wait 200 ms after sending.</para>
/// </summary>
internal record QfocSetSpeedCommand(
    [property: JsonPropertyName("speed")] int Speed
) : QfocCommand(13, "set_speed");

#endregion

#region Responses

/// <summary>
/// Init response — returned by cmd_id 1 (<see cref="QfocInitCommand"/>).
/// <para>Example: <c>{"version":"20240828","bv":"2.0"}</c></para>
/// </summary>
internal sealed class QfocInitResponse
{
    /// <summary>Firmware build date as yyyyMMdd (e.g. "20240828").</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Board version string distinguishing hardware variants.
    /// "1.x" = Standard, "2.x" = High Precision.
    /// </summary>
    [JsonPropertyName("bv")]
    public string? BoardVersion { get; set; }
}

/// <summary>
/// Position response (idx = 1) — returned after a <see cref="QfocPosCommand"/> query
/// or when a <see cref="QfocRuntoCommand"/> move completes.
/// <para>Example: <c>{"idx":1,"pos":5000}</c></para>
/// </summary>
internal sealed class QfocPositionResponse
{
    [JsonPropertyName("idx")]
    public int Idx { get; set; }

    /// <summary>Current absolute step position (signed 32-bit).</summary>
    [JsonPropertyName("pos")]
    public int Pos { get; set; }
}

/// <summary>
/// Temperature/status response (idx = 4) — returned by <see cref="QfocTempCommand"/>.
/// <para>Example: <c>{"idx":4,"o_t":15200,"c_t":32100,"sg":0,"c_r":121}</c></para>
/// <para>Temperatures are in milli-°C (divide by 1000 for °C).
/// The 12 V rail ADC (<c>c_r</c>) reads ~120 when 12 V is present;
/// divide by 10 for approximate voltage. Values &gt; 80 indicate external
/// power supply is connected.</para>
/// </summary>
internal sealed class QfocTemperatureResponse
{
    [JsonPropertyName("idx")]
    public int Idx { get; set; }

    /// <summary>
    /// External NTC temperature in milli-°C. Divide by 1000 for °C.
    /// When no external NTC probe is connected, reads a rail value (typically very high or very low).
    /// </summary>
    [JsonPropertyName("o_t")]
    public int ExternalNtcMilliC { get; set; }

    /// <summary>On-chip temperature in milli-°C. Divide by 1000 for °C. Always available.</summary>
    [JsonPropertyName("c_t")]
    public int ChipTempMilliC { get; set; }

    /// <summary>
    /// StallGuard alarm value. 0 = no alarm.
    /// Non-zero indicates the motor experienced a stall or collision event.
    /// Requires StallGuard to be configured via cmd_id 18 (sgthrs) and cmd_id 17 (tcool).
    /// </summary>
    [JsonPropertyName("sg")]
    public int StallGuardAlarm { get; set; }

    /// <summary>
    /// 12 V rail ADC reading. Divide by 10 for approximate voltage.
    /// Values &gt; 80 (i.e. &gt; 8.0 V) indicate external 12 V supply is connected.
    /// When powered via USB only, this reads near zero.
    /// </summary>
    [JsonPropertyName("c_r")]
    public int Rail12vAdc { get; set; }

    /// <summary>True when the 12 V supply is present (&gt; 8.0 V).</summary>
    public bool Is12vPresent => Rail12vAdc > 80;
}

#endregion

/// <summary>
/// AOT-safe JSON source generator context for the QFOC serial protocol.
/// </summary>
[JsonSerializable(typeof(QfocInitCommand))]
[JsonSerializable(typeof(QfocStopCommand))]
[JsonSerializable(typeof(QfocTempCommand))]
[JsonSerializable(typeof(QfocPosCommand))]
[JsonSerializable(typeof(QfocRuntoCommand))]
[JsonSerializable(typeof(QfocReverseCommand))]
[JsonSerializable(typeof(QfocSetSpeedCommand))]
[JsonSerializable(typeof(QfocInitResponse))]
[JsonSerializable(typeof(QfocPositionResponse))]
[JsonSerializable(typeof(QfocTemperatureResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class QfocJsonContext : JsonSerializerContext
{
}

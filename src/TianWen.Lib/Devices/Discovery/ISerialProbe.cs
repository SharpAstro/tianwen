using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// A single probe registered with <see cref="ISerialProbeService"/>. One probe == one
/// protocol handshake at one baud rate. Device sources register probes during DI setup;
/// the probe service opens each port once per baud, runs all probes for that baud
/// group sequentially against the shared open handle, and publishes matches keyed by
/// probe <see cref="Name"/>. Device sources read matches from the service during
/// <c>DiscoverAsync</c> — they no longer open ports themselves.
/// </summary>
public interface ISerialProbe
{
    /// <summary>
    /// Stable identifier (e.g. <c>"Skywatcher"</c>, <c>"OnStep"</c>, <c>"QFOC"</c>).
    /// Device sources look up matches via <see cref="ISerialProbeService.ResultsFor(string)"/>
    /// using this key — must be unique across all registered probes.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Baud rate this probe needs. Probes are grouped by baud; the service opens each
    /// port once per distinct baud to amortise the open cost across all probes that
    /// share it.
    /// </summary>
    int BaudRate { get; }

    /// <summary>
    /// How this probe's response is delimited on the wire. Within a baud group, probes
    /// are sorted by <see cref="ProbeFraming"/> so framed protocols run first and
    /// <see cref="ProbeFraming.Unframed"/> probes (fixed-length reads, no terminator)
    /// run last. Rationale: an unframed read that times out or over-reads can leave
    /// bytes in the device-side buffer that contaminate the next probe's response.
    /// Keeping the unframed probe at the tail means only it ever has to deal with
    /// stale bytes, and every framed probe before it exits cleanly on its terminator.
    /// Default <see cref="ProbeFraming.Unframed"/> — the conservative choice for new
    /// probes that haven't declared their framing.
    /// </summary>
    ProbeFraming Framing => ProbeFraming.Unframed;

    /// <summary>
    /// Encoding used for ASCII/text-based writes and reads. Binary probes typically
    /// use <see cref="Encoding.ASCII"/> — the encoding only matters when the probe
    /// calls string-based overloads on <see cref="ISerialConnection"/>.
    /// </summary>
    Encoding Encoding => Encoding.ASCII;

    /// <summary>
    /// Shared (default) lets the probe run alongside other probes in the same baud
    /// group on a shared port. ExclusiveBaud reserves the baud group for this probe
    /// only — reserved for future binary protocols with stateful handshakes.
    /// </summary>
    ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;

    /// <summary>
    /// Per-attempt deadline. The service links this to the outer cancellation token;
    /// a timeout cancels only the current attempt and allows <see cref="MaxAttempts"/>
    /// retries before the probe is considered "no match" on this port.
    /// </summary>
    TimeSpan Budget { get; }

    /// <summary>
    /// How many times to retry on timeout. Cold-start tolerant devices (OnStep ESP32)
    /// use 2; most use 1.
    /// </summary>
    int MaxAttempts => 1;

    /// <summary>
    /// Device-URI host names this probe can verify for pinned-port verification
    /// (see <see cref="ISerialProbeService"/>'s two-tier algorithm). Typically a
    /// single entry — the name of the <see cref="IDeviceSource{TDevice}"/> host that
    /// the probe publishes URIs under (e.g. <c>"OnStepDevice"</c>,
    /// <c>"SkywatcherDevice"</c>). Default empty means the probe doesn't participate
    /// in verification — pinned ports referencing its family always fall through to
    /// the general probe pass.
    /// </summary>
    IReadOnlyCollection<string> MatchesDeviceHosts => [];

    /// <summary>
    /// Run the handshake on an already-open connection. <paramref name="port"/> is the
    /// canonical enumerated form (<c>serial:COM5</c>) and is what the probe should
    /// stash inside <see cref="SerialProbeMatch.Port"/> and use when building the
    /// device URI. Return a match on success, null on no-match. Exceptions thrown here
    /// are caught by the service, logged with full <c>port/baud/probe</c> scope, and
    /// treated as no-match — probes should not try to catch-all themselves.
    /// </summary>
    ValueTask<SerialProbeMatch?> ProbeAsync(string port, ISerialConnection conn, CancellationToken cancellationToken);
}

/// <summary>
/// Exclusivity hint for <see cref="ISerialProbe"/> within a baud group.
/// </summary>
public enum ProbeExclusivity
{
    /// <summary>Can share the open handle with sibling probes in the same baud group.</summary>
    Shared,

    /// <summary>Claims the baud group exclusively — sibling probes are skipped on that port.</summary>
    ExclusiveBaud,
}

/// <summary>
/// Response framing of an <see cref="ISerialProbe"/>. The enum value doubles as a
/// sort priority within a baud group (lower = runs earlier). Ordering is chosen so
/// framed protocols — which exit cleanly on a terminator byte — run before unframed
/// probes that can over-read and leave stale bytes behind for the next probe.
/// </summary>
public enum ProbeFraming
{
    /// <summary>Response is terminated by <c>#</c> (LX200 family: Meade, OnStep, iOptron).</summary>
    HashTerminated = 0,

    /// <summary>Response is terminated by <c>\r</c> (Skywatcher).</summary>
    CarriageReturnTerminated = 1,

    /// <summary>Response is terminated by <c>}</c> (QFOC JSON).</summary>
    BraceTerminated = 2,

    /// <summary>
    /// No byte terminator — the probe reads a fixed byte count (e.g. QHYCFW3 "VRS"
    /// returns exactly 8 bytes). Runs last because a timed-out or over-read fixed-length
    /// read can desync the buffer for any probe that runs after it on the same handle.
    /// </summary>
    Unframed = 99,
}

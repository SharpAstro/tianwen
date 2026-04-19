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
    /// Run the handshake on an already-open connection. Return a match on success
    /// (carries the device URI to publish), null on no-match. Exceptions thrown here
    /// are caught by the service, logged with full <c>port/baud/probe</c> scope, and
    /// treated as no-match — probes should not try to catch-all themselves.
    /// </summary>
    ValueTask<SerialProbeMatch?> ProbeAsync(ISerialConnection conn, CancellationToken cancellationToken);
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

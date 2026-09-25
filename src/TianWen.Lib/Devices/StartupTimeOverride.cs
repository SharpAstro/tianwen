using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianWen.Lib.Devices;

/// <summary>
/// Reads the optional <c>TIANWEN_NOW</c> environment variable once at process start and exposes a
/// frozen wall-clock offset, so the session can be tested at a simulated instant (e.g. a real night
/// at the configured site while the machine clock says daytime) without a fake-time pump.
/// <para>
/// Set <c>TIANWEN_NOW</c> to an ISO-8601 timestamp -- ideally with an explicit offset, e.g.
/// <c>2026-06-21T22:00:00+10:00</c> (a value with no offset is read as the machine's local time).
/// When set and parseable, the single <see cref="ITimeProvider"/> registered by
/// <c>AddExternal</c> is wrapped in an <see cref="OffsetTimeProvider"/>, so every consumer that
/// resolves the clock from DI -- planner schedule, session loop, fake mount/camera, mount-reported
/// UTC -- jumps to that instant and then advances at real-time rate. Absent or unparseable: the
/// real system clock is used, i.e. exactly the previous behaviour. Dev/test only.
/// </para>
/// <para>
/// <b>A process another one starts shares its clock</b> through <see cref="OffsetEnvVarName"/>
/// (<see cref="ForAChildProcess"/>): the node a client starts, which would otherwise read the same
/// <c>TIANWEN_NOW</c> and anchor it at its OWN start, disagreeing with the client by the launch gap
/// (docs/plans/hardware-in-the-server.md, "Spawn and lifetime").
/// </para>
/// </summary>
public static class StartupTimeOverride
{
    /// <summary>Environment variable that, when set, anchors the clock to a simulated "now".</summary>
    public const string EnvVarName = "TIANWEN_NOW";

    /// <summary>
    /// The offset a parent process froze, handed to a process it starts (<see cref="ForAChildProcess"/>): it wins over
    /// <see cref="EnvVarName"/>, which the child inherits too and would anchor at its own start. A <see cref="TimeSpan"/>
    /// in the invariant constant format ("c").
    /// </summary>
    public const string OffsetEnvVarName = "TIANWEN_CLOCK_OFFSET";

    // Frozen on first access (process start) so the wired provider and any startup log agree exactly:
    // every later GetUtcNow() is real-now + this offset.
    private static readonly (DateTimeOffset SimulatedNow, TimeSpan Offset)? _frozen = Freeze(
        Environment.GetEnvironmentVariable(OffsetEnvVarName), Environment.GetEnvironmentVariable(EnvVarName), DateTimeOffset.UtcNow);

    /// <summary>The clock this process runs on: an inherited offset, else a simulated instant, else none.</summary>
    internal static (DateTimeOffset SimulatedNow, TimeSpan Offset)? Freeze(string? inheritedOffset, string? simulatedNow, DateTimeOffset realNow)
    {
        if (TimeSpan.TryParseExact(inheritedOffset, "c", CultureInfo.InvariantCulture, out var offset))
        {
            return (realNow + offset, offset);
        }

        return TryParse(simulatedNow, realNow, out var now, out var off) ? (now, off) : null;
    }

    /// <summary>
    /// What a process this one starts needs in its environment to run on this process's clock: the offset, as frozen
    /// here, and no <see cref="EnvVarName"/>. Empty when this process runs on the real clock: the child then reads
    /// its own environment, as a node started by hand does. A null value removes the variable.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ForAChildProcess() => _frozen is { } frozen
        ? new Dictionary<string, string?>
        {
            [OffsetEnvVarName] = frozen.Offset.ToString("c", CultureInfo.InvariantCulture),
            [EnvVarName] = null,
        }
        : new Dictionary<string, string?>();

    /// <summary>Whether a valid <see cref="EnvVarName"/> override is active for this process.</summary>
    public static bool IsActive => _frozen is not null;

    /// <summary>Raw env-var value (for diagnostics), or <c>null</c> when unset.</summary>
    public static string? RawValue => Environment.GetEnvironmentVariable(EnvVarName);

    /// <summary>
    /// Returns the frozen simulated "now" and the fixed offset from the real clock, when active.
    /// </summary>
    public static bool TryGet(out DateTimeOffset simulatedNow, out TimeSpan offset)
    {
        if (_frozen is { } f)
        {
            simulatedNow = f.SimulatedNow;
            offset = f.Offset;
            return true;
        }

        simulatedNow = default;
        offset = default;
        return false;
    }

    /// <summary>
    /// Pure parse used by the frozen field and by tests: parses <paramref name="raw"/> as an
    /// ISO-8601 timestamp (no offset = machine-local) and derives the offset from
    /// <paramref name="realNow"/>. Returns <c>false</c> for null/blank/unparseable input.
    /// </summary>
    internal static bool TryParse(string? raw, DateTimeOffset realNow, out DateTimeOffset simulatedNow, out TimeSpan offset)
    {
        simulatedNow = default;
        offset = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out simulatedNow))
        {
            return false;
        }

        offset = simulatedNow - realNow;
        return true;
    }
}

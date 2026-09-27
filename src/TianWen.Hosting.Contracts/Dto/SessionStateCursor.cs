using System;
using System.Globalization;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Where a client's copy of a session's histories ends (P5b part 7 of docs/plans/hardware-in-the-server.md): the session it
/// holds them for, and how far into each it has got. <c>GET /session/state</c> named with it answers each history from
/// there, so a night's exposure log crosses once rather than whole on every poll (about 0.8 MB a poll at four OTAs over
/// ten hours). A cursor naming another session than the one on show gets every history whole.
/// </summary>
/// <param name="SessionId">The session the client holds the histories of (<see cref="SessionStateDto.SessionId"/>).</param>
/// <param name="ExposureLog">Entries of the exposure log held.</param>
/// <param name="FocusHistory">Focus runs held.</param>
/// <param name="CoolingSamples">Cooling samples held.</param>
/// <param name="PhaseTimeline">Phases held.</param>
/// <param name="GuideSteps">Guide steps taken up to the newest held: the next wanted is this one.</param>
public readonly record struct SessionStateCursor(
    Guid SessionId, int ExposureLog, int FocusHistory, int CoolingSamples, int PhaseTimeline, long GuideSteps)
{
    /// <summary>The query <c>GET /session/state</c> takes it as.</summary>
    public string ToQuery() => string.Create(CultureInfo.InvariantCulture,
        $"session={SessionId:D}&exposures={ExposureLog}&focus={FocusHistory}&cooling={CoolingSamples}&phases={PhaseTimeline}&guide={GuideSteps}");

    /// <summary>
    /// The cursor a request names, read through <paramref name="query"/> (a query value by key); null when it names none, or
    /// one that does not read, which is then answered whole.
    /// </summary>
    public static SessionStateCursor? FromQuery(Func<string, string?> query) =>
        Guid.TryParse(query("session"), out var session)
        && Count(query("exposures")) is { } exposures
        && Count(query("focus")) is { } focus
        && Count(query("cooling")) is { } cooling
        && Count(query("phases")) is { } phases
        && long.TryParse(query("guide"), NumberStyles.None, CultureInfo.InvariantCulture, out var guide)
            ? new SessionStateCursor(session, exposures, focus, cooling, phases, guide)
            : null;

    private static int? Count(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
}

/// <summary>
/// Where each history in a <see cref="SessionStateDto"/> starts in the session's whole history (P5b part 7): an index into
/// the exposure log, the focus history, the cooling samples and the phase timeline, and for the guide steps, which are a
/// window of the latest, the number of the first one sent. A client appends each to what it holds from there.
/// </summary>
public sealed class HistoryFromDto
{
    public required int ExposureLog { get; init; }
    public required int FocusHistory { get; init; }
    public required int CoolingSamples { get; init; }
    public required int PhaseTimeline { get; init; }
    public required long GuideSteps { get; init; }
}

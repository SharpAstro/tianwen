namespace TianWen.Lib.Sequencing;

/// <summary>
/// The run a session's node reports it is going on on that session (<see cref="ISessionTelemetry.Run"/>, P5b part 4 of
/// docs/plans/hardware-in-the-server.md): what a mirror knows of a run, since the flags an in-process host sets as it
/// starts one (the live view's running flag, its mode) are set by nobody on a rig's side.
/// </summary>
public enum ReportedRun
{
    /// <summary>No run is going on on this session: none started, or the last one has ended.</summary>
    None,

    /// <summary>An imaging session's run.</summary>
    Session,

    /// <summary>A flat-frame run on its own.</summary>
    Flats,

    /// <summary>
    /// The node serves no session at all: none has run since it started, so there is none to show (P5b part 9). Unlike
    /// <see cref="None"/>, whose session is still there to read (how it ended), this is an idle rig, as a view holding no
    /// session is on this computer.
    /// </summary>
    NoSession,
}

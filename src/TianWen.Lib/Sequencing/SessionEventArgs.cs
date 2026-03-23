using System;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Event args for <see cref="ISession.PhaseChanged"/>.
/// </summary>
public sealed class SessionPhaseChangedEventArgs(SessionPhase oldPhase, SessionPhase newPhase) : EventArgs
{
    public SessionPhase OldPhase { get; } = oldPhase;
    public SessionPhase NewPhase { get; } = newPhase;
}

/// <summary>
/// Event args for <see cref="ISession.FrameWritten"/>.
/// </summary>
public sealed class FrameWrittenEventArgs(ExposureLogEntry entry) : EventArgs
{
    public ExposureLogEntry Entry { get; } = entry;
}

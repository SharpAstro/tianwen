using System;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// A Synta motor-board command the mount's SAFETY depended on did not take effect, and retrying
/// it did not help.
/// </summary>
/// <remarks>
/// <para>Raised only by <c>SendCommandVerifiedAsync</c>, which is used for the handful of commands
/// that put an axis back into a safe state after a pulse: restoring the sidereal step period
/// (<c>:I1</c>) and stopping an axis that a pulse started (<c>:K1</c> / <c>:K2</c>). Ordinary
/// commands stay best-effort and merely log, because a rejection there costs one guide correction
/// and the next frame re-issues it.</para>
///
/// <para><b>Why these commands and not the others.</b> Every other command in the driver fails
/// FORWARD: a lost <c>:G</c> loses a pulse, a lost <c>:J</c> loses a move, and the guide loop
/// corrects for both on its next frame. The restore commands fail BACKWARD -- the mount is left
/// running at a rate the driver believes it has already cancelled. An unrestored <c>:I1</c> tracks
/// RA at up to 2x sidereal (or, after an East pulse at 1.0x, at a thousandth of it) for the rest of
/// the night, and an unacknowledged <c>:K</c> leaves an axis slewing with nothing scheduled to stop
/// it. Neither is self-correcting and neither is visible except as ruined subframes, so these are
/// the commands worth costing a fault over.</para>
///
/// <para>The fault reaches the session through the guider: it propagates out of
/// <c>StartPulseGuideAsync</c>, out of the guide loop, and is turned into a <c>GuidingErrorEvent</c> by
/// <c>BuiltInGuiderDriver</c>, which the session drains, logs by name and answers by restarting the
/// guider. There is deliberately no new plumbing for it.</para>
/// </remarks>
public sealed class SkywatcherDriverException : Exception
{
    internal SkywatcherDriverException(char cmd, char axis, string? lastResponse, int attempts)
        : base($"Skywatcher command :{cmd}{axis} was not acknowledged after {attempts} attempts "
             + $"(last response: {Describe(lastResponse)}). The axis may still be running at the "
             + "pulse rate.")
    {
        Data["Command"] = $":{cmd}{axis}";
        Data["Response"] = Describe(lastResponse);
        Data["Attempts"] = attempts;
    }

    /// <summary>
    /// Render an acknowledgement for a human. A <c>null</c> read is a TIMEOUT, not an empty answer,
    /// and the two mean different things when reading a log: no answer says the board never
    /// replied (or the line is desynced), while <c>!2</c> says it replied and refused.
    /// </summary>
    private static string Describe(string? response) => response switch
    {
        null or "" => "no answer",
        _ => $"\"{response.TrimEnd('\r')}\""
    };
}

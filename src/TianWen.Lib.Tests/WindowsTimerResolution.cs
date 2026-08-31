using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Lib.Tests;

/// <summary>
/// Raises the Windows system timer resolution for the lifetime of a scope, so a poll written as
/// <c>Task.Delay(1)</c> waits about a millisecond instead of the default ~15.6 ms scheduling
/// quantum.
/// <para>
/// This exists because a wait we cannot express as a signal has a FLOOR, and on Windows that floor
/// is the quantum, not the number we asked for. Measured directly over 200 iterations:
/// <c>Task.Delay(1)</c> takes <b>15.73 ms</b> by default, <b>1.52 ms</b> while this is held, and
/// 15.70 ms again once it is released. Nothing in the source says 15.7 ms; every delay in it asks
/// for 1 ms. Holding it for the pumped run takes the functional suite from 3m05 to 1m20.
/// </para>
/// <para>
/// It removes a floor; it does not make a slow test fast. Where the pump is genuinely waiting for
/// the session loop to make progress rather than for its own timer, a finer clock just samples the
/// same wait more often -- the camera-coupled case in <c>SessionObservationLoopTests</c> is exactly
/// that, and was unmoved by this. Beware the shape of the evidence there: the pump's wait had
/// totalled 165 s across 10,445 polls at 15.8 ms, which multiplies out perfectly and still was not
/// the cause. A rate that fits the total is not causation; removing the rate and watching the total
/// is.
/// </para>
/// <para>
/// Deliberately TEST-ONLY. Raising the system timer resolution costs power and extra wakeups
/// process-wide, which is a fair trade for a test run that is otherwise spending its time asleep
/// and no trade at all for a user's machine -- production code gets its precision from
/// <c>ITimeProvider</c> instead. The right fix for a given wait is still a signal where one can be
/// had (and one WAS tried for this pump, and was worse: the waits that remained fell through to
/// their backstop). This is for the waits where it cannot.
/// </para>
/// </summary>
internal static partial class WindowsTimerResolution
{
    /// <summary>
    /// One millisecond: the finest period Windows accepts here, and about the practical floor.
    /// </summary>
    private const uint PeriodMs = 1;

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    [SupportedOSPlatform("windows")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    [SupportedOSPlatform("windows")]
    private static partial uint TimeEndPeriod(uint period);

    /// <summary>
    /// Raises the resolution until the returned scope is disposed. A no-op off Windows, where a
    /// short delay already resolves finely enough that this problem does not arise.
    /// </summary>
    /// <remarks>
    /// Windows reference-counts these requests and honours the finest outstanding one, so nesting
    /// and concurrent test collections are safe: a scope closing never coarsens the clock under a
    /// scope that is still open.
    /// </remarks>
    public static Scope Raise() => new Scope(OperatingSystem.IsWindows() && TimeBeginPeriod(PeriodMs) == 0);

    /// <summary>Undoes one <see cref="Raise"/>. A struct so the common path allocates nothing.</summary>
    internal readonly struct Scope(bool raised) : IDisposable
    {
        public void Dispose()
        {
            // The platform check is repeated rather than inferred from `raised`: the analyser cannot
            // see that only Windows ever sets it, and CA1416 is right to insist the call site says so.
            if (raised && OperatingSystem.IsWindows())
            {
                TimeEndPeriod(PeriodMs);
            }
        }
    }
}

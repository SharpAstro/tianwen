using System;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Logging;

/// <summary>
/// <see cref="UnhandledExceptions"/> written to a program's log (#1811). Every TianWen program that keeps a file log
/// calls <see cref="HookForTheProcess"/> once, as soon as it has a logger. The two that keep none
/// (<c>tianwen-mcp</c>, <c>tianwen-ascomhost</c>) call <see cref="UnhandledExceptions.HookForTheProcess"/> with a
/// writer to stderr instead. <c>EveryProgramLogsItsUnhandledExceptions</c> holds a new program to one of the two.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A windowed program has no console to die on. A crash in <c>tianwen-gui</c> or <c>tianwen-fits</c>
/// reached only a redirected stderr, and nothing at all when started from the Start menu or by a file association;
/// the node's keeper journals THAT the node died, not why. And a task that faults with nobody awaiting it was lost
/// without a word.</para>
/// <para><b>The crash is Critical and the unawaited exception is an Error.</b> The second does not end anything: the
/// runtime raises it when the task is collected, which can be long after it faulted, so its line says what happened
/// and not when.</para>
/// <para><b>The file log is drained before the crash handler returns (#541).</b> Its lines are queued and written by
/// a thread of its own, so the crash line is not on disk when <c>LogCritical</c> returns; the handler then waits for
/// every live <see cref="FileLoggerProvider"/> to write what it holds (<see cref="FileLoggerProvider.FlushAll"/>),
/// for a bounded time. An unhandled exception on one thread does not stop the others before the handlers have run,
/// so the writer thread is there to do it.</para>
/// </remarks>
public static class UnhandledExceptionLog
{
    /// <summary>Hooks both kinds for the life of the process, written through <paramref name="logger"/>.</summary>
    public static void HookForTheProcess(ILogger logger)
        => UnhandledExceptions.HookForTheProcess(Crash(logger), Unobserved(logger));

    /// <summary><see cref="HookForTheProcess"/> for a test, which takes the hooks off again by disposing the result.</summary>
    internal static IDisposable Hook(ILogger logger) => UnhandledExceptions.Hook(Crash(logger), Unobserved(logger));

    internal static Action<string, Exception?> Crash(ILogger logger)
        => (text, exception) =>
        {
            logger.LogCritical(exception, "{What}", text);
            // The file log writes on a thread of its own (#541): the process is about to end, so what is queued,
            // this line with it, is written before the handler returns.
            // A log that does not get there in the time it is given (a dead disk) has nowhere left to say so.
            _ = FileLoggerProvider.FlushAll();
        };

    private static Action<string, Exception> Unobserved(ILogger logger)
        => (text, exception) => logger.LogError(exception, "{What}", text);
}

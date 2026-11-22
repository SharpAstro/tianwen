using System;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Logging;

/// <summary>
/// <see cref="UnhandledExceptions"/> written to a program's log (#1811). Every TianWen program calls
/// <see cref="HookForTheProcess"/> once, as soon as it has a logger; <c>EveryProgramLogsItsUnhandledExceptions</c> holds
/// a new one to it.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A windowed program has no console to die on. A crash in <c>tianwen-gui</c> or <c>tianwen-fits</c>
/// reached only a redirected stderr, and nothing at all when started from the Start menu or by a file association;
/// the node's keeper journals THAT the node died, not why. And a task that faults with nobody awaiting it was lost
/// without a word.</para>
/// <para><b>The crash is Critical and the unawaited exception is an Error.</b> The second does not end anything: the
/// runtime raises it when the task is collected, which can be long after it faulted, so its line says what happened
/// and not when.</para>
/// <para><b>The file log's lock (#541).</b> A handler runs on the thread that crashed, or on the finalizer thread, and
/// writes through the same lock as every other line. That lock is re-entrant, so a crash while this thread holds it
/// writes; and one held by another thread is held for one line's write, which that thread finishes, since an
/// unhandled exception on one thread does not stop the others before the handlers have run.</para>
/// </remarks>
public static class UnhandledExceptionLog
{
    /// <summary>Hooks both kinds for the life of the process, written through <paramref name="logger"/>.</summary>
    public static void HookForTheProcess(ILogger logger)
        => UnhandledExceptions.HookForTheProcess(Crash(logger), Unobserved(logger));

    /// <summary><see cref="HookForTheProcess"/> for a test, which takes the hooks off again by disposing the result.</summary>
    internal static IDisposable Hook(ILogger logger) => UnhandledExceptions.Hook(Crash(logger), Unobserved(logger));

    private static Action<string, Exception?> Crash(ILogger logger)
        => (text, exception) => logger.LogCritical(exception, "{What}", text);

    private static Action<string, Exception> Unobserved(ILogger logger)
        => (text, exception) => logger.LogError(exception, "{What}", text);
}

using System;
using System.Threading.Tasks;

namespace TianWen.Lib.Logging;

/// <summary>
/// The two kinds of exception nobody handled, handed to a program's own way of writing them down before they are
/// lost (#1811): the one that is about to end the process, and the one a task ended with that nobody awaited.
/// </summary>
/// <remarks>
/// <para>This file uses nothing but the base library, so the one program without a logger
/// (<c>tianwen-ascomhost</c>, which links its sources one by one) takes it as it is. A program with a logger calls
/// <see cref="UnhandledExceptionLog"/>.</para>
/// <para><b>A writer that fails is swallowed here, on purpose.</b> These run while the process is already going
/// down for another reason, and an exception out of a handler would replace the one being reported.</para>
/// </remarks>
public static class UnhandledExceptions
{
    /// <summary>What is written before an unhandled exception that ends the process.</summary>
    public const string Ending = "Unhandled exception, the process is ending";

    /// <summary>What is written before an unhandled exception the runtime says does not end the process.</summary>
    public const string NotEnding = "Unhandled exception";

    /// <summary>What is written before an exception a task ended with and nobody awaited or read.</summary>
    public const string Unobserved = "A task ended with an exception that nobody awaited";

    /// <summary>
    /// Hooks both for the life of the process. Call it once, as soon as the program can write anything down.
    /// </summary>
    /// <param name="crash">Writes an unhandled exception: its text, and the exception where the thrown object is one
    /// (anything can be thrown by code that is not C#).</param>
    /// <param name="unobserved">Writes a task's unawaited exception.</param>
    public static void HookForTheProcess(Action<string, Exception?> crash, Action<string, Exception> unobserved)
        => _ = Hook(crash, unobserved);

    /// <summary><see cref="HookForTheProcess"/> for a test, which takes the hooks off again by disposing the result.</summary>
    internal static IDisposable Hook(Action<string, Exception?> crash, Action<string, Exception> unobserved)
    {
        UnhandledExceptionEventHandler onCrash = (_, e) => OnCrash(crash, e.ExceptionObject, e.IsTerminating);
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) => OnUnobserved(unobserved, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += onCrash;
        TaskScheduler.UnobservedTaskException += onUnobserved;
        return new Hooks(onCrash, onUnobserved);
    }

    internal static void OnCrash(Action<string, Exception?> crash, object thrown, bool terminating)
    {
        var text = terminating ? Ending : NotEnding;
        try
        {
            // Not every thrown object is an Exception: one that is not is written by what it says of itself.
            crash(thrown is Exception ? text : $"{text} ({thrown})", thrown as Exception);
        }
        catch (Exception)
        {
            // See the remark on the class: the process is going down for what is being written, not for this.
        }
    }

    internal static void OnUnobserved(Action<string, Exception> unobserved, AggregateException exception)
    {
        try
        {
            unobserved(Unobserved, Unwrapped(exception));
        }
        catch (Exception)
        {
            // As above.
        }
    }

    /// <summary>
    /// The exception itself where the task ended with one, which is the usual case and reads better than an
    /// aggregate of one; the flattened aggregate where it ended with several.
    /// </summary>
    internal static Exception Unwrapped(AggregateException exception)
        => exception.Flatten() is { InnerExceptions: [var only] } ? only : exception.Flatten();

    private sealed class Hooks(UnhandledExceptionEventHandler onCrash, EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved) : IDisposable
    {
        public void Dispose()
        {
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }
}

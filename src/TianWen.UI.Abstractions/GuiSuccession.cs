using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A window replaced by a fresh process when its GPU cannot draw again (P7 of docs/plans/hardware-in-the-server.md, #937).
/// The rig is the node's since P6, so nothing of the night is in the window: a successor finds the node, reads the session
/// it runs and beats presence within the grace an interactive run is given, while the window whose device is wedged leaves
/// without a Vulkan teardown, which blocks for ever on a hung device (the prior art in docs/plans/gpu-device-recovery.md).
/// A fresh process gets a fresh driver instance, the one recovery a hung driver cannot defeat.
/// </summary>
/// <remarks>
/// The successor is marked by the environment, never an argument, so a user's own arguments (<c>--active</c>) pass through
/// unchanged, and it waits for its predecessor to exit before claiming the single-instance gate, which would otherwise hand
/// its start to the process that is dying. A boot loop is bounded twice: a generation cap, and a healthy uptime after which
/// the count starts again.
/// </remarks>
public static class GuiSuccession
{
    /// <summary>The process a successor replaces, whose exit it waits for.</summary>
    public const string PredecessorVariable = "TIANWEN_GUI_SUCCESSOR_OF";

    /// <summary>How many windows in a row have replaced one that could not draw: 0 for one a user started.</summary>
    public const string GenerationVariable = "TIANWEN_GUI_GENERATION";

    /// <summary>The most windows in a row that replace one that could not draw; the next one is not started.</summary>
    public const int MaxGeneration = 3;

    /// <summary>How long a window must have drawn for its own loss to start the count again.</summary>
    public static readonly TimeSpan HealthyUptime = TimeSpan.FromMinutes(5);

    /// <summary>How long a successor waits for its predecessor to exit before it starts anyway.</summary>
    public static readonly TimeSpan PredecessorExitBudget = TimeSpan.FromSeconds(15);

    /// <summary>The exit code of a window that left for a successor, as the prior art uses.</summary>
    public const int ReplacedExitCode = 70;

    /// <summary>This process's generation, from <paramref name="environment"/>: 0 for one a user started.</summary>
    public static int GenerationOf(Func<string, string?> environment)
        => int.TryParse(environment(GenerationVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            ? generation
            : 0;

    /// <summary>The process this one replaces, or null when a user started it.</summary>
    public static int? PredecessorOf(Func<string, string?> environment)
        => int.TryParse(environment(PredecessorVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;

    /// <summary>
    /// How to start the window that replaces this one, <paramref name="executable"/> with this process's own
    /// <paramref name="arguments"/>, or null when it is not to be replaced: <see cref="MaxGeneration"/> windows in a row
    /// have already failed to draw, each within <see cref="HealthyUptime"/> of its start.
    /// </summary>
    /// <param name="generation">This process's generation (<see cref="GenerationOf"/>).</param>
    /// <param name="uptime">How long this window has run.</param>
    public static ProcessStartInfo? SuccessorFor(string executable, IReadOnlyList<string> arguments, int processId, int generation, TimeSpan uptime)
    {
        var next = uptime >= HealthyUptime ? 1 : generation + 1;
        if (next > MaxGeneration)
        {
            return null;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[PredecessorVariable] = processId.ToString(CultureInfo.InvariantCulture);
        start.Environment[GenerationVariable] = next.ToString(CultureInfo.InvariantCulture);
        return start;
    }

    /// <summary>
    /// Leaves this window for its <paramref name="successor"/> (<see cref="SuccessorFor"/>): starts it, releases the
    /// instance <paramref name="gate"/>, then exits with <see cref="ReplacedExitCode"/> and no teardown, since the Vulkan
    /// teardown blocks for ever on a hung device. The gate goes BEFORE the exit because the exit can hang as well (a
    /// driver unloading on a hung device), and a gate held by a process that never ends would hand every later start,
    /// the successor's included, to a window that cannot draw. A successor that does not start changes none of that:
    /// the window leaves either way, and the rig goes on in the node, where a window the user opens again finds it.
    /// </summary>
    /// <param name="why">What ended this window, for the log: a lost or a failed display.</param>
    public static void LeaveFor(ProcessStartInfo successor, string why, IDisposable? gate, ILogger logger)
        => LeaveFor(successor, why, gate, logger, StartProcess, Environment.Exit);

    /// <inheritdoc cref="LeaveFor(ProcessStartInfo, string, IDisposable?, ILogger)"/>
    /// <param name="start">Starts a process and answers its id.</param>
    /// <param name="exit">Ends this process with an exit code.</param>
    internal static void LeaveFor(ProcessStartInfo successor, string why, IDisposable? gate, ILogger logger,
        Func<ProcessStartInfo, int?> start, Action<int> exit)
    {
        try
        {
            var pid = start(successor);
            logger.LogWarning("{Why}: a new window takes over (pid {Pid}); this one leaves, and the rig goes on on this computer's node.",
                why, pid);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "{Why}: the new window did not start; this one leaves, and the rig goes on on this computer's node.", why);
        }

        gate?.Dispose();
        exit(ReplacedExitCode);
    }

    private static int? StartProcess(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        return process?.Id;
    }

    /// <summary>How often a successor looks again whether its predecessor has exited.</summary>
    internal static readonly TimeSpan PredecessorPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Waits for the process this one replaces to exit, bounded by <paramref name="budget"/>: true once it has (or it had
    /// already), false when it is still running at the end.
    /// </summary>
    /// <remarks>
    /// The answer is always a LOOK at the process, taken at or after the end of the budget, never which of two callbacks
    /// ran first. <see cref="Process.WaitForExitAsync"/> raced its exit notification against the budget's cancellation,
    /// and on Unix the notification for a process this one did not start comes only from the runtime's own polling loop,
    /// which runs on the thread pool, while the budget's timer cancels without it: on a starved pool the budget won, and a
    /// predecessor that had exited 18 s earlier was reported still running (#1245). <see cref="Process.HasExited"/> on a
    /// process nobody is watching asks the system straight away (<c>kill(pid, 0)</c> on Unix, the handle on Windows).
    /// </remarks>
    public static async Task<bool> WaitForPredecessorAsync(int processId, TimeSpan budget, CancellationToken cancellationToken)
    {
        Process predecessor;
        try
        {
            predecessor = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Gone already.
            return true;
        }

        using (predecessor)
        {
            return await WaitForExitAsync(() => HasExited(predecessor), budget, TimeProvider.System,
                static (delay, ct) => Task.Delay(delay, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasExited(Process predecessor)
        => predecessor.HasExited || OperatingSystem.IsLinux() && IsZombie(predecessor.Id);

    /// <summary>
    /// Whether a Linux process has exited and waits only for its parent to reap it: <c>kill(pid, 0)</c>, which is what
    /// <see cref="Process.HasExited"/> asks for a process this one did not start, still finds a zombie, so without this a
    /// parent slow to reap would hold the successor for its whole budget.
    /// </summary>
    private static bool IsZombie(int processId)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{processId}/stat");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            // Reaped since: gone.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // "pid (comm) state ...": the name may hold spaces and parentheses, the state follows the LAST ')'.
        var nameEnd = stat.LastIndexOf(')');
        return nameEnd >= 0 && nameEnd + 2 < stat.Length && stat[nameEnd + 2] is 'Z' or 'X';
    }

    /// <summary>
    /// <see cref="WaitForPredecessorAsync"/>'s loop: looks at <paramref name="hasExited"/>, and sleeps
    /// <see cref="PredecessorPollInterval"/> at most between looks, until it answers true or a look taken once
    /// <paramref name="budget"/> has passed still answers false. A sleep that overruns (a starved box) delays the answer,
    /// never changes it.
    /// </summary>
    internal static async Task<bool> WaitForExitAsync(Func<bool> hasExited, TimeSpan budget, TimeProvider time,
        Func<TimeSpan, CancellationToken, Task> sleep, CancellationToken cancellationToken)
    {
        var start = time.GetTimestamp();
        while (!hasExited())
        {
            var remaining = budget - time.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await sleep(remaining < PredecessorPollInterval ? remaining : PredecessorPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

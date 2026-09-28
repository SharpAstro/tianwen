using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.IO;

/// <summary>
/// An external tool never outlives the call that started it: ONE rule for every tool TianWen runs (the plate solvers'
/// <c>astap_cli</c> and <c>solve-field</c>, 7-Zip for the planetary corpus). A wait ends on the process's exit, a timeout,
/// or the caller's cancellation, and the last two kill the whole process tree.
/// </summary>
internal static class BoundedProcess
{
    /// <summary>
    /// Waits for <paramref name="proc"/> to exit, and never lets it outlive the wait: on the timeout or on
    /// cancellation the whole process TREE is killed. <c>wsl.exe</c> and <c>bash -l -c</c> each put the tool
    /// one process further down, so killing only the direct child leaves the tool running.
    /// </summary>
    /// <returns><see langword="true"/> when the process exited on its own; <see langword="false"/> when it
    /// ran past <paramref name="timeout"/> and was killed.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled; the tree is killed first.</exception>
    public static async Task<bool> WaitForExitOrKillAsync(Process proc, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t, TimeProvider.System) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>Kills <paramref name="proc"/> and every process under it; a process already gone is nothing to stop.</summary>
    public static void KillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited, or not ours to kill: either way there is nothing left to stop.
        }
    }
}

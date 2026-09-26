using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Lib;

namespace TianWen.Hosting;

/// <summary>
/// Keeps the machine's node running (docs/plans/hardware-in-the-server.md, "Spawn and lifetime", decision 10): a
/// client starts <c>tianwen-server --keeper</c>, which starts the node, waits on it, and starts it again after it
/// crashes. Without one, a node that crashed while no window was open left the mount unguarded until a client next
/// started.
/// </summary>
/// <remarks>
/// <para>
/// The keeper holds no hardware and does nothing but wait, which is why it outlives what it guards. It is the process
/// a client's spawn breaks away (or, on Unix, the one that leaves the client's session), so the node it starts
/// inherits that and needs nothing of its own.
/// </para>
/// <para>
/// <b>What ends it:</b> the node stopping cleanly (asked to, over its socket; one that stops to restart, to apply the
/// share setting, is started again at once), a node that never started because its
/// command line was wrong or another node holds the socket, and a crash LOOP: two crashes within
/// <see cref="CrashLoopWindow"/>. A node that crashes because a driver crashes it would crash again on the same
/// device, so a second crash leaves the node down, for the next client to report, rather than restarted forever.
/// </para>
/// </remarks>
public sealed class NodeKeeper(Func<CancellationToken, Task<int>> runNode, TimeProvider timeProvider, ILogger logger)
{
    /// <summary>Two crashes this close together are a loop, not bad luck.</summary>
    public static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(5);

    /// <summary>A keeper of the node at <paramref name="nodePath"/>, started with <paramref name="nodeArguments"/>.</summary>
    public static NodeKeeper ForProcess(string nodePath, IReadOnlyList<string> nodeArguments, TimeProvider timeProvider, ILogger logger) =>
        new NodeKeeper(cancellationToken => RunNodeProcessAsync(nodePath, nodeArguments, logger, cancellationToken), timeProvider, logger);

    /// <returns>How the keeper ended, as a <see cref="NodeExitCodes"/> value.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? lastCrash = null;
        while (true)
        {
            var exit = await runNode(cancellationToken).ConfigureAwait(false);
            if (exit is NodeExitCodes.Restart)
            {
                // The node stopped to apply a setting it reads only at start: not a crash, and started again at once.
                logger.LogInformation("The node restarted itself to apply a setting; starting it again");
                continue;
            }
            if (exit is NodeExitCodes.Stopped or NodeExitCodes.InvalidArguments or NodeExitCodes.AlreadyRunning or NodeExitCodes.CouldNotStart)
            {
                logger.LogInformation("The node ended with {ExitCode}, which is not a crash; the keeper ends with it", exit);
                return exit;
            }

            var now = timeProvider.GetUtcNow();
            if (lastCrash is { } previous && now - previous < CrashLoopWindow)
            {
                logger.LogError("The node crashed again (exit {ExitCode}) within {Window} of the last crash; leaving it down", exit, CrashLoopWindow);
                return NodeExitCodes.CrashLoop;
            }

            lastCrash = now;
            logger.LogWarning("The node crashed (exit {ExitCode}); starting it again", exit);
        }
    }

    private static async Task<int> RunNodeProcessAsync(string nodePath, IReadOnlyList<string> nodeArguments, ILogger logger, CancellationToken cancellationToken)
    {
        // No window, the keeper's own (null) standard streams, and the data root as its working directory, never the
        // directory a client happened to be started from.
        var start = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = TianWenDataRoot.Directory.FullName,
        };
        foreach (var argument in nodeArguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? node;
        try
        {
            node = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "Could not start the node {Path}", nodePath);
            return NodeExitCodes.CouldNotStart;
        }

        if (node is null)
        {
            logger.LogError("Could not start the node {Path}", nodePath);
            return NodeExitCodes.CouldNotStart;
        }

        using (node)
        {
            logger.LogInformation("Started the node {Path}, pid {Pid}", nodePath, node.Id);
            await node.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return node.ExitCode;
        }
    }
}

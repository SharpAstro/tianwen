using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.IO;

/// <summary>
/// <see cref="IPowerRequest"/> on macOS (<c>caffeinate -i -w &lt;pid&gt;</c>) and Linux (<c>systemd-inhibit</c> around a
/// <c>tail --pid</c>): the request is a child that holds it.
/// </summary>
/// <remarks>
/// <para><b>This long-lived child is the stated exception to <see cref="BoundedProcess"/>'s rule</b> that a tool never outlives
/// the call that started it: its life IS the request's, so it runs until the handle is disposed, and it ends with the node by
/// itself, since each form is told the node's pid (<c>caffeinate -w</c>, <c>tail --pid</c>) and exits when that process does.
/// Releasing kills the whole tree (<see cref="BoundedProcess.KillTree"/>).</para>
/// <para>A tool that is absent is logged ONCE and the node goes on without the request, never failing a connect for it.</para>
/// </remarks>
internal sealed class ProcessPowerRequest(string tool, Func<string, int, string[]> arguments, ILogger logger) : IPowerRequest
{
    private int _missingLogged;

    public static ProcessPowerRequest ForMacOS(ILogger logger)
        => new ProcessPowerRequest("caffeinate", static (_, pid) => ["-i", "-w", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)], logger);

    public static ProcessPowerRequest ForLinux(ILogger logger)
        => new ProcessPowerRequest("systemd-inhibit", static (reason, pid) =>
        [
            "--what=sleep:idle", "--mode=block", "--who=TianWen", $"--why={reason}",
            "tail", $"--pid={pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", "/dev/null",
        ], logger);

    public IDisposable Acquire(string reason)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments(reason, Environment.ProcessId))
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            if (Process.Start(info) is { } process)
            {
                return new Held(process);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            if (Interlocked.Exchange(ref _missingLogged, 1) == 0)
            {
                logger.LogWarning("{Tool} is not available ({Message}); the computer may sleep while the node holds hardware", tool, ex.Message);
            }
        }
        return NoPowerRequest.Handle;
    }

    private sealed class Held(Process process) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }
            BoundedProcess.KillTree(process);
            process.Dispose();
        }
    }
}

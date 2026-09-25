using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TianWen.Hosting;

/// <summary>
/// How a keeper leaves the client that started it, on Unix (docs/plans/hardware-in-the-server.md, "Spawn and
/// lifetime"). Windows does this at the spawn instead, where the client breaks the keeper away from its job and gives
/// it no console.
/// </summary>
[UnsupportedOSPlatform("windows")]
public static partial class NodeDetachment
{
    /// <summary>
    /// Starts a session of its own, so a closed terminal or an ended desktop session, which signals its whole session,
    /// does not take the keeper and its node with the client; and puts standard input, output and error on the null
    /// device, so a node started from the TUI can never write over the TUI's screen and nothing waits on a pipe nobody
    /// drains. The child calls <c>setsid()</c> ITSELF: a child of <c>Process.Start</c> is never a process-group leader,
    /// so it succeeds, where <c>posix_spawn</c>'s attribute struct is opaque on macOS and would have to be guessed at.
    /// </summary>
    public static void FromTheClient(ILogger logger)
    {
        if (setsid() < 0)
        {
            logger.LogWarning("setsid failed (errno {Errno}); the keeper stays in the session that started it", Marshal.GetLastPInvokeError());
        }

        using var nullDevice = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var fd = (int)nullDevice.DangerousGetHandle();
        for (var standardStream = 0; standardStream <= 2; standardStream++)
        {
            if (dup2(fd, standardStream) < 0)
            {
                logger.LogWarning("Could not put standard stream {Stream} on the null device (errno {Errno})", standardStream, Marshal.GetLastPInvokeError());
            }
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldFd, int newFd);
}

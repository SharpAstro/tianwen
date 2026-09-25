using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TianWen.Hosting.Extensions;

/// <summary>
/// The node on its socket (docs/plans/hardware-in-the-server.md, "Transport"): bound only by the lock's holder, and
/// owner-only on Unix.
/// </summary>
public static class NodeSocketHosting
{
    /// <summary>
    /// Listens on the socket <paramref name="held"/> admits this node to, after clearing a socket file a dead node
    /// left there, which a bind would fail on. Taking the lock as the argument is the point: nothing that does not
    /// hold it can clear or bind the socket.
    /// </summary>
    public static void ListenOnNodeSocket(this KestrelServerOptions options, NodeLock held)
    {
        held.ClearStaleSocket();
        options.ListenUnixSocket(held.SocketPath);
    }

    /// <summary>
    /// Once the node listens, makes its socket owner-only on Unix, where connecting takes write permission on the
    /// file and the umask alone would decide it. Windows keeps the AppData folder's ACL, which the file inherits.
    /// </summary>
    /// <remarks>
    /// Nothing removes the socket when the node stops: the runtime deletes a socket file when the socket that bound
    /// it is disposed, so only a node that died leaves one, which the next lock holder clears
    /// (<see cref="NodeLock.ClearStaleSocket"/>).
    /// </remarks>
    public static void RestrictNodeSocketToItsOwner(this WebApplication app, NodeLock held)
    {
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(held.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });
    }
}

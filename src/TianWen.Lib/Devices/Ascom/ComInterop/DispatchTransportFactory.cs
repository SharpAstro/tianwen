using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Builds the right <see cref="IDispatchTransport"/> for an ASCOM ProgID: an out-of-process
/// <see cref="RemoteDispatchTransport"/> for CET-incompatible in-proc .NET Framework drivers
/// (<see cref="AscomComServerClassifier"/>), or an in-proc <see cref="DispatchObject"/> for everything
/// else. If a driver needs the helper but it can't be found/started, falls back to in-proc with a warning
/// (the pre-Phase-4 behaviour, which may fastfail on connect, but is no worse than before).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DispatchTransportFactory
{
    private const string HelperExeName = "tianwen-ascomhost.exe";

    // The connect-path DoEvents busy-spin runs on the helper; give the handshake generous headroom.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public static IDispatchTransport Create(string progId, IServiceProvider? serviceProvider)
    {
        var logger = (serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger("TianWen.Lib.Devices.Ascom.ComInterop.DispatchTransportFactory");

        if (AscomComServerClassifier.RequiresOutOfProcessHost(progId, out var reason))
        {
            if (TryLocateHelper(out var exePath))
            {
                try
                {
                    var transport = RemoteDispatchTransport.Create(exePath, progId, HandshakeTimeout);
                    logger?.LogInformation(
                        "ASCOM {ProgId} hosted out-of-process ({Reason}) via {Exe}.", progId, reason, exePath);
                    return transport;
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Failed to start the out-of-process ASCOM host for {ProgId} ({Reason}); falling back to in-proc (may fastfail on connect).",
                        progId, reason);
                }
            }
            else
            {
                logger?.LogWarning(
                    "ASCOM {ProgId} needs the out-of-process host ({Reason}) but {Exe} was not found (set TIANWEN_ASCOMHOST or ship it beside the app); falling back to in-proc (may fastfail on connect).",
                    progId, reason, HelperExeName);
            }
        }
        else
        {
            logger?.LogDebug("ASCOM {ProgId} runs in-proc ({Reason}).", progId, reason);
        }

        return new DispatchObject(progId);
    }

    /// <summary>
    /// Resolves the helper exe: the explicit <c>TIANWEN_ASCOMHOST</c> override, then beside the running program. A
    /// checkout's build puts it there as a release does (<c>src/ExeBeside.targets</c>: the node carries it, and every
    /// client and test that runs ASCOM carries the node or the helper), so there is one place to look.
    /// </summary>
    internal static bool TryLocateHelper(out string exePath)
    {
        var overridePath = Environment.GetEnvironmentVariable("TIANWEN_ASCOMHOST");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            exePath = overridePath;
            return true;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, HelperExeName);
        if (File.Exists(beside))
        {
            exePath = beside;
            return true;
        }

        exePath = string.Empty;
        return false;
    }
}

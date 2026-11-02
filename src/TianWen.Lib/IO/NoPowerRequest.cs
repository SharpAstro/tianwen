using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TianWen.Lib.IO;

/// <summary>An <see cref="IPowerRequest"/> that asks nothing: a platform with no way to ask, or one that could not.</summary>
internal sealed class NoPowerRequest : IPowerRequest
{
    public static readonly NoPowerRequest Instance = new NoPowerRequest();

    /// <summary>The handle of a request that was not made; releasing it is nothing.</summary>
    public static readonly IDisposable Handle = new Inert();

    public IDisposable Acquire(string reason) => Handle;

    private sealed class Inert : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Chooses the <see cref="IPowerRequest"/> for the OS this process runs on, as the credential store is chosen.</summary>
public static class PowerRequests
{
    /// <summary>Windows: a power request object; macOS and Linux: a child holding it; anything else: none.</summary>
    public static IPowerRequest ForThisOs(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("TianWen.Lib.IO.PowerRequest") ?? NullLogger.Instance;
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPowerRequest(logger);
        }
        if (OperatingSystem.IsMacOS())
        {
            return ProcessPowerRequest.ForMacOS(logger);
        }
        if (OperatingSystem.IsLinux())
        {
            return ProcessPowerRequest.ForLinux(logger);
        }
        return NoPowerRequest.Instance;
    }
}

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TianWen.Lib.IO;

/// <summary>An <see cref="ISuspendNotice"/> that never raises: a platform the OS gives no notice on.</summary>
internal sealed class NoSuspendNotice : ISuspendNotice
{
    public static readonly NoSuspendNotice Instance = new NoSuspendNotice();

    public event Func<Task>? Suspending
    {
        add { }
        remove { }
    }

    public event Action? Resumed
    {
        add { }
        remove { }
    }
}

/// <summary>Chooses the <see cref="ISuspendNotice"/> for the OS this process runs on.</summary>
public static class SuspendNotices
{
    /// <summary>Windows: the power callback; anywhere else none, said once in the log.</summary>
    public static ISuspendNotice ForThisOs(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("TianWen.Lib.IO.SuspendNotice") ?? NullLogger.Instance;
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSuspendNotice(logger);
        }
        logger.LogInformation("This OS gives the node no notice before a suspend, so the mounts are not stopped first; "
            + "the sleep inhibitor held while hardware is in use is the protection here");
        return NoSuspendNotice.Instance;
    }
}

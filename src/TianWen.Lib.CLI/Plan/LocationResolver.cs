using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Plan;

/// <summary>
/// CLI wrapper around <see cref="TransformFactory"/> that reports errors via <see cref="IConsoleHost"/>.
/// </summary>
internal static class LocationResolver
{
    public static Transform? ResolveFromProfile(
        IConsoleHost consoleHost,
        Profile profile,
        ITimeProvider timeProvider)
    {
        var transform = TransformFactory.FromProfile(profile, timeProvider, out var error);
        if (error is not null)
        {
            consoleHost.WriteError(error);
        }
        return transform;
    }
}

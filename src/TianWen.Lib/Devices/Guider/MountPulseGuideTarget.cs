using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Adapts an <see cref="IMountDriver"/> to the <see cref="IPulseGuideTarget"/> interface.
/// Used when pulse guide corrections should go directly to the mount.
/// </summary>
internal sealed class MountPulseGuideTarget(IMountDriver mount) : IPulseGuideTarget
{
    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
        => mount.PulseGuideAsync(direction, duration, cancellationToken);

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
        => mount.IsPulseGuidingAsync(cancellationToken);
}

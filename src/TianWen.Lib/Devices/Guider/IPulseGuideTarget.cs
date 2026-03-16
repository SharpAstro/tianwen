using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Abstracts the destination for pulse guide corrections.
/// Implementations may route corrections to a camera's ST-4 port, a mount, or both with fallback.
/// </summary>
internal interface IPulseGuideTarget
{
    ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken);

    ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken);
}

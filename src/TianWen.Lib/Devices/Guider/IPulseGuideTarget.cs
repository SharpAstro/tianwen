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
    /// <summary>
    /// STARTS a guide pulse and returns once the target has been commanded, NOT once the pulse has
    /// finished. See
    /// <see cref="IMountDriver.StartPulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/>
    /// for the contract in full.
    /// </summary>
    /// <remarks>
    /// <b>Awaiting this is not waiting for the pulse</b>, which is exactly why it is not the method
    /// most callers should reach for. Use
    /// <see cref="PulseGuideTargetExtensions.PulseGuideAsync"/> -- it starts a pulse AND waits --
    /// and keep this primitive for the callers that genuinely want to do something else meanwhile,
    /// which today means driving the other axis.
    /// </remarks>
    ValueTask StartPulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a pulse started by
    /// <see cref="StartPulseGuideAsync(GuideDirection, TimeSpan, CancellationToken)"/> is still
    /// running. One flag for BOTH axes, following ASCOM: it answers "is the mount still moving
    /// under a guide correction", which is the only question a caller actually asks.
    /// </summary>
    ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken);
}

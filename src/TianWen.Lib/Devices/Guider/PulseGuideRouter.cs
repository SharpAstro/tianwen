using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Routes pulse guide corrections based on <see cref="PulseGuideSource"/> configuration.
/// </summary>
internal sealed class PulseGuideRouter : IPulseGuideTarget
{
    private readonly ICameraDriver? _camera;
    private readonly IMountDriver? _mount;

    // Resolved once in constructor to avoid repeated fallback logic
    private readonly bool _useCamera;

    public PulseGuideRouter(PulseGuideSource source, ICameraDriver? camera, IMountDriver? mount)
    {
        _camera = camera;
        _mount = mount;

        _useCamera = source switch
        {
            PulseGuideSource.Camera => camera is { CanPulseGuide: true }
                ? true
                : throw new InvalidOperationException("PulseGuideSource.Camera requires a camera that supports pulse guiding (ST-4)."),

            PulseGuideSource.Mount => mount is { CanPulseGuide: true }
                ? false
                : throw new InvalidOperationException("PulseGuideSource.Mount requires a mount that supports pulse guiding."),

            // Auto prefers the mount: mount CanPulseGuide means the mount itself moves —
            // verifiable. Camera CanPulseGuide only proves an ST-4 *socket* exists (SDKs
            // report HasST4Port); it cannot know whether a guide cable is plugged in, and
            // pulses into an unconnected socket are silent no-ops that wedge guiding.
            PulseGuideSource.Auto => mount is { CanPulseGuide: true }
                ? false
                : camera is { CanPulseGuide: true }
                    ? true
                    : throw new InvalidOperationException("Neither mount nor camera (ST-4) supports pulse guiding."),

            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };
    }

    public ValueTask PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (_useCamera)
        {
            return _camera!.PulseGuideAsync(direction, duration, cancellationToken);
        }

        return _mount!.PulseGuideAsync(direction, duration, cancellationToken);
    }

    public ValueTask<bool> IsPulseGuidingAsync(CancellationToken cancellationToken)
    {
        if (_useCamera)
        {
            return _camera!.GetIsPulseGuidingAsync(cancellationToken);
        }

        return _mount!.IsPulseGuidingAsync(cancellationToken);
    }
}

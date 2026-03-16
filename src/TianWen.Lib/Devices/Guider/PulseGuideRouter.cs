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

            PulseGuideSource.Auto => camera is { CanPulseGuide: true }
                ? true
                : mount is { CanPulseGuide: true }
                    ? false
                    : throw new InvalidOperationException("Neither camera (ST-4) nor mount supports pulse guiding."),

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

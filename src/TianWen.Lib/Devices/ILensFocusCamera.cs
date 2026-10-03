using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// A step of a camera's own lens drive: which way the focus moves (Near, toward close focus; Far, toward infinity) and how
/// far, in the body's own sizes (a Canon EOS offers three each way). Relative only: no position stands behind a step.
/// Numeric on the wire, so the sign is the direction and the magnitude the size.
/// </summary>
public enum LensFocusStep : sbyte
{
    NearLarge = -3,
    NearMedium = -2,
    NearSmall = -1,
    FarSmall = 1,
    FarMedium = 2,
    FarLarge = 3,
}

/// <summary>
/// A camera that drives its own lens's focus (#681, P12 of docs/plans/live-session-preview.md): a Canon EOS body moves an
/// AF lens by steps, and only while its Live View runs, which is why a live view is what offers it. Relative steps, no
/// position: a focuser for autofocus is a further step (a made-up position from a nominal start, moved only in the smallest
/// step).
/// </summary>
public interface ILensFocusCamera
{
    /// <summary>Whether a step can be asked for now: the body streams its live view, the only time it drives its lens.</summary>
    bool CanDriveLens { get; }

    /// <summary>
    /// Moves the lens's focus by <paramref name="step"/>. False when the body refused it (no lens, a lens switched to manual
    /// focus, or no live view running), which it says in its log.
    /// </summary>
    ValueTask<bool> DriveLensAsync(LensFocusStep step, CancellationToken cancellationToken = default);
}

using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// App-specific <see cref="HitResult"/> variant: the user pressed on a manual white-balance slider track
/// in the info panel. Like the SER transport scrub (and unlike a self-contained button), a WB slider needs
/// press + drag + release, so the mouse-down handlers begin a drag for <see cref="Channel"/> and the
/// move/up handlers track it. <see cref="Channel"/> is 0 = R, 1 = G, 2 = B.
/// </summary>
public sealed record WhiteBalanceSliderHit(int Channel) : HitResult;

using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// App-specific <see cref="HitResult"/> variant: the user pressed on the SER transport scrub track.
/// Unlike a plain button (which is self-contained via <c>OnClick</c>), scrubbing needs press + drag +
/// release, so the mouse-down handlers begin a scrub and the move/up handlers track it. The frame the
/// press maps to is derived from the press X against the scrub track rect by the renderer.
/// </summary>
public sealed record TransportScrubHit : HitResult;

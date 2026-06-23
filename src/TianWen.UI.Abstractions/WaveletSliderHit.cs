using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// App-specific <see cref="HitResult"/> variant: the user pressed on a wavelet-sharpen layer slider in the
/// info panel (shown for the live stacked view). Like the white-balance sliders it is a press + drag +
/// release control, so the mouse-down handlers begin a drag for <see cref="Band"/> and the move/up handlers
/// track it. <see cref="Band"/> is 0 = finest a-trous scale .. 5 = coarsest (the Registax layer convention).
/// </summary>
public sealed record WaveletSliderHit(int Band) : HitResult;

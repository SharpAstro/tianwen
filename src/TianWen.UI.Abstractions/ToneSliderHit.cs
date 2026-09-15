using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>Which dial in the tone popover a press landed on.</summary>
public enum ToneSlider
{
    /// <summary>The curves boost, <see cref="ViewerState.CurvesBoost"/>.</summary>
    Boost,

    /// <summary>How hard the soft clip bends, <see cref="ViewerState.HdrAmount"/>.</summary>
    SoftClipAmount,

    /// <summary>Where the soft clip starts, <see cref="ViewerState.HdrKnee"/>.</summary>
    SoftClipKnee,
}

/// <summary>
/// App-specific <see cref="HitResult"/> variant: the user pressed on a tone-popover slider track.
/// Like the white-balance sliders it needs press + drag + release, so the mouse-down handlers begin a
/// drag for <see cref="Slider"/> and the move / up handlers track it.
/// </summary>
public sealed record ToneSliderHit(ToneSlider Slider) : HitResult;

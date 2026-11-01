namespace TianWen.UI.Abstractions;

/// <summary>
/// A preview source that can also show its master BEFORE the wavelet sharpening, so the viewer's A | B can compare the two
/// (<see cref="SplitCompare.Unsharpened"/>, #1883). Implemented by <see cref="LiveStackPreviewSource"/>: the Live and the
/// Full view of a planetary stack alike.
/// </summary>
/// <remarks>
/// Render thread only, like the rest of the source. The unsharpened master is built only while
/// <see cref="WantUnsharpened"/> says it is wanted, so a view nobody compares pays nothing for it.
/// </remarks>
public interface IUnsharpenedPreview
{
    /// <summary>True when a sharpening is asked for: with none, the master on show IS the unsharpened one and there is nothing to compare.</summary>
    bool SharpeningOn { get; }

    /// <summary>
    /// The master as stacked, on the scale the sharpened one is shown on, or null until it is built (or while it is not wanted).
    /// </summary>
    IPreviewSource? Unsharpened { get; }

    /// <summary>
    /// Identity of <see cref="Unsharpened"/>: moves only when that master is rebuilt (a new stack), never when only the
    /// sharpening changes, so a host uploads it once per master and a dial move re-sharpens without touching it.
    /// </summary>
    int UnsharpenedGeneration { get; }

    /// <summary>Says whether the unsharpened master is wanted. Idempotent; call every frame it is compared, and with false when it is not.</summary>
    void WantUnsharpened(bool wanted);
}

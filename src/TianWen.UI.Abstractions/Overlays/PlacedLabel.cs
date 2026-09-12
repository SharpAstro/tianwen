namespace TianWen.UI.Abstractions.Overlays;

/// <summary>
/// A label the placement pass settled: the item it names and the box it occupies, in screen pixels,
/// with the top-left at (<see cref="X"/>, <see cref="Y"/>).
/// </summary>
/// <remarks>
/// The whole box rather than a corner, because the width and height are what the pass MEASURED to
/// choose the slot, and every consumer that wanted them back (the sky map's label click bridge, the
/// viewer's tap hit-test, a reserved region for a later pass) used to re-measure the lines with a
/// formula that had to match the pass's own. Handing the box out is what makes "where is this label"
/// have one answer.
/// </remarks>
public readonly record struct PlacedLabel(OverlayItem Item, float X, float Y, float Width, float Height)
{
    /// <summary>Whether a screen position lands inside the box.</summary>
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

using System.Collections.Immutable;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The catalogued object the viewer currently has SELECTED: what a click landed on, kept until it
    /// is dismissed.
    /// </summary>
    /// <remarks>
    /// <para><b>A pointing is not a selection, and this is the difference.</b> The overlay draws every
    /// object the field contains and the context menu names the one under a right-click, but neither
    /// leaves anything behind -- so there was nowhere for a left click's answer to go, which is why
    /// the resolver (<c>ImageRendererBase.FindObjectAt</c>) existed for weeks wired to right-click
    /// alone.</para>
    /// <para><b>Resolved ONCE, at the click, and carried whole.</b> The lines, the designation and the
    /// coordinates are all read off the catalogue when the selection is made rather than re-resolved
    /// per frame: the highlight, the info panel and the atlas link then agree by construction, and
    /// none of them touches the catalogue on the render thread. It also means a selection SURVIVES the
    /// catalogue being unavailable afterwards, which a per-frame lookup would not.</para>
    /// <para>The coordinates are the OBJECT's, not the click's -- the ring is drawn where the object
    /// is, not where the pointer was, and the atlas link centres on the same place.</para>
    /// </remarks>
    /// <param name="Name">Best common name, or the canonical designation when the object has none.</param>
    /// <param name="Designation">Canonical catalogue designation, e.g. "NGC 6523".</param>
    /// <param name="RaHours">The object's right ascension in HOURS, the convention everywhere but a link.</param>
    /// <param name="Dec">The object's declination in degrees.</param>
    /// <param name="Lines">
    /// The identification stack as the overlay label would write it at full zoom, so the panel and a
    /// marker's own label cannot disagree about what the object is called.
    /// </param>
    public readonly record struct ViewerObjectSelection(
        string Name,
        string Designation,
        double RaHours,
        double Dec,
        ImmutableArray<string> Lines)
    {
        /// <summary>
        /// The token a share link carries so the atlas SELECTS the object rather than merely pointing
        /// at where it is: the designation, falling back to the name.
        /// </summary>
        /// <remarks>
        /// Same rule as the context menu's, and for the same reason -- a catalogue number is what a
        /// search resolves unambiguously, while a common name can be shared or absent. They are equal
        /// for an object whose designation IS its name, which is the common case.
        /// </remarks>
        public string Token => Designation is { Length: > 0 } ? Designation : Name;
    }
}

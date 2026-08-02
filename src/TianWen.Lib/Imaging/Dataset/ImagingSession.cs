using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// One imaging session for dataset purposes: the raw lights of one camera imaging one
/// target through one filter under one session directory. Identity (<see cref="Id"/>) is
/// machine-portable, derived from the archive-root-RELATIVE directory plus the camera name (and the
/// target and filter when present) rather than from absolute paths, because it keys the pinned
/// train/test split (<c>test-sessions.txt</c>) that must stay meaningful across machines and
/// archive relocations.
///
/// <para>Grouping is <b>per target</b>, not just per directory: a single dated N.I.N.A. LIGHT
/// folder routinely holds two-to-four distinct pointings (e.g. HD 71272 + RCW 27 + Vela SNR on
/// one night), distinguished only by the FITS <c>OBJECT</c> header. Those cannot register to a
/// common reference, and worse, the session-relative, star-count-led quality gate would treat
/// a sparse nebula field as an outlier against a rich star field. Splitting by
/// <see cref="Target"/> keeps every session a single registerable pointing so both the gate and
/// the registration are correct.</para>
///
/// <para>Grouping is <b>per filter</b> for the same two reasons, which bite harder: on a mono
/// narrowband night both lines of one pointing land in one folder under one OBJECT, so nothing but
/// <c>FILTER</c> separates them. OIII detects far fewer stars than Ha through equivalent filters,
/// which makes the MAD gate see a bimodal population and reject the OIII half as a left tail; and
/// whatever survives integrates to a master that is neither line. Splitting by
/// <see cref="FilterName"/> also disambiguates flat matching, which
/// <see cref="CalibrationResolver"/> resolves from <c>Lights[0]</c> and so would otherwise settle
/// on whichever filter happened to sort first.</para>
/// </summary>
/// <param name="SessionDir">Absolute session directory on this machine.</param>
/// <param name="RelativeDir">Session directory relative to its archive root, with
/// forward-slash separators (portable).</param>
/// <param name="Camera">INSTRUME of the session's lights.</param>
/// <param name="Target">OBJECT of the session's lights (the target/pointing name), trimmed.
/// Empty when the frames carry no OBJECT header, in which case grouping degenerates to the
/// legacy per-directory-per-camera behaviour.</param>
/// <param name="FilterName">The filter the session's lights were shot through: the canonical
/// <see cref="Filter.Name"/> when the header text parsed to a known filter, the trimmed raw header
/// text when it did not, and empty when there is no filter at all. Empty keeps grouping and
/// <see cref="Id"/> at the pre-filter behaviour.</param>
/// <param name="Lights">Gated, deduplicated raw light frames (header-only handles).</param>
public sealed record ImagingSession(
    string SessionDir,
    string RelativeDir,
    string Camera,
    string Target,
    string FilterName,
    ImmutableArray<FrameInfo> Lights)
{
    /// <summary>Portable, stable session identity: <c>relative/dir|CAMERA</c>, plus
    /// <c>|OBJECT</c> when a target is present and <c>|FILTER</c> when a filter is. Frames with
    /// neither keep the legacy two-part id and a filterless target keeps the legacy three-part one,
    /// so every train/test assignment made before filters entered the key survives untouched: the
    /// split is a stable per-id hash (<see cref="DatasetSplitWriter"/>), so an id that does not move
    /// cannot change sets.</summary>
    public string Id => (Target.Length > 0, FilterName.Length > 0) switch
    {
        (true, false) => FormattableString.Invariant($"{RelativeDir}|{Camera}|{Target}"),
        (false, false) => FormattableString.Invariant($"{RelativeDir}|{Camera}"),
        // Both slots are emitted whenever a filter is present, so a session with no OBJECT cannot
        // produce "dir|CAM|Ha" and collide with one whose OBJECT happens to be named "Ha".
        _ => FormattableString.Invariant($"{RelativeDir}|{Camera}|{Target}|{FilterName}"),
    };
}

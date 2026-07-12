using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// One imaging session for dataset purposes: the raw lights of one camera imaging one
/// target under one session directory. Identity (<see cref="Id"/>) is machine-portable — it is
/// derived from the archive-root-RELATIVE directory plus the camera name (and the target when
/// present), never from absolute paths — because it keys the pinned train/test split
/// (<c>test-sessions.txt</c>) that must stay meaningful across machines and archive relocations.
///
/// <para>Grouping is <b>per target</b>, not just per directory: a single dated N.I.N.A. LIGHT
/// folder routinely holds two-to-four distinct pointings (e.g. HD 71272 + RCW 27 + Vela SNR on
/// one night), distinguished only by the FITS <c>OBJECT</c> header. Those cannot register to a
/// common reference and — worse — the session-relative, star-count-led quality gate would treat
/// a sparse nebula field as an outlier against a rich star field. Splitting by
/// <see cref="Target"/> keeps every session a single registerable pointing so both the gate and
/// the registration are correct.</para>
/// </summary>
/// <param name="SessionDir">Absolute session directory on this machine.</param>
/// <param name="RelativeDir">Session directory relative to its archive root, with
/// forward-slash separators (portable).</param>
/// <param name="Camera">INSTRUME of the session's lights.</param>
/// <param name="Target">OBJECT of the session's lights (the target/pointing name), trimmed.
/// Empty when the frames carry no OBJECT header, in which case grouping degenerates to the
/// legacy per-directory-per-camera behaviour.</param>
/// <param name="Lights">Gated, deduplicated raw light frames (header-only handles).</param>
public sealed record ImagingSession(
    string SessionDir,
    string RelativeDir,
    string Camera,
    string Target,
    ImmutableArray<FrameInfo> Lights)
{
    /// <summary>Portable, stable session identity: <c>relative/dir|CAMERA</c>, plus
    /// <c>|OBJECT</c> when a target is present. Frames with no OBJECT keep the legacy
    /// two-part id, so existing single-target splits are byte-identical.</summary>
    public string Id => Target.Length > 0
        ? FormattableString.Invariant($"{RelativeDir}|{Camera}|{Target}")
        : FormattableString.Invariant($"{RelativeDir}|{Camera}");
}

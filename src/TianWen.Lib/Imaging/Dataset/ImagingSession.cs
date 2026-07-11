using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// One imaging session for dataset purposes: the raw lights of one camera under one
/// session directory. Identity (<see cref="Id"/>) is machine-portable — it is derived
/// from the archive-root-RELATIVE directory plus the camera name, never from absolute
/// paths — because it keys the pinned train/test split (<c>test-sessions.txt</c>) that
/// must stay meaningful across machines and archive relocations.
/// </summary>
/// <param name="SessionDir">Absolute session directory on this machine.</param>
/// <param name="RelativeDir">Session directory relative to its archive root, with
/// forward-slash separators (portable).</param>
/// <param name="Camera">INSTRUME of the session's lights.</param>
/// <param name="Lights">Gated, deduplicated raw light frames (header-only handles).</param>
public sealed record ImagingSession(
    string SessionDir,
    string RelativeDir,
    string Camera,
    ImmutableArray<FrameInfo> Lights)
{
    /// <summary>Portable, stable session identity: <c>relative/dir|CAMERA</c>.</summary>
    public string Id => FormattableString.Invariant($"{RelativeDir}|{Camera}");
}

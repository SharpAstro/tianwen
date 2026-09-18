using System;
using System.IO;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Frames the capture software, or the observer at the telescope, already marked as rejected.
///
/// <para><b>N.I.N.A. renames a frame it grades out with a <c>BAD_</c> prefix</b>, and the same
/// convention is used by hand for a run someone watched go wrong. It is a judgement made with the
/// sky in view and the mount in front of you, which is more than any later pass can reconstruct, so
/// a scan that ignores it is throwing away the best evidence it will ever get about those frames.</para>
///
/// <para>This archive carries two kinds. The 33 frames of <c>2026-02-20 BAD LIGHT EXAMPLES</c> are
/// kept deliberately as a negative set, and a FOLDER exclusion already covers them
/// (<c>--exclude-path '*BAD LIGHT*'</c>). The other kind hides in an ordinary session: every one of
/// the 21 lights of <c>Helix-Nebula/2026-08-01</c> is named <c>BAD_...</c>, the roof cut into the
/// aperture for the whole run, and the session baked anyway, because nothing in the scan read a file
/// NAME. Its master carries the roof's shadow, about 6 percent down the bottom of the frame in every
/// sub, which no rejector can remove: rejection drops outliers, and a defect present in every frame
/// is the consensus.</para>
///
/// <para>Matched on the name alone and nothing else: the prefix is what the tool writes, and reading
/// it costs no I/O at all, which is what lets the check sit in front of a header read.</para>
/// </summary>
public static class CaptureRejection
{
    /// <summary>The prefix N.I.N.A. gives a frame it graded out.</summary>
    public const string RejectedPrefix = "BAD_";

    /// <summary>
    /// Whether <paramref name="path"/> names a frame that was marked rejected at capture.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on the FILE NAME, never on the directory: a folder called
    /// <c>BAD LIGHT EXAMPLES</c> is a deliberate collection with its own exclusion, and a path that
    /// happens to contain those letters somewhere above the frame says nothing about the frame.
    /// </remarks>
    public static bool IsRejectedAtCapture(string path)
        => Path.GetFileName(path.AsSpan()).StartsWith(RejectedPrefix, StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>
    /// Why a rejected frame was rejected, as the viewer's Grade section names it (#1951): written into the name after the
    /// prefix (<c>BAD_CLOUD_...</c>), so it travels with the file and Explorer shows it, while every reader of the prefix reads
    /// the frame as rejected exactly as before. <see cref="Unspecified"/> is a bare <c>BAD_</c>, which is what N.I.N.A. writes.
    /// </summary>
    public enum Cause
    {
        Unspecified,
        Cloud,
        Streaky,
        Defocused,
        Obstruction,
    }

    /// <summary>
    /// The cause a rejected frame's name records, or null for a frame not rejected. A name with the prefix and no cause word
    /// after it (N.I.N.A.'s own, or a cause this build does not know) is <see cref="Cause.Unspecified"/>.
    /// </summary>
    public static Cause? CauseOf(string path)
    {
        if (!IsRejectedAtCapture(path))
        {
            return null;
        }
        var rest = Path.GetFileName(path.AsSpan())[RejectedPrefix.Length..];
        foreach (var cause in NamedCauses)
        {
            if (rest.StartsWith(CauseWord(cause), StringComparison.OrdinalIgnoreCase))
            {
                return cause;
            }
        }
        return Cause.Unspecified;
    }

    /// <summary>
    /// The name <paramref name="path"/>'s file would have rejected for <paramref name="rejectAs"/>, or kept when it is null:
    /// the prefix and any cause word taken off first, so a frame moves from one cause to another, or back to its capture name,
    /// in one rename.
    /// </summary>
    public static string MarkedPath(string path, Cause? rejectAs)
    {
        var name = Path.GetFileName(path);
        var bare = name;
        if (CauseOf(path) is { } current)
        {
            bare = name[RejectedPrefix.Length..];
            if (current != Cause.Unspecified)
            {
                bare = bare[CauseWord(current).Length..];
            }
        }
        var marked = rejectAs switch
        {
            null => bare,
            Cause.Unspecified => RejectedPrefix + bare,
            { } cause => RejectedPrefix + CauseWord(cause) + bare,
        };
        return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, marked);
    }

    private static readonly Cause[] NamedCauses = [Cause.Cloud, Cause.Streaky, Cause.Defocused, Cause.Obstruction];

    /// <summary>A cause's word in a name, its separator included: <c>CLOUD_</c>.</summary>
    private static string CauseWord(Cause cause) => cause.ToString().ToUpperInvariant() + "_";

    /// <summary>
    /// Marks the frame at <paramref name="path"/> rejected for <paramref name="rejectAs"/> (the <see cref="RejectedPrefix"/>
    /// and the cause's word added to its name) or kept when it is null (both taken off), the convention N.I.N.A. writes and
    /// every scan here reads, so the viewer's reject is what the stacker leaves out. Renames THIS name only and says how many
    /// other names the file has: in a de-duplicated archive
    /// a frame's other names sit in other trees (an Unsorted link into Astro-Pics), some of which are never written, so
    /// bringing them along is the caller's decision with the count in hand, never a side effect. A name already as asked is
    /// no rename.
    /// </summary>
    /// <returns>The frame's path after the call (the same path when nothing moved), or null with <paramref name="error"/> set
    /// when the rename failed: a name already taken by another file, a file gone, a folder that refuses the write.</returns>
    public static string? TryMark(string path, Cause? rejectAs, out int otherNames, out string? error)
    {
        otherNames = HardLinkProbe.TryGetIdentity(path) is { LinkCount: > 1 } identity ? identity.LinkCount - 1 : 0;
        error = null;
        var target = MarkedPath(path, rejectAs);
        if (string.Equals(target, path, StringComparison.Ordinal))
        {
            return path;
        }
        if (File.Exists(target))
        {
            error = $"{Path.GetFileName(target)} already exists beside it";
            return null;
        }
        try
        {
            File.Move(path, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }
}

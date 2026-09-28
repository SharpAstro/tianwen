using System;
using System.Globalization;
using System.IO;

namespace TianWen.Lib.IO;

/// <summary>
/// How much a verb may write to a drive: it keeps a reserve free, and asks the DRIVE every time rather than carrying a
/// budget, since other programs fill the same disk (docs/plans/planetary-restoration.md, R0: the user keeps 109 GB free
/// on the drive the planetary scratch lives on). A write that would dip into the reserve is refused in words before a
/// byte is written.
/// </summary>
public static class ScratchSpace
{
    /// <summary>The reserve the planetary verbs keep by default: the user's 109 GB on <c>D:</c> (2026-09-28).</summary>
    public const long DefaultKeepFreeBytes = 109L * 1024 * 1024 * 1024;

    /// <summary>
    /// Why writing <paramref name="bytesToWrite"/> under <paramref name="path"/> would leave less than
    /// <paramref name="keepFreeBytes"/> free on its drive, or null when it fits.
    /// </summary>
    /// <param name="freeBytesOf">The free space of the drive holding a path; the real drive's when null (tests pass their own).</param>
    public static string? Refusal(string path, long bytesToWrite, long keepFreeBytes, Func<string, long>? freeBytesOf = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var free = (freeBytesOf ?? FreeBytesOf)(path);
        var left = free - Math.Max(0, bytesToWrite);
        return left >= keepFreeBytes
            ? null
            : $"Writing {Size(bytesToWrite)} under {path} would leave {Size(Math.Max(0, left))} free on its drive, under the {Size(keepFreeBytes)} kept free";
    }

    /// <summary>The free space on the drive holding <paramref name="path"/>, as the drive reports it now.</summary>
    public static long FreeBytesOf(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"{path} is on no drive", nameof(path));
        }
        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>
    /// Reads a size such as <c>109G</c>, <c>500M</c> or <c>2048</c> (bytes), in binary units as Windows shows them.
    /// </summary>
    public static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var trimmed = text.Trim();
        long scale = 1;
        switch (char.ToUpperInvariant(trimmed[^1]))
        {
            case 'K':
                scale = 1024;
                break;
            case 'M':
                scale = 1024 * 1024;
                break;
            case 'G':
                scale = 1024L * 1024 * 1024;
                break;
            case 'T':
                scale = 1024L * 1024 * 1024 * 1024;
                break;
        }
        var number = scale == 1 ? trimmed : trimmed[..^1];
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }
        bytes = (long)(value * scale);
        return true;
    }

    /// <summary>A size as a person reads it, in binary units as Windows shows them.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };
}

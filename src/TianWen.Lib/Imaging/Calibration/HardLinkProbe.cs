using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Reports how many directory entries point at a file's data, so a caller that is about to
/// <b>replace</b> a file can tell whether other paths would silently be left behind.
///
/// <para><b>Why an editor needs this.</b> <see cref="FitsHeaderEditor"/> writes a temp file and
/// swaps it in with <see cref="File.Replace(string, string, string?)"/>, which re-points one
/// directory entry at new data. That is exactly the right thing for a file with a single name, and
/// exactly the wrong thing for a hard-linked one: the other links keep the OLD inode, so the edit
/// silently applies to one path and not its siblings. In a de-duplicated archive that produces two
/// files that used to be the same frame and now disagree in their headers, which for a FILTER card
/// means one copy of a night groups as filtered and the other as unfiltered. The storage that
/// de-duplication saved is also quietly given back.</para>
///
/// <para><b>Windows only, deliberately, and it fails open.</b> The link count comes from
/// <c>GetFileInformationByHandle</c>. There is no BCL API for it and no portable one, and the Unix
/// answer needs a <c>stat</c> whose struct layout varies by platform and libc, which is a poor
/// trade for a guard. Elsewhere this returns <see langword="null"/>, meaning "unknown", and a
/// caller must treat unknown as permission to proceed rather than as a refusal: refusing on every
/// non-Windows file would break the tool on the platforms that never had the problem in front of
/// them. The archive this was written for is NTFS.</para>
/// </summary>
internal static partial class HardLinkProbe
{
    /// <summary>
    /// Number of hard links to <paramref name="path"/>, or <see langword="null"/> when the platform
    /// cannot report it or the file could not be opened. A normal file answers 1; anything above 1
    /// means other paths share this data.
    /// </summary>
    public static int? TryGetLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        return TryGetLinkCountWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static int? TryGetLinkCountWindows(string path)
    {
        // A read-only, fully-shared handle: probing must never be the reason another process fails
        // to open the file, and must never itself be a write.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle.DangerousGetHandle(), out var info)
            ? (int)info.NumberOfLinks
            : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(IntPtr file, out BY_HANDLE_FILE_INFORMATION fileInformation);

    // Blittable by construction (FILETIME is two uints), so it passes through the LibraryImport
    // source generator with no marshalling stub. Layout mirrors the Win32 struct exactly; only
    // NumberOfLinks is read, but the whole shape has to be declared for the call to be correct.
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}

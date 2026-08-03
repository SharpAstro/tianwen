using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Tells a caller that is about to <b>replace</b> a file which physical file it is really touching,
/// how many other names point at it, and what those names are, so the other names can either be
/// left alone or brought along.
///
/// <para><b>Why an editor needs this.</b> <see cref="FitsHeaderEditor"/> writes a temp file and
/// swaps it in with <see cref="File.Replace(string, string, string?)"/>, which re-points one
/// directory entry at new data. That is exactly the right thing for a file with a single name, and
/// exactly the wrong thing for a hard-linked one: the other links keep the OLD file, so the edit
/// silently applies to one path and not its siblings. In a de-duplicated archive that produces two
/// files that used to be the same frame and now disagree in their headers, which for a FILTER card
/// means one copy of a night groups as filtered and the other as unfiltered. The storage that
/// de-duplication saved is also quietly given back.</para>
///
/// <para><b>Identity is the file, not the name.</b> <see cref="FileIdentity"/> is the pair NTFS
/// actually keys on (volume serial plus file index), which is what makes "did this edit produce new
/// data" and "does this other name still hold the original" answerable rather than assumed. Link
/// counts alone cannot distinguish "the sibling still points where it did" from "the sibling was
/// replaced by something else entirely while we worked".</para>
///
/// <para><b>Windows only, deliberately, and every member fails open.</b> The answers come from
/// <c>GetFileInformationByHandle</c> and <c>FindFirstFileNameW</c>. There is no BCL API for either
/// and no portable one, and the Unix equivalent needs a <c>stat</c> whose struct layout varies by
/// platform and libc, which is a poor trade for a guard. Elsewhere these return
/// <see langword="null"/> or empty, meaning "unknown", and a caller must treat unknown as
/// permission to proceed rather than as a refusal: refusing on every non-Windows file would break
/// the tool on the platforms that never had the problem in front of them. The archive this was
/// written for is NTFS.</para>
/// </summary>
internal static partial class HardLinkProbe
{
    private static readonly IntPtr InvalidHandle = new(-1);
    private const int ErrorMoreData = 234;
    private const int ErrorHandleEof = 38;

    /// <summary>
    /// Which physical file a path names, plus how many names point at it. Two paths naming one file
    /// report equal <see cref="VolumeSerial"/> and <see cref="FileIndex"/>; a normal file reports
    /// <see cref="LinkCount"/> 1.
    /// </summary>
    public readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, int LinkCount)
    {
        /// <summary>True when both describe the same physical file, whatever their link counts are.
        /// The link count deliberately does not participate: it changes as names come and go, while
        /// the file being pointed at is the thing worth comparing.</summary>
        public bool IsSameFileAs(FileIdentity other)
            => VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;

        public override string ToString() => $"{VolumeSerial:X8}:{FileIndex:X16}/{LinkCount}";
    }

    /// <summary>
    /// Identity of the file <paramref name="path"/> names, or <see langword="null"/> when the
    /// platform cannot report it or the file could not be opened.
    /// </summary>
    public static FileIdentity? TryGetIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        return TryGetIdentityWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static FileIdentity? TryGetIdentityWindows(string path)
    {
        try
        {
            // A read-only, fully-shared handle: probing must never be the reason another process
            // fails to open the file, and must never itself be a write.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var info))
            {
                return null;
            }
            return new FileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                (int)info.NumberOfLinks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A caller that must know treats null as a hard failure; a caller that is only deciding
            // whether to be careful treats it as "no reason to be".
            return null;
        }
    }

    /// <summary>
    /// Every name that points at the same file as <paramref name="path"/>, as full paths, including
    /// <paramref name="path"/> itself. Empty when the platform cannot answer or the walk failed,
    /// which a caller must read as "unknown", never as "there are none".
    /// </summary>
    public static ImmutableArray<string> EnumerateLinks(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        return EnumerateLinksWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static ImmutableArray<string> EnumerateLinksWindows(string path)
    {
        var full = Path.GetFullPath(path);
        // Each name comes back VOLUME-relative ("\dir\frame.fits"), so it is only usable once the
        // volume root is put back in front. That shape is also the guarantee that every answer is on
        // one volume, which is what makes a later CreateHardLink between any two of them legal.
        if (Path.GetPathRoot(full) is not { Length: > 0 } root)
        {
            return [];
        }
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar);

        var buffer = new char[1024];
        var length = (uint)buffer.Length;
        var find = FindFirstFileName(full, 0, ref length, ref Start(buffer));
        if (find == InvalidHandle)
        {
            if (Marshal.GetLastWin32Error() != ErrorMoreData)
            {
                return [];
            }
            // Both entry points answer ERROR_MORE_DATA with the size they need, so one retry at the
            // demanded size is always enough.
            buffer = new char[length];
            find = FindFirstFileName(full, 0, ref length, ref Start(buffer));
            if (find == InvalidHandle)
            {
                return [];
            }
        }

        try
        {
            var names = ImmutableArray.CreateBuilder<string>();
            while (true)
            {
                names.Add(prefix + NameFrom(buffer));

                length = (uint)buffer.Length;
                if (FindNextFileName(find, ref length, ref Start(buffer)))
                {
                    continue;
                }
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorMoreData)
                {
                    buffer = new char[length];
                    if (FindNextFileName(find, ref length, ref Start(buffer)))
                    {
                        continue;
                    }
                    error = Marshal.GetLastWin32Error();
                }
                // Running out of names is the normal end of the walk; anything else means the answer
                // is incomplete, and half a list of links is worse than none.
                return error == ErrorHandleEof ? names.DrainToImmutable() : [];
            }
        }
        finally
        {
            FindClose(find);
        }
    }

    /// <summary>
    /// Adds <paramref name="newLink"/> as another name for the file <paramref name="existingFile"/>
    /// names. Both must be on the same volume, which every path from
    /// <see cref="EnumerateLinks"/> is by construction.
    /// </summary>
    public static bool TryCreateHardLink(string newLink, string existingFile, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Hard links are only created on Windows.";
            return false;
        }
        if (CreateHardLink(newLink, existingFile, IntPtr.Zero))
        {
            error = "";
            return true;
        }
        error = Marshal.GetPInvokeErrorMessage(Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>The NUL-terminated name in a Win32 output buffer. The length the API reports back is
    /// documented only for the ERROR_MORE_DATA case, so the terminator is the reliable end.</summary>
    private static string NameFrom(char[] buffer)
    {
        var nul = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, nul < 0 ? buffer.Length : nul);
    }

    /// <summary>The buffer's first code unit as the <c>ushort</c> the imports are declared over.
    /// Passing a byref to a P/Invoke pins the array for the duration of the call.</summary>
    private static ref ushort Start(char[] buffer)
        => ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetArrayDataReference(buffer));

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(IntPtr file, out BY_HANDLE_FILE_INFORMATION fileInformation);

    // The link-name buffer is declared as `ushort`, not `char`: `char` marshalling is charset
    // dependent, so the LibraryImport generator refuses it (SYSLIB1051) unless the whole assembly
    // disables runtime marshalling. A UTF-16 code unit is a ushort either way, and `Start` below is
    // the one place the reinterpretation happens.
    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindFirstFileName(string fileName, uint flags, ref uint stringLength, ref ushort linkName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextFileName(IntPtr findStream, ref uint stringLength, ref ushort linkName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(IntPtr findFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    // Blittable by construction (FILETIME is two uints), so it passes through the LibraryImport
    // source generator with no marshalling stub. Layout mirrors the Win32 struct exactly; only
    // three fields are read, but the whole shape has to be declared for the call to be correct.
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

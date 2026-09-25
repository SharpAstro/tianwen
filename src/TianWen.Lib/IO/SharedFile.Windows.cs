using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TianWen.Lib.IO;

public static partial class SharedFile
{
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Windows' replacing rename with POSIX semantics (Windows 10 1709 and later): the name moves to the new
    /// file at once, and a reader holding the old one keeps reading it, as a rename does on Unix.
    /// <see cref="File.Move(string, string, bool)"/> is <c>MoveFileEx</c>, which refuses to replace a file that
    /// any handle holds open, delete sharing or not, and a reader of a shared file may hold it at any moment.
    /// The old file's readers must share delete for this to succeed, which <see cref="OpenReadAsync"/> does.
    /// </summary>
    /// <returns>False where there is no POSIX rename (an older Windows, FAT, some network shares), for the
    /// caller to fall back on <see cref="File.Move(string, string, bool)"/>.</returns>
    [SupportedOSPlatform("windows")]
    private static unsafe bool TryReplaceWithPosixRename(string source, string destination)
    {
        using var handle = CreateFile(source, Delete | Synchronize, (uint)(FileShare.ReadWrite | FileShare.Delete), 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            throw Failure(Marshal.GetLastPInvokeError(), source);
        }

        // FILE_RENAME_INFO: Flags (a DWORD, in a union with a BOOLEAN), RootDirectory (a HANDLE, so the DWORD
        // before it is padded to pointer alignment), FileNameLength (a DWORD, in bytes), then the name, which
        // is a full path since there is no root directory.
        var name = Path.GetFullPath(destination);
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = name.Length * sizeof(char);
        var info = new byte[nameOffset + nameBytes + sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(info, RenameReplaceIfExists | RenamePosixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), (uint)nameBytes);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(info.AsSpan(nameOffset));

        fixed (byte* pInfo = info)
        {
            if (SetFileInformationByHandle(handle, FileRenameInfoEx, pInfo, (uint)info.Length))
            {
                return true;
            }
        }

        var error = Marshal.GetLastPInvokeError();
        return error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction
            ? false
            : throw Failure(error, destination);
    }

    // The exceptions File.Move raises for the same errors, so IsTransient reads both paths alike.
    private static Exception Failure(int error, string path) => error switch
    {
        ErrorAccessDenied => new UnauthorizedAccessException($"Access to the path '{path}' is denied."),
        ErrorFileNotFound => new FileNotFoundException(Marshal.GetPInvokeErrorMessage(error), path),
        ErrorPathNotFound => new DirectoryNotFoundException(Marshal.GetPInvokeErrorMessage(error)),
        _ => new IOException($"{Marshal.GetPInvokeErrorMessage(error)} : '{path}'", unchecked((int)0x80070000) | error)
    };

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, byte* fileInformation, uint bufferSize);
}

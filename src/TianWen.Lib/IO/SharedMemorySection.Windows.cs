using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TianWen.Lib.IO;

// The Windows half: a pagefile-backed section whose DACL grants the current user alone, mapped by address.
public sealed unsafe partial class SharedMemorySection
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;
    private const int ErrorAlreadyExists = 183;
    private const uint SddlRevision1 = 1;

    [SupportedOSPlatform("windows")]
    private static SharedMemorySection CreateWindows(string name, long capacity)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current user has no SID");
        // P: protected, so nothing is inherited; the one ACE grants this user everything, and nobody else anything.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", SddlRevision1, out var descriptor, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not build a shared memory section's security descriptor");
        }

        SectionHandle handle;
        try
        {
            var attributes = new SecurityAttributes { Length = sizeof(SecurityAttributes), SecurityDescriptor = descriptor };
            handle = CreateFileMappingW(-1, ref attributes, PageReadWrite, (uint)((ulong)capacity >> 32), (uint)capacity, name);
        }
        finally
        {
            LocalFree(descriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(error, $"Could not create a shared memory section of {capacity} bytes");
        }
        if (error == ErrorAlreadyExists)
        {
            handle.Dispose();
            throw new InvalidOperationException($"A shared memory section named {name} exists already");
        }
        return MappedWindows(name, capacity, handle, FileMapRead | FileMapWrite);
    }

    [SupportedOSPlatform("windows")]
    private static SharedMemorySection OpenWindows(string name, long capacity)
    {
        var handle = OpenFileMappingW(FileMapRead, inheritHandle: false, name);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open the shared memory section {name}");
        }
        return MappedWindows(name, capacity, handle, FileMapRead);
    }

    private static SharedMemorySection MappedWindows(string name, long capacity, SectionHandle handle, uint access)
    {
        var address = MapViewOfFile(handle, access, 0, 0, (nuint)capacity);
        if (address == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not map {capacity} bytes of the shared memory section {name}");
        }
        return new SharedMemorySection(name, capacity, (byte*)address, map: null, view: null, handle, unlinkOnDispose: false);
    }

    private static void UnmapWindows(byte* pointer) => UnmapViewOfFile((nint)pointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary>A section handle, closed with <c>CloseHandle</c>.</summary>
    private sealed class SectionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SectionHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint revision,
        out nint securityDescriptor, nint securityDescriptorSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SectionHandle CreateFileMappingW(nint file, ref SecurityAttributes attributes, uint protect,
        uint maximumSizeHigh, uint maximumSizeLow, string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SectionHandle OpenFileMappingW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(SectionHandle section, uint desiredAccess, uint fileOffsetHigh, uint fileOffsetLow, nuint numberOfBytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

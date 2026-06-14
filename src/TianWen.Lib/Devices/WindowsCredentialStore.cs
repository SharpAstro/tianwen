using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TianWen.Lib.Devices;

/// <summary>
/// <see cref="ICredentialStore"/> backed by the Windows Credential Manager (Generic credentials in
/// the per-user vault — visible and clearable under Control Panel -> Credential Manager). Targets
/// are namespaced as <c>TianWen/{key}</c>.
/// <para>
/// P/Invoke uses source-generated <see cref="LibraryImportAttribute"/> marshalling so it stays
/// NativeAOT-clean. The <c>CREDENTIAL</c> string fields are carried as <see cref="IntPtr"/> and
/// marshalled by hand — <c>LibraryImport</c> does not auto-marshal string fields inside structs,
/// so keeping the struct blittable is what lets it pass through the source generator.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    private static string TargetName(string key) => $"TianWen/{key}";

    public string? Get(string key)
    {
        if (!CredRead(TargetName(key), CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            // We write the blob as UTF-16; CredentialBlobSize is in bytes.
            var value = Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / sizeof(char));
            return string.IsNullOrEmpty(value) ? null : value;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Set(string key, string? value)
    {
        var target = TargetName(key);

        if (value is null)
        {
            // Best-effort delete; "not found" is the desired end state, so treat it as success.
            if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_NOT_FOUND)
                {
                    throw new Win32Exception(err);
                }
            }
            return;
        }

        var blob = Encoding.Unicode.GetBytes(value);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(Environment.UserName);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };

            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref CREDENTIAL credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    // String fields are IntPtr (LPWSTR) and marshalled by hand so the struct stays blittable for
    // the LibraryImport source generator. Layout mirrors the Win32 CREDENTIALW struct exactly.
    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}

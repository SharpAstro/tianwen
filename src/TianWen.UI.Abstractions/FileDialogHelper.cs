using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Launches a native file picker dialog.
/// On Windows, uses the modern IFileOpenDialog COM interface (Vista+).
/// On Linux/macOS, falls back to shell commands (zenity/kdialog/osascript).
/// Returns the selected path, or <c>null</c> if cancelled.
/// </summary>
public static partial class FileDialogHelper
{
    /// <summary>
    /// Shows a native open-file dialog filtered to the given file types.
    /// </summary>
    /// <param name="filters">
    /// Display name to extensions map, e.g. <c>{ "FITS files", [".fits", ".fit", ".fts"] }</c>.
    /// </param>
    /// <param name="title">Dialog title. Defaults to "Open file".</param>
    /// <param name="cancellationToken">Cancellation token (only effective on Linux/macOS process-based dialogs).</param>
    public static async Task<string?> PickAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string title = "Open file",
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return PickWindows(filters, title);
        }
        if (OperatingSystem.IsLinux())
        {
            return await PickLinuxAsync(filters, cancellationToken).ConfigureAwait(false);
        }
        if (OperatingSystem.IsMacOS())
        {
            return await PickMacOSAsync(filters, title, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // ── Windows: IFileOpenDialog COM interface (modern Vista+ dialog) ──

    [SupportedOSPlatform("windows")]
    private static string? PickWindows(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, string title)
    {
        var hr = CoCreateInstance(ref CLSID_FileOpenDialog, nint.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref IID_IFileOpenDialog, out var dialogPtr);
        if (hr < 0)
        {
            return null;
        }

        try
        {
            var vtbl = Marshal.ReadIntPtr(dialogPtr);

            // SetTitle (index 17)
            CallMethod<SetTitleDelegate>(vtbl, 17)(dialogPtr, title);

            // SetFileTypes (index 4)
            var specs = new COMDLG_FILTERSPEC[filters.Count];
            var pinned = new GCHandle[filters.Count * 2];
            var idx = 0;
            foreach (var kv in filters)
            {
                var name = kv.Key;
                var pattern = string.Join(';', kv.Value.Select(ext => "*" + ext));
                pinned[idx * 2] = GCHandle.Alloc(name, GCHandleType.Pinned);
                pinned[idx * 2 + 1] = GCHandle.Alloc(pattern, GCHandleType.Pinned);
                specs[idx] = new COMDLG_FILTERSPEC
                {
                    pszName = pinned[idx * 2].AddrOfPinnedObject(),
                    pszSpec = pinned[idx * 2 + 1].AddrOfPinnedObject(),
                };
                idx++;
            }

            try
            {
                CallMethod<SetFileTypesDelegate>(vtbl, 4)(dialogPtr, (uint)specs.Length, specs);

                // Show (index 3)
                hr = CallMethod<ShowDelegate>(vtbl, 3)(dialogPtr, nint.Zero);
                if (hr < 0)
                {
                    return null; // user cancelled or error
                }

                // GetResult (index 20)
                hr = CallMethod<GetResultDelegate>(vtbl, 20)(dialogPtr, out var shellItemPtr);
                if (hr < 0 || shellItemPtr == nint.Zero)
                {
                    return null;
                }

                try
                {
                    // IShellItem::GetDisplayName(SIGDN_FILESYSPATH = 0x80058000)
                    var shellItemVtbl = Marshal.ReadIntPtr(shellItemPtr);
                    hr = CallMethod<GetDisplayNameDelegate>(shellItemVtbl, 5)(shellItemPtr, 0x80058000, out var pathPtr);
                    if (hr < 0 || pathPtr == nint.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(pathPtr);
                    }
                    finally
                    {
                        CoTaskMemFree(pathPtr);
                    }
                }
                finally
                {
                    Marshal.Release(shellItemPtr);
                }
            }
            finally
            {
                foreach (var handle in pinned)
                {
                    if (handle.IsAllocated)
                    {
                        handle.Free();
                    }
                }
            }
        }
        finally
        {
            Marshal.Release(dialogPtr);
        }
    }

    private static TDelegate CallMethod<TDelegate>(nint vtbl, int slot) where TDelegate : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(Marshal.ReadIntPtr(vtbl, slot * nint.Size));
    }

    private static Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        public nint pszName;
        public nint pszSpec;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShowDelegate(nint self, nint hwndOwner);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFileTypesDelegate(nint self, uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetTitleDelegate(nint self, [MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResultDelegate(nint self, out nint ppsi);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDisplayNameDelegate(nint self, uint sigdnName, out nint ppszName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pv);

    // ── Linux: zenity / kdialog ──

    private static async Task<string?> PickLinuxAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        CancellationToken cancellationToken)
    {
        var zenityFilters = string.Join(' ',
            filters.Select(kv => $"--file-filter='{kv.Key} | {string.Join(' ', kv.Value.Select(ext => "*" + ext))}'"));

        var first = filters.First();
        var kdialogFilter = $"'{first.Key} ({string.Join(' ', first.Value.Select(ext => "*" + ext))})'";

        return await RunProcessAsync("zenity", $"--file-selection {zenityFilters}", cancellationToken).ConfigureAwait(false)
            ?? await RunProcessAsync("kdialog", $"--getopenfilename . {kdialogFilter}", cancellationToken).ConfigureAwait(false);
    }

    // ── macOS: osascript ──

    private static async Task<string?> PickMacOSAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        string title,
        CancellationToken cancellationToken)
    {
        var types = string.Join(", ",
            filters.Values.SelectMany(exts => exts).Select(ext => $"\"{ext.TrimStart('.')}\""));

        return await RunProcessAsync("osascript", $"-e 'POSIX path of (choose file of type {{{types}}} with prompt \"{title}\")'", cancellationToken).ConfigureAwait(false);
    }

    // ── Process helper (Linux/macOS) ──

    private static async Task<string?> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = (await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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
        var dialog = new FileOpenDialogClass();
        var fileDialog = (IFileOpenDialog)dialog;

        try
        {
            fileDialog.SetTitle(title);

            var specs = filters.Select(kv => new COMDLG_FILTERSPEC
            {
                pszName = Marshal.StringToCoTaskMemUni(kv.Key),
                pszSpec = Marshal.StringToCoTaskMemUni(string.Join(';', kv.Value.Select(ext => "*" + ext))),
            }).ToArray();

            var specSize = Marshal.SizeOf<COMDLG_FILTERSPEC>();
            var specsPtr = Marshal.AllocCoTaskMem(specSize * specs.Length);
            try
            {
                for (var i = 0; i < specs.Length; i++)
                {
                    Marshal.StructureToPtr(specs[i], specsPtr + i * specSize, false);
                }

                fileDialog.SetFileTypes((uint)specs.Length, specsPtr);

                var hr = fileDialog.Show(nint.Zero);
                if (hr < 0)
                {
                    return null;
                }

                fileDialog.GetResult(out var shellItem);
                try
                {
                    shellItem.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                    return path;
                }
                finally
                {
                    Marshal.ReleaseComObject(shellItem);
                }
            }
            finally
            {
                foreach (var spec in specs)
                {
                    Marshal.FreeCoTaskMem(spec.pszName);
                    Marshal.FreeCoTaskMem(spec.pszSpec);
                }
                Marshal.FreeCoTaskMem(specsPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    // ── COM interfaces for IFileOpenDialog ──

    [GeneratedComInterface]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SupportedOSPlatform("windows")]
    internal partial interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(nint hwndOwner);

        void SetFileTypes(uint cFileTypes, nint rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(nint pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(uint fos);

        void GetOptions(out uint pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, int fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close([MarshalAs(UnmanagedType.Error)] int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(nint pFilter);

        void GetResults(out nint ppenum);

        void GetSelectedItems(out nint ppsai);
    }

    [GeneratedComInterface]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SupportedOSPlatform("windows")]
    internal partial interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    internal enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COMDLG_FILTERSPEC
    {
        public nint pszName;
        public nint pszSpec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    [SupportedOSPlatform("windows")]
    private class FileOpenDialogClass
    {
    }

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

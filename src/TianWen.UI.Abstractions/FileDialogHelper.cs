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
/// On Windows, uses GetOpenFileName from comdlg32.dll.
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

    // ── Windows: GetOpenFileName from comdlg32.dll ──

    [SupportedOSPlatform("windows")]
    private static string? PickWindows(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, string title)
    {
        // Build null-separated filter string: "FITS files\0*.fits;*.fit;*.fts\0\0"
        var filterStr = string.Concat(
            filters.Select(kv => kv.Key + '\0' + string.Join(';', kv.Value.Select(ext => "*" + ext)) + '\0'))
            + '\0';

        const int maxPath = 260;
        var fileBuffer = Marshal.AllocHGlobal(maxPath * sizeof(char));
        Marshal.WriteInt16(fileBuffer, 0); // null-terminate

        try
        {
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                lpstrFilter = filterStr,
                lpstrFile = fileBuffer,
                nMaxFile = maxPath,
                lpstrTitle = title,
                Flags = 0x00080000 /* OFN_EXPLORER */ | 0x00001000 /* OFN_FILEMUSTEXIST */ | 0x00000800 /* OFN_PATHMUSTEXIST */,
            };

            if (!GetOpenFileName(ref ofn))
            {
                return null;
            }

            return Marshal.PtrToStringUni(fileBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public uint FlagsEx;
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
